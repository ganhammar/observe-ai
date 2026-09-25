using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Runtime;

namespace ObserveAi;

/// <summary>Identify's outcome: fingerprintable facts about one log event, or why it did not parse.</summary>
public sealed record IdentifyResult(
    bool Parsed, string? Runtime, string? ExceptionType, string? Fingerprint, string? Signature,
    string? NamespacePrefix, LogGroupKind? ResourceKind, string? ResourceName,
    IReadOnlyList<Frame>? Frames, string? Error);

/// <summary>Input for one triage execution: what the Kinesis consumer already computed, so the state machine never repeats it.</summary>
public sealed record ExecutionInput(
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("occurrences")] long Occurrences,
    [property: JsonPropertyName("logGroup")] string LogGroup,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("exceptionType")] string ExceptionType,
    [property: JsonPropertyName("frames")] IReadOnlyList<Frame> Frames);

/// <summary>Escalate's input. Sources maps each path SourceFetch.PathsFor names to its fetched contents.</summary>
public sealed record EscalateRequest(
    string Repo, string Commitish, ParsedTrace Trace, string RawTrace, IReadOnlyDictionary<string, string> Sources,
    CombineResult Verdict, long Occurrences, DateTimeOffset FirstSeen);

/// <summary>Escalate's outcome: the fetch requests, a verdict per frame, and the drafted issue.</summary>
public sealed record EscalateResult(
    IReadOnlyList<SourceFetchRequest> FetchRequests, IReadOnlyList<FrameVerdict> FrameVerdicts, Draft Draft);

/// <summary>
/// The pipeline stages as functions over the components they are given. None of them writes anywhere;
/// the state machine owns the flow between stages.
/// </summary>
public static class Pipeline
{
    /// <summary>
    /// Parses one raw log event into fingerprintable facts. A message with no stack trace returns
    /// Parsed = false for the state machine to route.
    /// </summary>
    public static IdentifyResult Identify(string logGroupName, string message, IReadOnlyList<string> appPrefixes)
    {
        var logGroup = ServiceIdentity.ParseLogGroup(logGroupName);
        var trace = TraceParser.Parse(message, appPrefixes);
        if (trace is null)
        {
            return new IdentifyResult(
                false, null, null, null, null, null, logGroup?.Kind, logGroup?.ResourceName, null,
                "message does not contain a recognised stack trace");
        }

        return new IdentifyResult(
            true, trace.Runtime, trace.ExceptionType, Fingerprint.Compute(trace), Fingerprint.Signature(trace),
            ServiceIdentity.NamespacePrefix(trace), logGroup?.Kind, logGroup?.ResourceName, trace.Frames, null);
    }

    /// <summary>
    /// Resolves the repository by convention (see ServiceIdentity.ConventionalRepo). Returns null for an
    /// unrecognised log group, which the state machine routes to its UnknownRepo stop.
    /// </summary>
    public static string? ResolveRepo(string logGroupName, string githubOrg) =>
        ServiceIdentity.ConventionalRepo(logGroupName, githubOrg);

    /// <summary>
    /// Scores one row in tree or flat mode. Only flat mode catches a rejected row. In tree mode a total
    /// scoring failure propagates, so Step Functions retries the Triage state and the fingerprint stays undecided.
    /// </summary>
    public static async Task<RowResultDto> TriageRowAsync(
        JsonElement row, IBedrockInvoker client, string modelArn, string mode,
        Lazy<QuestionTree> lazyTree, ILambdaContext context)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            var kind = DescribeKind(row);
            context.Logger.LogWarning($"Row rejected: expected an object, got {kind}");
            return RowResultDto.FromError(null, $"Row must be a JSON object, got {kind}");
        }

        string? rowId = row.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String
            ? idProperty.GetString()
            : null;

        if (mode == "flat")
        {
            try
            {
                var score = await BedrockBackend.ScoreAsync(client, modelArn, row).ConfigureAwait(false);
                return RowResultDto.FromScore(score);
            }
            catch (Exception error) when (error is RowValidationException or AmazonServiceException or AmazonClientException)
            {
                context.Logger.LogWarning($"Row {rowId} rejected: {error.GetType().Name}: {error.Message}");
                return RowResultDto.FromError(rowId, $"{error.GetType().Name}: {error.Message}");
            }
        }

        var tree = lazyTree.Value;
        var result = await Triage.RunAsync(client, modelArn, row, tree).ConfigureAwait(false);
        foreach (var failed in result.Answers.Where(answer => answer.Error is not null))
        {
            context.Logger.LogWarning($"Row {rowId} signal {failed.Key} failed: {failed.Error}");
        }
        return RowResultDto.FromTriage(rowId, result, tree);
    }

    private static string DescribeKind(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => "str",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Array => "list",
        JsonValueKind.Null or JsonValueKind.Undefined => "NoneType",
        _ => element.ValueKind.ToString(),
    };

    /// <summary>
    /// Verifies the fetched source frame by frame, asks the diagnosis model for a root cause, and drafts the
    /// issue. The per-frame "could this code throw here" questions go to the readout model; the root cause
    /// paragraph is the only generative call. Never calls GitHub.
    /// </summary>
    public static async Task<EscalateResult> EscalateAsync(
        IBedrockInvoker client, string modelArn, Diagnosis.Converse converse, string diagnosisModelId,
        EscalateRequest request, CancellationToken cancellationToken = default)
    {
        var framePaths = SourceFetch.FramePaths(request.Trace, request.RawTrace);
        var requests = SourceFetch.RequestsFor(
            request.Repo, request.Commitish, SourceFetch.PathsFor(request.Trace, request.RawTrace));

        var verdicts = new List<FrameVerdict>();
        for (var i = 0; i < request.Trace.Frames.Count; i++)
        {
            var frame = request.Trace.Frames[i];
            if (framePaths[i] is not { } path || !request.Sources.TryGetValue(path, out var source))
            {
                continue;
            }

            var row = SourceVerification.BuildRow($"verify::{i}", frame, request.Trace.ExceptionType, source);
            var score = await BedrockBackend.ScoreAsync(
                client, modelArn, row, constrain: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            var probabilities = score.OptionIds
                .Zip(score.Probabilities, (id, p) => (id, p))
                .ToDictionary(pair => pair.id, pair => pair.p);
            verdicts.Add(new FrameVerdict(frame, SourceVerification.Matched(probabilities)));
        }

        var summary = SourceVerification.Summarise(
            $"{request.Repo}@{request.Commitish}", verdicts.Select(v => v.Matched).ToList());
        var rootCause = await Diagnosis.DiagnoseAsync(
            converse, diagnosisModelId, request.RawTrace, request.Sources, cancellationToken).ConfigureAwait(false);
        var draft = IssueDraft.Build(
            request.Trace, request.Verdict, request.Occurrences, request.FirstSeen, summary, rootCause,
            [.. request.Sources.Keys]);

        return new EscalateResult(requests, verdicts, draft);
    }
}
