using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Lambda entrypoint for the pipeline stages, selected by the event's "action" field: identify,
/// resolve-repo, triage, check-rate, escalate or file-issue. A missing action means triage. A Kinesis
/// event has no action field and is recognised first by its top-level "Records" array; that path runs
/// identify, repo resolution and the seen check, and starts an execution only when triage is warranted.
/// </summary>
public static class Function
{
    private static readonly Lazy<AmazonBedrockRuntimeClient> LazyRuntime = new(CreateRuntime);
    private static readonly Lazy<IBedrockInvoker> LazyClient = new(() => new AmazonBedrockInvoker(LazyRuntime.Value));
    private static readonly Lazy<Diagnosis.Converse> LazyConverse = new(() => Diagnosis.Against(LazyRuntime.Value));
    private static readonly Lazy<IAmazonDynamoDB> LazyDynamo = new(() => new AmazonDynamoDBClient());
    private static readonly Lazy<IAmazonSecretsManager> LazySecretsManager = new(() => new AmazonSecretsManagerClient());
    private static readonly Lazy<IAmazonStepFunctions> LazyStepFunctions = new(() => new AmazonStepFunctionsClient());
    private static readonly Lazy<HttpClient> LazyHttp = new(() => new HttpClient());
    private static readonly Lazy<QuestionTree> LazyTree = new(QuestionTree.LoadEmbedded);

    /// <summary>Reads one secret's current value. Tests substitute a fake.</summary>
    internal delegate Task<string> ReadSecret(string secretArn, CancellationToken cancellationToken);

    /// <summary>Starts one Step Functions execution. Tests substitute a fake.</summary>
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
        // SEMIF_MODE selects the triage path: "tree" (default) or "flat", a single-question baseline.
        var mode = Environment.GetEnvironmentVariable("SEMIF_MODE") ?? "tree";

