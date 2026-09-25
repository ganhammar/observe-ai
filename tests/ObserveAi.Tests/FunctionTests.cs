using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Lambda.Core;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Drives the triage action through Function.DispatchAsync with a fake Invoke,
/// checking the combined-verdict response shape.
/// </summary>
[Collection("Environment")]
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
    public async Task TriageReturnsTheCombinedVerdictAtTheTopLevel()
    {
        var evt = JsonDocument.Parse(
            """{"action": "triage", "id": "row-2", "state": {"stack_trace": "boom"}}""").RootElement;
        var response = CompletionBody(new Dictionary<string, double> { ["A"] = -0.2, ["B"] = -1.0, ["C"] = -1.5, ["D"] = -2.0 });
        var deps = new Function.Dependencies(
            Invoke: (_, _, _) => Task.FromResult(response),
            Converse: (_, _, _, _) => throw new InvalidOperationException(),
            UpdateItem: (_, _) => throw new InvalidOperationException(),
            StartExecution: (_, _) => throw new InvalidOperationException(),
            GitHub: (_, _, _) => throw new InvalidOperationException());

        LambdaResponse verdict;
        Environment.SetEnvironmentVariable("MODEL_ARN", "arn:model");
        try
        {
            verdict = await Function.DispatchAsync(evt, deps, new FakeContext());
        }
        finally
        {
            Environment.SetEnvironmentVariable("MODEL_ARN", null);
        }

        Assert.Equal(["bug", "downstream"], verdict.OptionIds);
        Assert.Equal(2, verdict.Probabilities!.Count);
        Assert.NotNull(verdict.Fallback);
        Assert.Equal(Math.Exp(-0.2) + Math.Exp(-1.0), verdict.DeclaredMass!.Value, precision: 9);
        // Without evidence only the baseline is asked, so no signal answered.
        Assert.NotNull(verdict.Signals);
        Assert.Empty(verdict.Signals!);
    }
}
