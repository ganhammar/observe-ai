using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Ports the intent of tests/test_bedrock_backend.py: letter mapping, missing
/// options, abstention, declared_mass, and the empty-top_logprobs error, all
/// against a fake IBedrockInvoker rather than AWS. The response fixtures mix a
/// chat-shaped logprobs.content and a completion-shaped logprobs.top_logprobs
/// because FirstPositionLogprobs accepts either regardless of which request
/// shape was sent; only the completion shape is ever requested now.
/// </summary>
public class BedrockBackendTests
{
    private static JsonElement Row2() => Json("""
        {
          "id": "row-2",
          "state": {"stack_trace": "NullReferenceException at Foo.Bar"},
          "question": "Is this a bug or a downstream failure?",
          "options": [
            {"id": "bug", "description": "A defect in this service's own code."},
            {"id": "downstream", "description": "A failure in an external dependency."}
          ]
        }
        """);

    private static JsonElement Row4() => Json("""
        {
          "id": "row-4",
          "state": "evidence text",
          "question": "Which quadrant applies?",
          "options": [
            {"id": "a", "description": "First."},
            {"id": "b", "description": "Second."},
            {"id": "c", "description": "Third."},
            {"id": "d", "description": "Fourth."}
          ]
        }
        """);

    private static JsonElement Row4WithOptions(int count)
    {
        var row = JsonNode.Parse(Row4().GetRawText())!.AsObject();
        var options = row["options"]!.AsArray();
        while (options.Count > count)
        {
            options.RemoveAt(options.Count - 1);
        }
        return Json(row.ToJsonString());
    }

