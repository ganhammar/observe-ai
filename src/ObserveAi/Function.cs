using System.Net.Http;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;

namespace ObserveAi;

/// <summary>
/// AWS Lambda entrypoint for the pipeline stages a Step Functions state machine drives, selected
/// by an "action" field on the event: identify, resolve-repo, triage, check-rate, escalate, or
/// file-issue (a missing action means triage). The Kinesis event source that actually feeds this
/// pipeline carries no action field at all; a top-level "Records" array is how that shape is told
/// apart from everything else, ahead of the action switch. One deployment, one binary; the flow
/// between stages lives in the state machine, not here.
/// </summary>
public static class Function
{
    private static readonly Lazy<IBedrockInvoker> LazyClient = new(CreateClient);
    private static readonly Lazy<IAmazonDynamoDB> LazyDynamo = new(() => new AmazonDynamoDBClient());
    private static readonly Lazy<IAmazonSecretsManager> LazySecretsManager = new(() => new AmazonSecretsManagerClient());
    private static readonly Lazy<IAmazonStepFunctions> LazyStepFunctions = new(() => new AmazonStepFunctionsClient());
    private static readonly Lazy<HttpClient> LazyHttp = new(() => new HttpClient());
    private static readonly Lazy<QuestionTree> LazyTree = new(QuestionTree.LoadEmbedded);

    /// <summary>Reads one secret's current value. A test supplies a fake instead of a real Secrets Manager call.</summary>
    internal delegate Task<string> ReadSecret(string secretArn, CancellationToken cancellationToken);

    /// <summary>Starts one Step Functions execution. A test supplies a fake instead of a real StartExecution call.</summary>
    internal delegate Task<StartExecutionResponse> StartExecution(StartExecutionRequest request, CancellationToken cancellationToken);

    public static async Task Main()
    {
        var serializer = new SourceGeneratorLambdaJsonSerializer<LambdaJsonContext>();
        using var handlerWrapper = HandlerWrapper.GetHandlerWrapper<JsonElement, LambdaResponse>(
            FunctionHandlerAsync, serializer);
        using var bootstrap = new LambdaBootstrap(handlerWrapper);
        await bootstrap.RunAsync();
    }

    public static Task<LambdaResponse> FunctionHandlerAsync(JsonElement lambdaEvent, ILambdaContext context)
    {
        var modelArn = RequireEnv("MODEL_ARN");
        // SEMIF_MODE picks the triage path: the tree of sub-questions (default), or
        // the single flat question it replaced, kept so the two can be compared
        // against a deployed model without shipping code.
        var mode = Environment.GetEnvironmentVariable("SEMIF_MODE") ?? "tree";

        return DispatchAsync(lambdaEvent, LazyClient.Value, modelArn, mode, context);
    }

    /// <summary>
    /// The handler's body with the Bedrock client passed in, so a test can
    /// supply a fake invoker instead of one built from real AWS configuration.
    /// </summary>
    internal static Task<LambdaResponse> DispatchAsync(
        JsonElement lambdaEvent, IBedrockInvoker client, string modelArn, string mode, ILambdaContext context,
        Caps.UpdateItem? updateRate = null, StartExecution? startExecution = null,
        ReadSecret? readSecret = null, IssueFiler.CallGitHub? callGitHub = null)
    {
        if (lambdaEvent.ValueKind == JsonValueKind.Object
            && lambdaEvent.TryGetProperty("Records", out var records) && records.ValueKind == JsonValueKind.Array)
        {
            return RunStartExecutionsAsync(records, startExecution, context);
        }

        var action = lambdaEvent.ValueKind == JsonValueKind.Object
            && lambdaEvent.TryGetProperty("action", out var actionProperty)
            && actionProperty.ValueKind == JsonValueKind.String
                ? actionProperty.GetString()!
                : "triage";

        return action switch
        {
            "identify" => Task.FromResult(RunIdentify(lambdaEvent)),
            "resolve-repo" => Task.FromResult(RunResolveRepo(lambdaEvent)),
            "triage" => ScoreRowsAsync(lambdaEvent, client, modelArn, mode, context),
            "check-rate" => RunCheckRateAsync(lambdaEvent, updateRate),
            "escalate" => RunEscalateAsync(lambdaEvent, client, modelArn, context),
            "file-issue" => RunFileIssueAsync(lambdaEvent, readSecret, callGitHub),
            _ => throw new InvalidOperationException($"Unknown action: '{action}'"),
        };
    }

