using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Checks Function.DispatchAsync's routing: each action reaches its own stage,
/// a missing action still triages (nothing already deployed breaks), and an
/// unknown action is an error rather than a silent no-op.
/// </summary>
public class DispatchTests
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
        public Task<byte[]> InvokeModelAsync(string modelId, byte[] requestBody, CancellationToken cancellationToken = default) =>
            Task.FromResult(responseBody);
    }

    private sealed class NeverCalledInvoker : IBedrockInvoker
    {
        public Task<byte[]> InvokeModelAsync(string modelId, byte[] requestBody, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Bedrock must not be called for this request");
    }

    private static JsonElement Parse(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement;

    private const string DotnetTrace = """
        System.IndexOutOfRangeException: Index was outside the bounds of the array.
           at Pricing.Tiers.TierResolver.Resolve(Int32 index) in src/Pricing/Tiers/TierResolver.cs:line 42
        """;

    [Fact]
    public async Task IdentifyActionReachesTheIdentifyStage()
    {
        var evt = Parse(new JsonObject
        {
            ["action"] = "identify",
            ["logGroupName"] = "/aws/lambda/checkout-api",
            ["message"] = DotnetTrace,
        });

        var response = await Function.DispatchAsync(evt, new NeverCalledInvoker(), "arn:model", "tree", "completion", new FakeContext());

        Assert.NotNull(response.Identify);
        Assert.True(response.Identify!.Parsed);
        Assert.Equal("dotnet", response.Identify.Runtime);
        Assert.Null(response.Results);
        Assert.Null(response.Escalate);
    }

    [Fact]
    public async Task AMissingActionStillTriages()
    {
        var row = Parse(new JsonObject
        {
            ["id"] = "row-1",
            ["state"] = new JsonObject { ["x"] = 1 },
            ["question"] = "Bug or downstream?",
            ["options"] = new JsonArray(
                new JsonObject { ["id"] = "bug", ["description"] = "Our code." },
                new JsonObject { ["id"] = "downstream", ["description"] = "Their service." }),
        });
        var fake = new FakeInvoker(CompletionBody(new Dictionary<string, double> { ["A"] = -0.1, ["B"] = -2.0 }));

        var response = await Function.DispatchAsync(row, fake, "arn:model", "flat", "completion", new FakeContext());

        var result = Assert.Single(response.Results!);
        Assert.Equal("row-1", result.Id);
        Assert.Null(response.Identify);
        Assert.Null(response.Escalate);
    }

    [Fact]
    public async Task EscalateActionReachesTheEscalateStage()
    {
        var evt = Parse(new JsonObject
        {
            ["action"] = "escalate",
            ["repo"] = "acme/catalog",
            ["commitish"] = "abc1234",
            ["rawTrace"] = DotnetTrace,
            ["trace"] = new JsonObject
            {
                ["runtime"] = "dotnet",
                ["exceptionType"] = "System.IndexOutOfRangeException",
                ["frames"] = new JsonArray(
                    new JsonObject { ["method"] = "Pricing.Tiers.TierResolver.Resolve", ["inApp"] = true }),
            },
            ["verdict"] = new JsonObject { ["bug"] = 0.9, ["downstream"] = 0.1, ["fallback"] = false },
        });

        // No frame carries a fetched "source", so this must never call Bedrock.
        var response = await Function.DispatchAsync(evt, new NeverCalledInvoker(), "arn:model", "tree", "completion", new FakeContext());

        Assert.NotNull(response.Escalate);
        Assert.NotEmpty(response.Escalate!.FetchCommands);
        Assert.Empty(response.Escalate.FrameVerdicts);
        Assert.Null(response.Results);
        Assert.Null(response.Identify);
    }

    /// <summary>A cache hit resolves the repo without ever reaching the (unimplemented) model step.</summary>
    [Fact]
    public async Task ResolveRepoActionReachesTheResolveRepoStage()
    {
        Environment.SetEnvironmentVariable("NAMESPACE_CACHE_TABLE", "namespace-cache");
        Environment.SetEnvironmentVariable("AWS_REGION", "eu-central-1");
        try
        {
            var evt = Parse(new JsonObject
            {
                ["action"] = "resolve-repo",
                ["logGroupName"] = "/aws/lambda/checkout-api",
                ["namespacePrefix"] = "Checkout.",
            });
            RepoResolver.ReadCache readCache = (_, _) => Task.FromResult(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue> { ["repo"] = new("acme/checkout") },
            });
            RepoResolver.WriteCache writeCache = (_, _) => throw new InvalidOperationException("a cache hit must not write back");

            var response = await Function.DispatchAsync(
                evt, new NeverCalledInvoker(), "arn:model", "tree", "completion", new FakeContext(),
                readCache: readCache, writeCache: writeCache);

            Assert.Equal("acme/checkout", response.Repo);
            Assert.Equal("Cache", response.ResolvedBy);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NAMESPACE_CACHE_TABLE", null);
            Environment.SetEnvironmentVariable("AWS_REGION", null);
        }
    }

    [Fact]
    public async Task ResolveRepoActionReturnsAnAbsentRepoRatherThanThrowingWhenTheModelIsUnimplemented()
    {
        Environment.SetEnvironmentVariable("NAMESPACE_CACHE_TABLE", "namespace-cache");
        Environment.SetEnvironmentVariable("AWS_REGION", "eu-central-1");
        try
        {
            var evt = Parse(new JsonObject
            {
                ["action"] = "resolve-repo",
                ["logGroupName"] = "/aws/lambda/mystery-service",
                ["namespacePrefix"] = "Mystery.",
            });
            // No tag, and this namespace is not in the cache, so ResolveAsync falls
            // through to RepoResolver.ResolveWithModelAsync, which is not built yet.
            RepoResolver.ReadCache readCache = (_, _) => Task.FromResult(new GetItemResponse());
            RepoResolver.WriteCache writeCache = (_, _) => throw new InvalidOperationException("the model never returns, so nothing is cached");

            var response = await Function.DispatchAsync(
                evt, new NeverCalledInvoker(), "arn:model", "tree", "completion", new FakeContext(),
                readCache: readCache, writeCache: writeCache);

            Assert.Null(response.Repo);
            Assert.Null(response.ResolvedBy);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NAMESPACE_CACHE_TABLE", null);
            Environment.SetEnvironmentVariable("AWS_REGION", null);
        }
    }

    [Fact]
    public async Task CheckRateActionReachesTheCheckRateStageUnderBothLimits()
    {
        Environment.SetEnvironmentVariable("RATE_TABLE", "rate-table");
        Environment.SetEnvironmentVariable("ISSUES_PER_REPO_PER_HOUR", "5");
        Environment.SetEnvironmentVariable("ISSUES_PER_HOUR", "20");
        try
        {
            var evt = Parse(new JsonObject { ["action"] = "check-rate", ["repo"] = "acme/orders" });
            Caps.UpdateItem updateRate = (_, _) => Task.FromResult(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue> { ["occurrences"] = new() { N = "1" } },
            });

            var response = await Function.DispatchAsync(
                evt, new NeverCalledInvoker(), "arn:model", "tree", "completion", new FakeContext(), updateRate: updateRate);

            Assert.True(response.Allowed);
            Assert.False(response.Tripped);
            Assert.Equal(1, response.Count);
            Assert.Equal(5, response.Limit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RATE_TABLE", null);
            Environment.SetEnvironmentVariable("ISSUES_PER_REPO_PER_HOUR", null);
            Environment.SetEnvironmentVariable("ISSUES_PER_HOUR", null);
        }
    }

    [Fact]
    public async Task CheckRateActionSurfacesNotAllowedWhenOverTheRepoLimit()
    {
        Environment.SetEnvironmentVariable("RATE_TABLE", "rate-table");
        Environment.SetEnvironmentVariable("ISSUES_PER_REPO_PER_HOUR", "5");
        Environment.SetEnvironmentVariable("ISSUES_PER_HOUR", "100");
        try
        {
            var evt = Parse(new JsonObject { ["action"] = "check-rate", ["repo"] = "acme/orders" });
            Caps.UpdateItem updateRate = (_, _) => Task.FromResult(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue> { ["occurrences"] = new() { N = "7" } },
            });

            var response = await Function.DispatchAsync(
                evt, new NeverCalledInvoker(), "arn:model", "tree", "completion", new FakeContext(), updateRate: updateRate);

            Assert.False(response.Allowed);
            Assert.False(response.Tripped);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RATE_TABLE", null);
            Environment.SetEnvironmentVariable("ISSUES_PER_REPO_PER_HOUR", null);
            Environment.SetEnvironmentVariable("ISSUES_PER_HOUR", null);
        }
    }

    [Fact]
    public async Task CheckRateActionSurfacesTrippedWhenTheGlobalBreakerTrips()
    {
        Environment.SetEnvironmentVariable("RATE_TABLE", "rate-table");
        Environment.SetEnvironmentVariable("ISSUES_PER_REPO_PER_HOUR", "5");
        Environment.SetEnvironmentVariable("ISSUES_PER_HOUR", "3");
        try
        {
            var evt = Parse(new JsonObject { ["action"] = "check-rate", ["repo"] = "acme/orders" });
            Caps.UpdateItem updateRate = (_, _) => Task.FromResult(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue> { ["occurrences"] = new() { N = "4" } },
            });

            var response = await Function.DispatchAsync(
                evt, new NeverCalledInvoker(), "arn:model", "tree", "completion", new FakeContext(), updateRate: updateRate);

            Assert.True(response.Tripped);
            Assert.True(response.Allowed);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RATE_TABLE", null);
            Environment.SetEnvironmentVariable("ISSUES_PER_REPO_PER_HOUR", null);
            Environment.SetEnvironmentVariable("ISSUES_PER_HOUR", null);
        }
    }

    [Fact]
    public async Task AnUnknownActionIsAnError()
    {
        var evt = Parse(new JsonObject { ["action"] = "bogus" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Function.DispatchAsync(evt, new NeverCalledInvoker(), "arn:model", "tree", "completion", new FakeContext()));
    }

    private static byte[] CompletionBody(Dictionary<string, double> letterLogprobs)
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
            ["usage"] = new JsonObject { ["prompt_tokens"] = 50, ["completion_tokens"] = 1, ["total_tokens"] = 51 },
        };
        return Encoding.UTF8.GetBytes(payload.ToJsonString());
    }
}