    private static JsonElement Row1() => Json("""
        {
          "id": "row-1",
          "state": {"service": "checkout", "stack_trace": "boom"},
          "question": "Bug or dependency?",
          "options": [
            {"id": "bug", "description": "Our code."},
            {"id": "downstream", "description": "Their service."}
          ]
        }
        """);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static byte[] ChatBody(
        (string Token, double Logprob)[] topLogprobs, string chosenToken, double chosenLogprob, long promptTokens = 41)
    {
        var top = new JsonArray(topLogprobs
            .Select(entry => (JsonNode)new JsonObject { ["token"] = entry.Token, ["logprob"] = entry.Logprob })
            .ToArray());
        var payload = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = chosenToken },
                ["logprobs"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["token"] = chosenToken,
                        ["logprob"] = chosenLogprob,
                        ["top_logprobs"] = top,
                    }),
                },
                ["finish_reason"] = "stop",
            }),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = promptTokens, ["completion_tokens"] = 1, ["total_tokens"] = promptTokens + 1,
            },
        };
        return Encoding.UTF8.GetBytes(payload.ToJsonString());
    }

    private static byte[] CompletionBody(Dictionary<string, double> mapping, long promptTokens = 51)
    {
        var top = new JsonObject();
        foreach (var (token, logprob) in mapping)
        {
            top[token] = logprob;
        }
        var payload = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["text"] = "A",
                ["logprobs"] = new JsonObject { ["top_logprobs"] = new JsonArray(top) },
                ["finish_reason"] = "stop",
            }),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = promptTokens, ["completion_tokens"] = 1, ["total_tokens"] = promptTokens + 1,
            },
        };
        return Encoding.UTF8.GetBytes(payload.ToJsonString());
    }

    private sealed class FakeBedrockInvoker(byte[] responseBody) : IBedrockInvoker
    {
        public List<(string ModelId, byte[] Body)> Calls { get; } = [];

        public Task<byte[]> InvokeModelAsync(
            string modelId, byte[] requestBody, CancellationToken cancellationToken = default)
        {
            Calls.Add((modelId, requestBody));
            return Task.FromResult(responseBody);
        }
    }

    [Fact]
    public async Task LetterMappingTwoOptions()
    {
        var client = new FakeBedrockInvoker(ChatBody([("A", -0.1), ("B", -2.5)], "A", -0.1));

        var result = await BedrockBackend.ScoreAsync(client, "arn:model", Row2(), constrain: false);

        Assert.Equal("row-2", result.Id);
        Assert.Equal(["bug", "downstream"], result.OptionIds);
        Assert.Equal([-0.1, -2.5], result.OptionLogprobs);
        Assert.Empty(result.MissingOptions);
        var expected = new[] { Math.Exp(-0.1), Math.Exp(-2.5) };
        var total = expected.Sum();
        expected = [expected[0] / total, expected[1] / total];
        Assert.Equal(expected[0], result.Probabilities[0], precision: 9);
        Assert.Equal(expected[1], result.Probabilities[1], precision: 9);
        Assert.Equal(1.0, result.Probabilities.Sum(), precision: 9);
        Assert.Equal(41, result.InputTokens);
        Assert.Equal("bedrock-direct-v1", result.PromptVersion);
        Assert.Equal(64, result.PromptSha256.Length);
    }

    [Fact]
    public async Task LetterMappingFourOptionsPreservesOrder()
    {
        var client = new FakeBedrockInvoker(
            ChatBody([("C", -0.2), ("A", -1.0), ("D", -3.0), ("B", -4.0)], "C", -0.2));

        var result = await BedrockBackend.ScoreAsync(client, "arn:model", Row4(), constrain: false);

        Assert.Equal(["a", "b", "c", "d"], result.OptionIds);
        Assert.Equal([-1.0, -4.0, -0.2, -3.0], result.OptionLogprobs);
        Assert.Empty(result.MissingOptions);
        var maxIndex = result.Probabilities
            .Select((value, index) => (value, index))
            .OrderByDescending(entry => entry.value)
            .First().index;
        Assert.Equal(2, maxIndex);
    }

    [Fact]
    public async Task MissingOptionLetterIsRecordedAndZeroed()
    {
        var row = Row4WithOptions(3); // a, b, c only
        var client = new FakeBedrockInvoker(ChatBody([("A", -0.5), ("B", -1.5), ("X", -3.0)], "A", -0.5));

        var result = await BedrockBackend.ScoreAsync(client, "arn:model", row, constrain: false);

        Assert.Equal(["c"], result.MissingOptions);
        Assert.Equal(double.NegativeInfinity, result.OptionLogprobs[2]);
        Assert.Equal(0.0, result.Probabilities[2]);
        var expectedAb = new[] { Math.Exp(-0.5), Math.Exp(-1.5) };
        var total = expectedAb.Sum();
        Assert.Equal(expectedAb[0] / total, result.Probabilities[0], precision: 9);
        Assert.Equal(expectedAb[1] / total, result.Probabilities[1], precision: 9);
        Assert.Equal(Math.Exp(-0.5) + Math.Exp(-1.5), result.DeclaredMass, precision: 9);
        Assert.False(result.Abstained);
    }

    [Fact]
    public async Task AbstainedTrueWhenEveryOptionLetterIsMissing()
    {
        var client = new FakeBedrockInvoker(ChatBody([("Based", -0.1), ("on", -1.0)], "Based", -0.1));

        var result = await BedrockBackend.ScoreAsync(client, "arn:model", Row2(), constrain: false);

        Assert.Equal(["bug", "downstream"], result.MissingOptions);
        Assert.True(result.Abstained);
        Assert.Equal(0.0, result.DeclaredMass);
        Assert.Equal([0.0, 0.0], result.Probabilities);
    }

    [Fact]
    public async Task EmptyTopLogprobsRaisesClearError()
    {
        var payload = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "" },
                ["logprobs"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["token"] = "", ["logprob"] = 0.0, ["top_logprobs"] = new JsonArray(),
                    }),
                },
                ["finish_reason"] = "stop",
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 1, ["total_tokens"] = 11 },
        };
        var client = new FakeBedrockInvoker(Encoding.UTF8.GetBytes(payload.ToJsonString()));

        var error = await Assert.ThrowsAsync<RowValidationException>(
            () => BedrockBackend.ScoreAsync(client, "arn:model", Row2(), constrain: false));
        Assert.Contains("no candidate tokens", error.Message);
    }

    [Fact]
    public async Task DeclaredMassIsLowWhenModelPrefersOtherTokens()
    {
        var client = new FakeBedrockInvoker(
            ChatBody([("Based", -0.05), ("A", -6.0), ("B", -7.0)], "Based", -0.05));

        var result = await BedrockBackend.ScoreAsync(client, "arn:model", Row2(), constrain: false);

        Assert.Equal(Math.Exp(-6.0) + Math.Exp(-7.0), result.DeclaredMass, precision: 9);
        Assert.True(result.DeclaredMass < 0.01);
        Assert.Equal("Based", result.TopToken.Token);
        Assert.Equal(Math.Exp(-0.05), result.TopToken.Probability, precision: 9);
    }

    [Fact]
    public async Task ConstrainTrueSetsStructuredOutputs()
    {
        var client = new FakeBedrockInvoker(ChatBody([("A", -0.1), ("B", -2.5)], "A", -0.1));

        await BedrockBackend.ScoreAsync(client, "arn:model", Row2(), constrain: true);

        var sentBody = JsonDocument.Parse(client.Calls[0].Body).RootElement;
        var choice = sentBody.GetProperty("structured_outputs").GetProperty("choice")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["A", "B"], choice);
    }

    [Fact]
    public async Task ConstrainFalseOmitsStructuredOutputs()
    {
        var client = new FakeBedrockInvoker(ChatBody([("A", -0.1), ("B", -2.5)], "A", -0.1));

        await BedrockBackend.ScoreAsync(client, "arn:model", Row2(), constrain: false);

        var sentBody = JsonDocument.Parse(client.Calls[0].Body).RootElement;
        Assert.False(sentBody.TryGetProperty("structured_outputs", out _));
    }

    [Fact]
    public async Task TooManyOptionsRaisesWithoutCallingBedrock()
    {
        var client = new FakeBedrockInvoker(ChatBody([("A", -0.1)], "A", -0.1));

        var error = await Assert.ThrowsAsync<RowValidationException>(
            () => BedrockBackend.ScoreAsync(client, "arn:model", Row4(), topLogprobs: 2));
        Assert.Contains("top_logprobs", error.Message);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task InvalidRowRaisesBeforeCallingBedrock()
    {
        var client = new FakeBedrockInvoker(ChatBody([("A", -0.1)], "A", -0.1));
        var badRow = Row4WithOptions(1);

        var error = await Assert.ThrowsAsync<RowValidationException>(
            () => BedrockBackend.ScoreAsync(client, "arn:model", badRow));
        Assert.Contains("2-16", error.Message);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task CompletionApiSendsRenderedPromptNotMessages()
    {
        var client = new FakeBedrockInvoker(CompletionBody(new Dictionary<string, double> { ["A"] = -0.2, ["B"] = -1.7 }));

        var result = await BedrockBackend.ScoreAsync(client, "arn:model", Row1());

        var sentBody = JsonDocument.Parse(client.Calls[0].Body).RootElement;
        Assert.True(sentBody.TryGetProperty("prompt", out var prompt));
        Assert.False(sentBody.TryGetProperty("messages", out _));
        Assert.EndsWith("<|im_start|>assistant\n<think>\n\n</think>\n\n", prompt.GetString(), StringComparison.Ordinal);
        Assert.Equal(1, sentBody.GetProperty("max_tokens").GetInt32());
        Assert.Equal(["bug", "downstream"], result.OptionIds);
        Assert.True(result.Probabilities[0] > result.Probabilities[1]);
    }

    [Fact]
    public async Task CompletionPromptHashMatchesRenderedPrompt()
    {
        var client = new FakeBedrockInvoker(CompletionBody(new Dictionary<string, double> { ["A"] = -0.2, ["B"] = -1.7 }));

        var result = await BedrockBackend.ScoreAsync(client, "arn:model", Row1());

        var sentBody = JsonDocument.Parse(client.Calls[0].Body).RootElement;
        var prompt = sentBody.GetProperty("prompt").GetString()!;
        Assert.Equal(Semif.Digest(prompt), result.PromptSha256);
    }

    [Fact]
    public async Task MissingLogprobsRaisesActionableError()
    {
        var payload = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0, ["text"] = "A", ["logprobs"] = null, ["finish_reason"] = "stop",
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 51, ["completion_tokens"] = 1, ["total_tokens"] = 52 },
        };
        var client = new FakeBedrockInvoker(Encoding.UTF8.GetBytes(payload.ToJsonString()));

        var error = await Assert.ThrowsAsync<RowValidationException>(
            () => BedrockBackend.ScoreAsync(client, "arn:model", Row1()));
        Assert.Contains("no logprobs", error.Message);
    }

    [Fact]
    public async Task DeclaredMassIsLowWhenOtherTokensDominate()
    {
        // Reasoning token holds most of the mass; the option letters are a thin tail.
        var client = new FakeBedrockInvoker(CompletionBody(new Dictionary<string, double>
        {
            ["<think>"] = Math.Log(0.94), ["A"] = Math.Log(0.04), ["B"] = Math.Log(0.02),
        }));

        var result = await BedrockBackend.ScoreAsync(client, "arn:model", Row1(), constrain: false);

        Assert.Equal(0.06, result.DeclaredMass, precision: 6);
        Assert.Equal("<think>", result.TopToken.Token);
        // Renormalisation still reports a confident-looking split off that thin tail.
        Assert.Equal(2.0 / 3.0, result.Probabilities[0], precision: 6);
    }

    [Fact]
    public async Task CompletionSendsLogprobsAsAnIntegerCount()
    {
        var client = new FakeBedrockInvoker(CompletionBody(new Dictionary<string, double> { ["A"] = -0.2, ["B"] = -1.7 }));

        await BedrockBackend.ScoreAsync(client, "arn:model", Row1(), topLogprobs: 20);

        var sentBody = JsonDocument.Parse(client.Calls[0].Body).RootElement;
        Assert.Equal(20, sentBody.GetProperty("logprobs").GetInt32());
        Assert.False(sentBody.TryGetProperty("top_logprobs", out _));
    }

}