    /// <summary>
    /// What the Kinesis event source actually invokes: each record batches many
    /// CloudWatch Logs events, LogEnvelope.Unpack turns each into one candidate
    /// execution, and every candidate starts its own run of the pipeline rather
    /// than being triaged inline.
    /// </summary>
    private static async Task<LambdaResponse> RunStartExecutionsAsync(
        JsonElement records, StartExecution? startExecution, ILambdaContext context)
    {
        var pipelineArn = RequireEnv("PIPELINE_ARN");
        var start = startExecution ?? RealStartExecutionAsync;
        var started = 0L;
        foreach (var record in records.EnumerateArray())
        {
            var data = record.GetProperty("kinesis").GetProperty("data").GetString()!;
            foreach (var execution in LogEnvelope.Unpack(data))
            {
                await start(new StartExecutionRequest
                {
                    StateMachineArn = pipelineArn,
                    Input = JsonSerializer.Serialize(execution, LambdaJsonContext.Default.ExecutionInput),
                }, default).ConfigureAwait(false);
                started++;
            }
        }
        context.Logger.LogInformation($"Started {started} execution(s)");
        return new LambdaResponse { Started = started };
    }

    private static Task<StartExecutionResponse> RealStartExecutionAsync(StartExecutionRequest request, CancellationToken cancellationToken) =>
        LazyStepFunctions.Value.StartExecutionAsync(request, cancellationToken);

    private static LambdaResponse RunResolveRepo(JsonElement lambdaEvent)
    {
        var logGroupName = RequireString(lambdaEvent, "logGroupName");
        var repo = Pipeline.ResolveRepo(logGroupName, RequireEnv("GITHUB_ORG"));
        return new LambdaResponse { Repo = repo, ResolvedBy = repo is null ? null : "convention" };
    }

    /// <summary>Buckets are keyed by hour; two hours of slack past the boundary is plenty for the TTL sweep to catch up.</summary>
    private static async Task<LambdaResponse> RunCheckRateAsync(JsonElement lambdaEvent, Caps.UpdateItem? updateRate)
    {
        var repo = RequireString(lambdaEvent, "repo");
        var perRepoLimit = long.Parse(RequireEnv("ISSUES_PER_REPO_PER_HOUR"));
        var globalLimit = long.Parse(RequireEnv("ISSUES_PER_HOUR"));
        var result = await Caps.TryConsumeAsync(
            updateRate ?? Caps.Against(LazyDynamo.Value), RequireEnv("RATE_TABLE"), repo, perRepoLimit, globalLimit,
            DateTimeOffset.UtcNow, TimeSpan.FromHours(2)).ConfigureAwait(false);
        return new LambdaResponse { Allowed = result.Allowed, Tripped = result.Tripped, Count = result.Count, Limit = result.Limit };
    }

    private static LambdaResponse RunIdentify(JsonElement lambdaEvent)
    {
        var logGroupName = RequireString(lambdaEvent, "logGroupName");
        var message = RequireString(lambdaEvent, "message");
        var appPrefixes = lambdaEvent.TryGetProperty("appPrefixes", out var prefixes) && prefixes.ValueKind == JsonValueKind.Array
            ? prefixes.EnumerateArray().Select(p => p.GetString() ?? "").ToList()
            : [];

        return new LambdaResponse { Identify = IdentifyResultDto.From(Pipeline.Identify(logGroupName, message, appPrefixes)) };
    }

    /// <summary>Scores a single SemIf row or a {"rows": [...]} batch, returning {"results": [...]}, unchanged from before actions existed.</summary>
    internal static async Task<LambdaResponse> ScoreRowsAsync(
        JsonElement lambdaEvent, IBedrockInvoker client, string modelArn, string mode, ILambdaContext context)
    {
        var rows = ExtractRows(lambdaEvent);
        var results = new List<RowResultDto>(rows.Count);
        foreach (var row in rows)
        {
            results.Add(await Pipeline.TriageRowAsync(row, client, modelArn, mode, LazyTree, context).ConfigureAwait(false));
        }

        var failures = results.Count(result => result.Error is not null);
        context.Logger.LogInformation($"Scored {results.Count} rows, {failures} rejected");

        return new LambdaResponse { Results = results };
    }

    private static List<JsonElement> ExtractRows(JsonElement lambdaEvent)
    {
        if (lambdaEvent.ValueKind == JsonValueKind.Object
            && lambdaEvent.TryGetProperty("rows", out var rows)
            && rows.ValueKind == JsonValueKind.Array)
        {
            return [.. rows.EnumerateArray()];
        }
        return [lambdaEvent];
    }