        return DispatchAsync(lambdaEvent, LazyClient.Value, modelArn, mode, context);
    }

    /// <summary>The handler body, with the Bedrock client and every other external call injectable for tests.</summary>
    internal static Task<LambdaResponse> DispatchAsync(
        JsonElement lambdaEvent, IBedrockInvoker client, string modelArn, string mode, ILambdaContext context,
        Caps.UpdateItem? updateRate = null, StartExecution? startExecution = null,
        ReadSecret? readSecret = null, IssueFiler.CallGitHub? callGitHub = null, SeenStore.UpdateItem? recordSeen = null,
        SourceFetch.Get? getSource = null, Diagnosis.Converse? converse = null)
    {
        if (lambdaEvent.ValueKind == JsonValueKind.Object
            && lambdaEvent.TryGetProperty("Records", out var records) && records.ValueKind == JsonValueKind.Array)
        {
            return RunStartExecutionsAsync(records, startExecution, recordSeen, context);
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
            "escalate" => RunEscalateAsync(lambdaEvent, client, modelArn, readSecret, getSource, converse, context),
            "file-issue" => RunFileIssueAsync(lambdaEvent, readSecret, callGitHub),
            _ => throw new InvalidOperationException($"Unknown action: '{action}'"),
        };
    }

    private static readonly TimeSpan SeenRetention = TimeSpan.FromDays(30);
    private static readonly Regex UnsafeExecutionNameChars = new(@"[^A-Za-z0-9\-_.]", RegexOptions.Compiled);

    /// <summary>
    /// Handles a Kinesis batch: identifies each log event, resolves its repo and checks SeenTable, and starts
    /// an execution only for events worth triaging. Skipped events are counted, and the counts are the only
    /// record a skipped event leaves.
    /// </summary>
    private static async Task<LambdaResponse> RunStartExecutionsAsync(
        JsonElement records, StartExecution? startExecution, SeenStore.UpdateItem? recordSeen, ILambdaContext context)
    {
        var pipelineArn = RequireEnv("PIPELINE_ARN");
        var githubOrg = RequireEnv("GITHUB_ORG");
        var seenTable = RequireEnv("SEEN_TABLE");
        var start = startExecution ?? RealStartExecutionAsync;
        var record = recordSeen ?? SeenStore.Against(LazyDynamo.Value);
        var now = DateTimeOffset.UtcNow;

        long started = 0, alreadyKnown = 0, unparseable = 0, noRepo = 0;
        foreach (var kinesisRecord in records.EnumerateArray())
        {
            var data = kinesisRecord.GetProperty("kinesis").GetProperty("data").GetString()!;
            foreach (var candidate in LogEnvelope.Unpack(data))
            {
                var identity = Pipeline.Identify(candidate.LogGroup, candidate.Message, []);
                if (!identity.Parsed)
                {
                    unparseable++;
                    continue;
                }

                var repo = ServiceIdentity.ConventionalRepo(candidate.LogGroup, githubOrg);
                if (repo is null)
                {
                    noRepo++;
                    continue;
                }

                var signature = EvidenceSignature.Compute(TriageState(candidate.LogGroup, candidate.Message));
                var seen = await SeenStore.RecordAsync(record, seenTable, repo, identity.Fingerprint!, now, SeenRetention, signature)
                    .ConfigureAwait(false);
                if (!seen.ShouldTriage)
                {
                    alreadyKnown++;
                    continue;
                }

                var input = new ExecutionInput(
                    repo, identity.Fingerprint!, seen.Occurrences, candidate.LogGroup, candidate.Message,
                    identity.Runtime!, identity.ExceptionType!, identity.Frames!);
                await start(new StartExecutionRequest
                {
                    StateMachineArn = pipelineArn,
                    Name = SanitiseExecutionName(candidate.Id),
                    Input = JsonSerializer.Serialize(input, LambdaJsonContext.Default.ExecutionInput),
                }, default).ConfigureAwait(false);
                started++;
            }
        }

        context.Logger.LogInformation(
            $"Started {started}, already known {alreadyKnown}, unparseable {unparseable}, no repo {noRepo}");
        return new LambdaResponse { Started = started, AlreadyKnown = alreadyKnown, Unparseable = unparseable, NoRepo = noRepo };
    }

    /// <summary>The input the Triage state in infra/pipeline.asl.json builds from $.logGroup and $.message, so the signature hashes what triage scores.</summary>
    private static JsonElement TriageState(string logGroup, string message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("service", logGroup);
            writer.WriteString("stack_trace", message);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Step Functions deduplicates by execution name, so naming the execution after the log event id makes
    /// StartExecution idempotent across Kinesis retries. Names allow at most 80 characters and reject
    /// whitespace, wildcards, brackets and several punctuation marks.
    /// </summary>
    private static string SanitiseExecutionName(string id)
    {
        var sanitised = UnsafeExecutionNameChars.Replace(id, "_");
        return sanitised.Length > 80 ? sanitised[..80] : sanitised;
    }

    private static Task<StartExecutionResponse> RealStartExecutionAsync(StartExecutionRequest request, CancellationToken cancellationToken) =>
        LazyStepFunctions.Value.StartExecutionAsync(request, cancellationToken);

    private static LambdaResponse RunResolveRepo(JsonElement lambdaEvent)
    {
        var logGroupName = RequireString(lambdaEvent, "logGroupName");
        var repo = Pipeline.ResolveRepo(logGroupName, RequireEnv("GITHUB_ORG"));
        return new LambdaResponse { Repo = repo, ResolvedBy = repo is null ? null : "convention" };
    }

    /// <summary>Buckets are keyed by hour, and a two-hour TTL always lands after the bucket's hour has ended.</summary>
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

    /// <summary>Scores a single SemIf row or a {"rows": [...]} batch and returns {"results": [...]}.</summary>
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

    /// <summary>
    /// Fetches the files the trace names and runs the escalate stage over them. All GitHub access happens
    /// here, so Pipeline.EscalateAsync receives the source files as data.
    /// </summary>
    private static async Task<LambdaResponse> RunEscalateAsync(
        JsonElement lambdaEvent, IBedrockInvoker client, string modelArn, ReadSecret? readSecret,
        SourceFetch.Get? getSource, Diagnosis.Converse? converse, ILambdaContext context)
    {
        var request = ParseEscalateRequest(lambdaEvent);
        var paths = SourceFetch.PathsFor(request.Trace, request.RawTrace);
        if (getSource is null)
        {
            var token = await (readSecret ?? ReadGitHubTokenAsync)(RequireEnv("GITHUB_TOKEN_SECRET_ARN"), default)
                .ConfigureAwait(false);
            getSource = SourceFetch.Against(LazyHttp.Value, token);
        }
        var fetched = await SourceFetch.FetchAsync(getSource, request.Repo, request.Commitish, paths).ConfigureAwait(false);

        var result = await Pipeline.EscalateAsync(
            client, modelArn, converse ?? LazyConverse.Value, RequireEnv("DIAGNOSIS_MODEL_ID"),
            request with { Sources = fetched.Sources }).ConfigureAwait(false);
        var matched = result.FrameVerdicts.Count(v => v.Matched);
        context.Logger.LogInformation(
            $"Escalated {request.Repo}@{request.Commitish}: {fetched.Sources.Count}/{paths.Count} files fetched, {matched}/{result.FrameVerdicts.Count} frames matched");
        return new LambdaResponse { Escalate = EscalateResultDto.From(result) };
    }

    private static EscalateRequest ParseEscalateRequest(JsonElement e)
    {
        var traceEl = e.GetProperty("trace");
        var frames = new List<Frame>();
        foreach (var frame in traceEl.GetProperty("frames").EnumerateArray())
        {
            frames.Add(new Frame(frame.GetProperty("method").GetString()!, frame.GetProperty("inApp").GetBoolean()));
        }
        var trace = new ParsedTrace(traceEl.GetProperty("runtime").GetString()!, traceEl.GetProperty("exceptionType").GetString()!, frames);
        var v = e.GetProperty("verdict");
        var verdict = new CombineResult(
            v.GetProperty("bug").GetDouble(), v.GetProperty("downstream").GetDouble(), v.TryGetProperty("fallback", out var fb) && fb.GetBoolean());

        return new EscalateRequest(
            RequireString(e, "repo"), RequireString(e, "commitish"), trace, RequireString(e, "rawTrace"),
            new Dictionary<string, string>(), verdict,
            e.TryGetProperty("occurrences", out var occ) ? occ.GetInt64() : 1,
            e.TryGetProperty("firstSeen", out var firstSeen) ? firstSeen.GetDateTimeOffset() : DateTimeOffset.UtcNow);
    }

    /// <summary>IssueFiler comments on an open issue with the same title, or creates a new one.</summary>
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

    private static AmazonBedrockRuntimeClient CreateRuntime()
    {
        var region = RequireEnv("BEDROCK_REGION");
        var config = new AmazonBedrockRuntimeConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
        };
        config.RetryMode = RequestRetryMode.Standard;
        // A Bedrock model scaled to zero can hang a request; bounded retries fail the row inside the Lambda timeout.
        config.MaxErrorRetry = 3;
        // 20 seconds covers a diagnosis paragraph; a warm readout answers in under a second.
        config.Timeout = TimeSpan.FromSeconds(20);
        return new AmazonBedrockRuntimeClient(config);
    }
}
