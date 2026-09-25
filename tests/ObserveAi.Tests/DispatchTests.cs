using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.StepFunctions.Model;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Checks Function.DispatchAsync's routing: each action reaches its own stage,
/// a Records array routes to start-executions ahead of the action switch since
/// a Kinesis event carries no action field, and a missing or unknown action is
/// an error rather than a silent no-op.
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
    public async Task AMissingActionIsAnError()
    {
        var row = Parse(new JsonObject { ["id"] = "row-1", ["state"] = new JsonObject { ["stack_trace"] = "boom" } });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Function.DispatchAsync(row, new NeverCalledInvoker(), "arn:model", new FakeContext()));
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
            ["occurrences"] = 3,
            ["firstSeen"] = "2026-09-01T08:00:00+00:00",
        });

        // Nothing fetches, so no frame is verified and the readout is never called;
        // the diagnosis still runs, from the trace alone.
        Environment.SetEnvironmentVariable("DIAGNOSIS_MODEL_ID", "eu.model");
        var prompts = new List<string>();
        LambdaResponse response;
        try
        {
            response = await Function.DispatchAsync(
                evt, new NeverCalledInvoker(), "arn:model", new FakeContext(),
                getSource: (_, _) => Task.FromResult<string?>(null),
                converse: (_, _, user, _) => { prompts.Add(user); return Task.FromResult("Index past the end."); });
        }
        finally
        {
            Environment.SetEnvironmentVariable("DIAGNOSIS_MODEL_ID", null);
        }

        Assert.NotNull(response.Escalate);
        Assert.NotEmpty(response.Escalate!.FetchRequests);
        Assert.Empty(response.Escalate.FrameVerdicts);
        Assert.Contains("Index past the end.", response.Escalate.Draft.Body);
        Assert.Contains("Seen 3 times, first on 2026-09-01.", response.Escalate.Draft.Body);
        Assert.Single(prompts);
        Assert.Null(response.Probabilities);
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
                evt, new NeverCalledInvoker(), "arn:model", new FakeContext(), updateRate: updateRate);

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
                evt, new NeverCalledInvoker(), "arn:model", new FakeContext(), updateRate: updateRate);

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
                evt, new NeverCalledInvoker(), "arn:model", new FakeContext(), updateRate: updateRate);

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
            () => Function.DispatchAsync(evt, new NeverCalledInvoker(), "arn:model", new FakeContext()));
    }

    private const string PipelineArn = "arn:aws:states:eu-central-1:1:stateMachine:pipeline";

    private const string FirstSeen = "2026-09-01T08:00:00.0000000+00:00";

    /// <summary>A SeenTable backing store that counts per key and reports a fixed first sighting.</summary>
    private static SeenStore.UpdateItem SeenCounter(Dictionary<string, long> store) => (request, _) =>
    {
        var key = $"{request.Key["repo"].S}/{request.Key["fingerprint"].S}";
        store[key] = store.GetValueOrDefault(key) + 1;
        return Task.FromResult(new UpdateItemResponse
        {
            Attributes = new Dictionary<string, AttributeValue>
            {
                ["occurrences"] = new() { N = store[key].ToString() },
                ["first_seen"] = new(FirstSeen),
            },
        });
    };

    /// <summary>
    /// One batch exercising every start-executions outcome at once: an
    /// unparseable line, a parseable one in a log group with no repo
    /// convention, a first sighting, and a repeat of that same sighting. The
    /// first sighting's execution is named after its own sanitised id.
    /// </summary>
    [Fact]
    public async Task StartExecutionsCountsEveryOutcomeAndNamesTheExecutionAfterTheLogEventId()
    {
        Environment.SetEnvironmentVariable("PIPELINE_ARN", PipelineArn);
        Environment.SetEnvironmentVariable("GITHUB_ORG", "acme");
        Environment.SetEnvironmentVariable("SEEN_TABLE", "seen-table");
        try
        {
            var evt = Parse(new JsonObject
            {
                ["Records"] = new JsonArray(
                    new JsonObject
                    {
                        ["kinesis"] = new JsonObject
                        {
                            ["data"] = KinesisEnvelope(
                                "/aws/lambda/checkout-api",
                                ("1", "nothing here looks like a stack trace"),
                                ("2:with/slash", DotnetTrace),
                                ("3", DotnetTrace)),
                        },
                    },
                    new JsonObject { ["kinesis"] = new JsonObject { ["data"] = KinesisEnvelope("some-custom-log-group", DotnetTrace) } }),
            });
            var started = new List<StartExecutionRequest>();
            Function.StartExecution startExecution = (request, _) => { started.Add(request); return Task.FromResult(new StartExecutionResponse()); };

            var response = await Function.DispatchAsync(
                evt, new NeverCalledInvoker(), "arn:model", new FakeContext(),
                startExecution: startExecution, recordSeen: SeenCounter([]));

            Assert.Equal(1, response.Started);
            Assert.Equal(1, response.AlreadyKnown);
            Assert.Equal(1, response.Unparseable);
            Assert.Equal(1, response.NoRepo);
            var request = Assert.Single(started);
            Assert.Equal(PipelineArn, request.StateMachineArn);
            Assert.Equal("2_with_slash", request.Name);
            Assert.Contains("\"repo\":\"acme/checkout-api\"", request.Input);
            Assert.Contains("\"occurrences\":1", request.Input);
            Assert.Contains("\"firstSeen\":\"2026-09-01T08:00:00+00:00\"", request.Input);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PIPELINE_ARN", null);
            Environment.SetEnvironmentVariable("GITHUB_ORG", null);
            Environment.SetEnvironmentVariable("SEEN_TABLE", null);
        }
    }

    [Fact]
    public async Task FileIssueActionReadsTheSecretAndFilesThroughIssueFiler()
    {
        Environment.SetEnvironmentVariable("GITHUB_TOKEN_SECRET_ARN", "arn:aws:secretsmanager:eu-central-1:1:secret:github-token");
        try
        {
            var evt = Parse(new JsonObject
            {
                ["action"] = "file-issue",
                ["repo"] = "acme/orders",
                ["title"] = "NullReferenceException in Orders.Billing.Build",
                ["body"] = "Body text.",
            });
            var secretRequests = new List<string>();
            Function.ReadSecret readSecret = (arn, _) =>
            {
                secretRequests.Add(arn);
                return Task.FromResult("ghp_faketoken");
            };
            var calls = new List<(string Url, string Body)>();
            IssueFiler.CallGitHub callGitHub = (url, body, _) =>
            {
                calls.Add((url, body));
                return Task.FromResult(url.Contains("state=open") ? "[]" : """{"number":99}""");
            };

            var response = await Function.DispatchAsync(
                evt, new NeverCalledInvoker(), "arn:model", new FakeContext(),
                readSecret: readSecret, callGitHub: callGitHub);

            Assert.Equal(["arn:aws:secretsmanager:eu-central-1:1:secret:github-token"], secretRequests);
            Assert.Equal("created", response.Outcome);
            Assert.Equal(99, response.IssueNumber);
            Assert.Equal(2, calls.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN_SECRET_ARN", null);
        }
    }

    /// <summary>Builds the gzipped base64 envelope a CloudWatch Logs subscription delivers as one Kinesis record's data.</summary>
    private static string KinesisEnvelope(string logGroup, params string[] messages) =>
        KinesisEnvelope(logGroup, messages.Select((message, i) => (i.ToString(), message)).ToArray());

    /// <summary>As above, with an explicit id per log event rather than one assigned by index.</summary>
    private static string KinesisEnvelope(string logGroup, params (string Id, string Message)[] events)
    {
        var envelope = new JsonObject
        {
            ["messageType"] = "DATA_MESSAGE",
            ["logGroup"] = logGroup,
            ["logEvents"] = new JsonArray(events
                .Select(e => new JsonObject { ["id"] = e.Id, ["timestamp"] = 1440442987000, ["message"] = e.Message })
                .ToArray()),
        };

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
            gzip.Write(bytes, 0, bytes.Length);
        }
        return Convert.ToBase64String(output.ToArray());
    }
}