    private static async Task<LambdaResponse> RunEscalateAsync(
        JsonElement lambdaEvent, IBedrockInvoker client, string modelArn, ILambdaContext context)
    {
        var request = ParseEscalateRequest(lambdaEvent);
        var result = await Pipeline.EscalateAsync(client, modelArn, request).ConfigureAwait(false);
        var matched = result.FrameVerdicts.Count(v => v.Matched);
        context.Logger.LogInformation($"Escalated {request.Repo}@{request.Commitish}: {matched}/{result.FrameVerdicts.Count} frames matched");
        return new LambdaResponse { Escalate = EscalateResultDto.From(result) };
    }

    /// <summary>trace.frames[i].source is the fetched span for that frame, or absent when nothing has been checked out for it yet.</summary>
    private static EscalateRequest ParseEscalateRequest(JsonElement e)
    {
        var traceEl = e.GetProperty("trace");
        var frames = new List<Frame>();
        var sources = new List<string?>();
        foreach (var frame in traceEl.GetProperty("frames").EnumerateArray())
        {
            frames.Add(new Frame(frame.GetProperty("method").GetString()!, frame.GetProperty("inApp").GetBoolean()));
            sources.Add(frame.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null);
        }
        var trace = new ParsedTrace(traceEl.GetProperty("runtime").GetString()!, traceEl.GetProperty("exceptionType").GetString()!, frames);
        var v = e.GetProperty("verdict");
        var verdict = new CombineResult(
            v.GetProperty("bug").GetDouble(), v.GetProperty("downstream").GetDouble(), v.TryGetProperty("fallback", out var fb) && fb.GetBoolean());

        return new EscalateRequest(
            RequireString(e, "repo"), RequireString(e, "commitish"), trace, RequireString(e, "rawTrace"), sources, verdict,
            e.TryGetProperty("occurrences", out var occ) ? occ.GetInt64() : 1,
            e.TryGetProperty("firstSeen", out var firstSeen) ? firstSeen.GetDateTimeOffset() : DateTimeOffset.UtcNow,
            e.TryGetProperty("rootCause", out var rootCause) ? rootCause.GetString() ?? "" : "");
    }

    /// <summary>Reads the token out of band and lets IssueFiler decide whether this is a fresh issue or a reopen of one already tracked.</summary>
    private static async Task<LambdaResponse> RunFileIssueAsync(
        JsonElement lambdaEvent, ReadSecret? readSecret, IssueFiler.CallGitHub? callGitHub)
    {
        var repo = RequireString(lambdaEvent, "repo");
        var draft = new Draft(RequireString(lambdaEvent, "title"), RequireString(lambdaEvent, "body"));

        var token = await (readSecret ?? ReadGitHubTokenAsync)(RequireEnv("GITHUB_TOKEN_SECRET_ARN"), default)
            .ConfigureAwait(false);
        var result = await IssueFiler.FileAsync(callGitHub ?? IssueFiler.Against(LazyHttp.Value, token), repo, draft)
            .ConfigureAwait(false);

        return new LambdaResponse { Outcome = result.Outcome.ToString().ToLowerInvariant(), IssueNumber = result.IssueNumber };
    }

    private static async Task<string> ReadGitHubTokenAsync(string secretArn, CancellationToken cancellationToken)
    {
        var response = await LazySecretsManager.Value.GetSecretValueAsync(
            new GetSecretValueRequest { SecretId = secretArn }, cancellationToken).ConfigureAwait(false);
        return response.SecretString;
    }

    private static string RequireString(JsonElement element, string field) =>
        element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidOperationException($"Missing required field: '{field}'");

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} environment variable is not set");

    private static IBedrockInvoker CreateClient()
    {
        var region = RequireEnv("BEDROCK_REGION");
        var config = new AmazonBedrockRuntimeConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
        };
        config.RetryMode = RequestRetryMode.Standard;
        // A Bedrock model that has scaled to zero can sit there until something
        // times out; with the old MaxErrorRetry of 10 that something was the whole
        // 60 second Lambda invocation, billed in full for a request that never
        // returned. A small retry count with a per-attempt timeout well inside the
        // Lambda timeout fails the row instead, with an error the caller can act on.
        config.MaxErrorRetry = 3;
        config.Timeout = TimeSpan.FromSeconds(10);
        return new AmazonBedrockInvoker(new AmazonBedrockRuntimeClient(config));
    }
}
