using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>Input for one triage execution: what the Kinesis consumer already computed, so the state machine never repeats it.</summary>
public sealed record ExecutionInput(
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("occurrences")] long Occurrences,
    [property: JsonPropertyName("firstSeen")] DateTimeOffset FirstSeen,
    [property: JsonPropertyName("logGroup")] string LogGroup,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("exceptionType")] string ExceptionType,
    [property: JsonPropertyName("frames")] IReadOnlyList<Frame> Frames);

/// <summary>
/// Lambda entrypoint for the pipeline stages, selected by the event's required "action" field: triage,
/// check-rate, escalate or file-issue. A Kinesis event has no action field and is recognised first by its
/// top-level "Records" array; that path parses the trace, resolves the repo and checks the seen table, and
/// starts an execution only for a fingerprint's first sighting.
/// </summary>
public static class Function
{
    private static readonly Lazy<AmazonBedrockRuntimeClient> LazyRuntime = new(CreateRuntime);
    private static readonly Lazy<BedrockBackend.Invoke> LazyInvoke = new(() => BedrockBackend.Against(LazyRuntime.Value));
    private static readonly Lazy<Diagnosis.Converse> LazyConverse = new(() => Diagnosis.Against(LazyRuntime.Value));
    private static readonly Lazy<Dynamo.UpdateItem> LazyUpdateItem = new(() => Dynamo.Against(new AmazonDynamoDBClient()));
    private static readonly Lazy<IAmazonSecretsManager> LazySecretsManager = new(() => new AmazonSecretsManagerClient());
    private static readonly Lazy<IAmazonStepFunctions> LazyStepFunctions = new(() => new AmazonStepFunctionsClient());
    private static readonly Lazy<HttpClient> LazyHttp = new(() => new HttpClient());
    private static readonly Lazy<QuestionTree> LazyTree = new(QuestionTree.LoadEmbedded);

    /// <summary>Starts one Step Functions execution.</summary>
    internal delegate Task<StartExecutionResponse> StartExecution(StartExecutionRequest request, CancellationToken cancellationToken);

