using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Lambda.Core;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Drives Function.ScoreRowsAsync directly with a fake IBedrockInvoker, checking
/// that SEMIF_MODE=flat still returns the single-question response shape, and that
/// the default tree mode returns the combined-verdict shape instead.
/// </summary>
public class FunctionTests
{
    private sealed class FakeLogger : ILambdaLogger
    {
        public void Log(string message) { }
        public void LogLine(string message) { }
    }

    private sealed class FakeContext : ILambdaContext
    {
        public string AwsRequestId => "test";
        public IClientContext? ClientContext => null;
        public string FunctionName => "test";
        public string FunctionVersion => "1";
        public ICognitoIdentity? Identity => null;
        public string InvokedFunctionArn => "arn:test";
        public ILambdaLogger Logger { get; } = new FakeLogger();
        public string LogGroupName => "test";
        public string LogStreamName => "test";
        public int MemoryLimitInMB => 128;
        public TimeSpan RemainingTime => TimeSpan.FromSeconds(30);
    }

    private sealed class FakeInvoker(byte[] responseBody) : IBedrockInvoker
    {
        public Task<byte[]> InvokeModelAsync(
            string modelId, byte[] requestBody, CancellationToken cancellationToken = default) =>
            Task.FromResult(responseBody);
    }

    private static byte[] CompletionBody(Dictionary<string, double> letterLogprobs, long promptTokens = 50)
    {
        var top = new JsonObject();
        foreach (var (letter, logprob) in letterLogprobs)
        {
            top[letter] = logprob;
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

    [Fact]
    public async Task FlatModeReturnsTheOldSingleQuestionShape()
    {
        var row = JsonDocument.Parse("""
            {
              "id": "row-1",
              "state": {"stack_trace": "boom"},
              "question": "Bug or dependency?",
              "options": [
                {"id": "bug", "description": "Our code."},
                {"id": "downstream", "description": "Their service."}
              ]
            }
            """).RootElement;
        var fake = new FakeInvoker(CompletionBody(new Dictionary<string, double> { ["A"] = -0.1, ["B"] = -2.0 }));

        var response = await Function.ScoreRowsAsync(row, fake, "arn:model", "flat", new FakeContext());

        var result = Assert.Single(response.Results);
        Assert.Equal("row-1", result.Id);
        Assert.Equal(["bug", "downstream"], result.OptionIds);
        Assert.NotNull(result.Probabilities);
        Assert.NotNull(result.DeclaredMass);
        Assert.Null(result.Fallback);
        Assert.Null(result.Signals);
    }

    [Fact]
    public async Task TreeModeReturnsTheCombinedVerdictShape()
    {
        var row = JsonDocument.Parse("""{"id": "row-2", "state": {"stack_trace": "boom"}}""").RootElement;
        var fake = new FakeInvoker(
            CompletionBody(new Dictionary<string, double> { ["A"] = -0.2, ["B"] = -1.0, ["C"] = -1.5, ["D"] = -2.0 }));

        var response = await Function.ScoreRowsAsync(row, fake, "arn:model", "tree", new FakeContext());

        var result = Assert.Single(response.Results);
        Assert.Equal("row-2", result.Id);
        Assert.Equal(["bug", "downstream"], result.OptionIds);
        Assert.Equal(2, result.Probabilities!.Count);
        Assert.NotNull(result.Fallback);
        Assert.NotNull(result.Signals);
        Assert.Equal(7, result.Signals!.Count);
        Assert.Null(result.DeclaredMass);
    }
}
