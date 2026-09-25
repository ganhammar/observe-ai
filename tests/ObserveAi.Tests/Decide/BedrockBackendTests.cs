using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Ports the intent of tests/test_bedrock_backend.py: letter mapping, missing
/// options, declared_mass, and the empty and missing logprobs errors, all
/// against a fake Invoke rather than AWS.
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

    private static byte[] CompletionBody(Dictionary<string, double> mapping)
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
            ["usage"] = new JsonObject { ["prompt_tokens"] = 51, ["completion_tokens"] = 1, ["total_tokens"] = 52 },
        };
        return Encoding.UTF8.GetBytes(payload.ToJsonString());
    }

    /// <summary>An Invoke that answers every request with response, recording each request body in calls.</summary>
    private static BedrockBackend.Invoke Returning(byte[] response, List<byte[]>? calls = null) => (_, body, _) =>
    {
        calls?.Add(body);
        return Task.FromResult(response);
    };

    [Fact]
    public async Task LetterMappingTwoOptions()
    {
        var invoke = Returning(CompletionBody(new() { ["A"] = -0.1, ["B"] = -2.5 }));

        var result = await BedrockBackend.ScoreAsync(invoke, "arn:model", Row2());

        Assert.Equal(["bug", "downstream"], result.OptionIds);
        var expected = new[] { Math.Exp(-0.1), Math.Exp(-2.5) };
        var total = expected.Sum();
        expected = [expected[0] / total, expected[1] / total];
        Assert.Equal(expected[0], result.Probabilities[0], precision: 9);
        Assert.Equal(expected[1], result.Probabilities[1], precision: 9);
        Assert.Equal(1.0, result.Probabilities.Sum(), precision: 9);
    }

    [Fact]
    public async Task LetterMappingFourOptionsPreservesOrder()
    {
        var invoke = Returning(
            CompletionBody(new() { ["C"] = -0.2, ["A"] = -1.0, ["D"] = -3.0, ["B"] = -4.0 }));

        var result = await BedrockBackend.ScoreAsync(invoke, "arn:model", Row4());

        Assert.Equal(["a", "b", "c", "d"], result.OptionIds);
        var maxIndex = result.Probabilities
            .Select((value, index) => (value, index))
            .OrderByDescending(entry => entry.value)
            .First().index;
        Assert.Equal(2, maxIndex);
    }

    [Fact]
    public async Task MissingOptionLetterIsZeroed()
    {
        var row = Row4WithOptions(3); // Only options a, b, and c remain.
        var invoke = Returning(CompletionBody(new() { ["A"] = -0.5, ["B"] = -1.5, ["X"] = -3.0 }));

        var result = await BedrockBackend.ScoreAsync(invoke, "arn:model", row);

        Assert.Equal(0.0, result.Probabilities[2]);
        var expectedAb = new[] { Math.Exp(-0.5), Math.Exp(-1.5) };
        var total = expectedAb.Sum();
        Assert.Equal(expectedAb[0] / total, result.Probabilities[0], precision: 9);
        Assert.Equal(expectedAb[1] / total, result.Probabilities[1], precision: 9);
        Assert.Equal(Math.Exp(-0.5) + Math.Exp(-1.5), result.DeclaredMass, precision: 9);
    }

    [Fact]
    public async Task EveryOptionLetterMissingGivesZeroMassAndZeroProbabilities()
    {
        var invoke = Returning(CompletionBody(new() { ["Based"] = -0.1, ["on"] = -1.0 }));

        var result = await BedrockBackend.ScoreAsync(invoke, "arn:model", Row2());

        Assert.Equal(0.0, result.DeclaredMass);
        Assert.Equal([0.0, 0.0], result.Probabilities);
    }

    [Fact]
    public async Task EmptyTopLogprobsRaisesClearError()
    {
        var invoke = Returning(CompletionBody([]));

        var error = await Assert.ThrowsAsync<RowValidationException>(
            () => BedrockBackend.ScoreAsync(invoke, "arn:model", Row2()));
        Assert.Contains("no candidate tokens", error.Message);
    }

    [Fact]
    public async Task InvalidRowRaisesBeforeCallingBedrock()
    {
        var calls = new List<byte[]>();
        var invoke = Returning(CompletionBody(new() { ["A"] = -0.1 }), calls);
        var badRow = Row4WithOptions(1);

        var error = await Assert.ThrowsAsync<RowValidationException>(
            () => BedrockBackend.ScoreAsync(invoke, "arn:model", badRow));
        Assert.Contains("2-16", error.Message);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task CompletionApiSendsRenderedPromptNotMessages()
    {
        var calls = new List<byte[]>();
        var invoke = Returning(CompletionBody(new() { ["A"] = -0.2, ["B"] = -1.7 }), calls);

        var result = await BedrockBackend.ScoreAsync(invoke, "arn:model", Row1());

        var sentBody = JsonDocument.Parse(calls[0]).RootElement;
        Assert.True(sentBody.TryGetProperty("prompt", out var prompt));
        Assert.False(sentBody.TryGetProperty("messages", out _));
        Assert.EndsWith("<|im_start|>assistant\n<think>\n\n</think>\n\n", prompt.GetString(), StringComparison.Ordinal);
        Assert.Equal(1, sentBody.GetProperty("max_tokens").GetInt32());
        Assert.Equal(["bug", "downstream"], result.OptionIds);
        Assert.True(result.Probabilities[0] > result.Probabilities[1]);
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
        var invoke = Returning(Encoding.UTF8.GetBytes(payload.ToJsonString()));

        var error = await Assert.ThrowsAsync<RowValidationException>(
            () => BedrockBackend.ScoreAsync(invoke, "arn:model", Row1()));
        Assert.Contains("no logprobs", error.Message);
    }

    [Fact]
    public async Task DeclaredMassIsLowWhenOtherTokensDominate()
    {
        // Reasoning token holds most of the mass; the option letters are a thin tail.
        var invoke = Returning(CompletionBody(new()
        {
            ["<think>"] = Math.Log(0.94), ["A"] = Math.Log(0.04), ["B"] = Math.Log(0.02),
        }));

        var result = await BedrockBackend.ScoreAsync(invoke, "arn:model", Row1());

        Assert.Equal(0.06, result.DeclaredMass, precision: 6);
        // Renormalisation still reports a confident-looking split off that thin tail.
        Assert.Equal(2.0 / 3.0, result.Probabilities[0], precision: 6);
    }

    [Fact]
    public async Task CompletionSendsLogprobsAsAnIntegerCount()
    {
        var calls = new List<byte[]>();
        var invoke = Returning(CompletionBody(new() { ["A"] = -0.2, ["B"] = -1.7 }), calls);

        await BedrockBackend.ScoreAsync(invoke, "arn:model", Row1());

        var sentBody = JsonDocument.Parse(calls[0]).RootElement;
        Assert.Equal(20, sentBody.GetProperty("logprobs").GetInt32());
        Assert.False(sentBody.TryGetProperty("top_logprobs", out _));
    }
}