    /// <summary>Every external call the handler makes. Tests construct one with fakes.</summary>
    internal sealed record Dependencies(
        BedrockBackend.Invoke Invoke,
        Diagnosis.Converse Converse,
        Dynamo.UpdateItem UpdateItem,
        StartExecution StartExecution,
        GitHub.Call GitHub)
    {
        /// <summary>
        /// The AWS and GitHub clients, each created on first use. The GitHub token is read from
        /// GITHUB_TOKEN_SECRET_ARN at most once per Dependencies.
        /// </summary>
        public static Dependencies Real()
        {
            var token = new Lazy<Task<string>>(() => ReadGitHubTokenAsync(RequireEnv("GITHUB_TOKEN_SECRET_ARN")));
            return new Dependencies(
                Invoke: (modelId, body, cancellationToken) => LazyInvoke.Value(modelId, body, cancellationToken),
                Converse: (modelId, system, user, cancellationToken) => LazyConverse.Value(modelId, system, user, cancellationToken),
                UpdateItem: (request, cancellationToken) => LazyUpdateItem.Value(request, cancellationToken),
                StartExecution: (request, cancellationToken) => LazyStepFunctions.Value.StartExecutionAsync(request, cancellationToken),
                GitHub: async (url, jsonBody, cancellationToken) =>
                {
                    var call = ObserveAi.GitHub.Against(LazyHttp.Value, await token.Value.ConfigureAwait(false));
                    return await call(url, jsonBody, cancellationToken).ConfigureAwait(false);
                });
        }
    }

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
        return DispatchAsync(lambdaEvent, Dependencies.Real(), context);
    }

    /// <summary>The handler body over the external calls it is given.</summary>
    internal static Task<LambdaResponse> DispatchAsync(JsonElement lambdaEvent, Dependencies deps, ILambdaContext context)
    {
        if (lambdaEvent.TryGetProperty("Records", out var records))
        {
            return RunStartExecutionsAsync(records, deps, context);
        }

        var action = RequireString(lambdaEvent, "action");
        return action switch
        {
            "triage" => RunTriageAsync(lambdaEvent, deps.Invoke, context),
            "check-rate" => RunCheckRateAsync(lambdaEvent, deps.UpdateItem),
            "escalate" => RunEscalateAsync(lambdaEvent, deps, context),
            "file-issue" => RunFileIssueAsync(lambdaEvent, deps.GitHub),
            _ => throw new InvalidOperationException($"Unknown action: '{action}'"),
        };
    }

    private static readonly TimeSpan SeenRetention = TimeSpan.FromDays(30);
    private static readonly Regex UnsafeExecutionNameChars = new(@"[^A-Za-z0-9\-_.]", RegexOptions.Compiled);

    /// <summary>
    /// Handles a Kinesis batch: parses each log event's trace, resolves its repo and checks SeenTable, and starts
    /// an execution only for a fingerprint's first sighting. Skipped events are counted, and the counts are the only
    /// record a skipped event leaves.
    /// </summary>
    private static async Task<LambdaResponse> RunStartExecutionsAsync(JsonElement records, Dependencies deps, ILambdaContext context)
    {
        var pipelineArn = RequireEnv("PIPELINE_ARN");
        var githubOrg = RequireEnv("GITHUB_ORG");
        var seenTable = RequireEnv("SEEN_TABLE");
        var now = DateTimeOffset.UtcNow;

        long started = 0, alreadyKnown = 0, unparseable = 0, noRepo = 0;
        foreach (var kinesisRecord in records.EnumerateArray())
        {
            var data = kinesisRecord.GetProperty("kinesis").GetProperty("data").GetString()!;
            foreach (var candidate in LogEnvelope.Unpack(data))
            {
                var trace = TraceParser.Parse(candidate.Message);
                if (trace is null)
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

                var fingerprint = Fingerprint.Compute(trace);
                var seen = await SeenStore.RecordAsync(deps.UpdateItem, seenTable, repo, fingerprint, now, SeenRetention)
                    .ConfigureAwait(false);
                if (seen.Occurrences != 1)
                {
                    alreadyKnown++;
                    continue;
                }

                var input = new ExecutionInput(
                    repo, fingerprint, seen.Occurrences, seen.FirstSeen, candidate.LogGroup, candidate.Message,
                    trace.Runtime, trace.ExceptionType, trace.Frames);
                await deps.StartExecution(new StartExecutionRequest
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

    /// <summary>
    /// Scores one row with the question tree. A total scoring failure propagates, so Step Functions retries
    /// the Triage state and the fingerprint stays undecided.
    /// </summary>
    private static async Task<LambdaResponse> RunTriageAsync(
        JsonElement lambdaEvent, BedrockBackend.Invoke invoke, ILambdaContext context)
    {
        var id = RequireString(lambdaEvent, "id");
        var tree = LazyTree.Value;
        var result = await Triage.RunAsync(invoke, RequireEnv("MODEL_ARN"), lambdaEvent, tree).ConfigureAwait(false);
        foreach (var failed in result.Answers.Where(answer => answer.Error is not null))
        {
            context.Logger.LogWarning($"Row {id} signal {failed.Key} failed: {failed.Error}");
        }
        // Mass off the option letters means the model is answering something else, as after a model swap.
        if (result.DeclaredMass < 0.9)
        {
            context.Logger.LogWarning($"Row {id} declared mass below 0.9: {result.DeclaredMass:0.000}");
        }
        return LambdaResponse.FromTriage(result, tree);
    }

    /// <summary>Buckets are keyed by hour, and a two-hour TTL always lands after the bucket's hour has ended.</summary>
    private static async Task<LambdaResponse> RunCheckRateAsync(JsonElement lambdaEvent, Dynamo.UpdateItem updateItem)
    {
        var repo = RequireString(lambdaEvent, "repo");
        var perRepoLimit = long.Parse(RequireEnv("ISSUES_PER_REPO_PER_HOUR"));
        var globalLimit = long.Parse(RequireEnv("ISSUES_PER_HOUR"));
        var result = await Caps.TryConsumeAsync(
            updateItem, RequireEnv("RATE_TABLE"), repo, perRepoLimit, globalLimit,
            DateTimeOffset.UtcNow, TimeSpan.FromHours(2)).ConfigureAwait(false);
        return new LambdaResponse { Allowed = result.Allowed, Tripped = result.Tripped, Count = result.Count, Limit = result.Limit };
    }

    /// <summary>
    /// Fetches the files the trace names and runs the escalate stage over them. All GitHub access happens
    /// here, so Escalation.RunAsync receives the source files as data.
    /// </summary>
    private static async Task<LambdaResponse> RunEscalateAsync(JsonElement lambdaEvent, Dependencies deps, ILambdaContext context)
    {
        var request = ParseEscalateRequest(lambdaEvent);
        var paths = SourceFetch.PathsFor(request.Trace, request.RawTrace);
        var fetched = await SourceFetch.FetchAsync(deps.GitHub, request.Repo, request.Commitish, paths).ConfigureAwait(false);

        var result = await Escalation.RunAsync(
            deps.Invoke, RequireEnv("MODEL_ARN"), deps.Converse, RequireEnv("DIAGNOSIS_MODEL_ID"),
            request with { Sources = fetched.Sources }).ConfigureAwait(false);
        var matched = result.FrameVerdicts.Count(v => v.Matched);
        context.Logger.LogInformation(
            $"Escalated {request.Repo}@{request.Commitish}: {fetched.Sources.Count}/{paths.Count} files fetched, {matched}/{result.FrameVerdicts.Count} frames matched");
        return new LambdaResponse { Escalate = result };
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

        return new EscalateRequest(
            RequireString(e, "repo"), RequireString(e, "commitish"), trace, RequireString(e, "rawTrace"),
            new Dictionary<string, string>(), e.GetProperty("bug").GetDouble(),
            e.TryGetProperty("occurrences", out var occ) ? occ.GetInt64() : 1,
            DateTimeOffset.Parse(RequireString(e, "firstSeen"), CultureInfo.InvariantCulture));
    }

    /// <summary>IssueFiler comments on an open issue with the same title, or creates a new one.</summary>
    private static async Task<LambdaResponse> RunFileIssueAsync(JsonElement lambdaEvent, GitHub.Call gitHub)
    {
        var repo = RequireString(lambdaEvent, "repo");
        var draft = new Draft(RequireString(lambdaEvent, "title"), RequireString(lambdaEvent, "body"));

        var result = await IssueFiler.FileAsync(gitHub, repo, draft).ConfigureAwait(false);

        return new LambdaResponse { Outcome = result.Outcome.ToString().ToLowerInvariant(), IssueNumber = result.IssueNumber };
    }

    private static async Task<string> ReadGitHubTokenAsync(string secretArn)
    {
        var response = await LazySecretsManager.Value.GetSecretValueAsync(
            new GetSecretValueRequest { SecretId = secretArn }).ConfigureAwait(false);
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
