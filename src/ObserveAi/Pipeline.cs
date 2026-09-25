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

/// <summary>
/// What starts one triage execution: everything the Kinesis consumer already
/// worked out (identify, repo resolution, the occurrence count), so the state
/// machine itself never redoes any of it.
/// </summary>
public sealed record ExecutionInput(
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("occurrences")] long Occurrences,
    [property: JsonPropertyName("logGroup")] string LogGroup,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("exceptionType")] string ExceptionType,
    [property: JsonPropertyName("frames")] IReadOnlyList<Frame> Frames);

/// <summary>
/// What escalate needs. FrameSources[i] is the fetched span for Trace.Frames[i],
/// or null when nothing has been fetched for that frame yet; nothing here calls
/// GitHub, so every field is data the caller already has.
/// </summary>
public sealed record EscalateRequest(
    string Repo, string Commitish, ParsedTrace Trace, string RawTrace, IReadOnlyList<string?> FrameSources,
    CombineResult Verdict, long Occurrences, DateTimeOffset FirstSeen, string RootCause);

/// <summary>Escalate's outcome: what to fetch, what the fetched source would have verified, and the issue drafted from both.</summary>
public sealed record EscalateResult(
    IReadOnlyList<SourceFetchRequest> FetchRequests, IReadOnlyList<FrameVerdict> FrameVerdicts, Draft Draft);

/// <summary>
/// The pipeline stages a Step Functions state machine drives this Lambda
/// through: identify, resolve-repo, triage, check-rate, escalate, and
/// file-issue. Each is a plain function over the components it needs; the
/// state machine owns the flow between them and everything with a real side
/// effect.
/// </summary>
public static class Pipeline
{
    /// <summary>
    /// Parses one raw log event into fingerprintable facts. Returns a result
    /// saying parsing failed rather than throwing: a line with no stack trace
    /// in it is routine, not exceptional, and the state machine routes it.
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
    /// Resolves the repository owning one log event by convention: the
    /// GitHub organisation configured for the deployment, plus the resource
    /// name the log group already names. Returns null when the log group
    /// does not match a known shape, so the state machine reaches its
    /// UnknownRepo stop rather than failing.
    /// </summary>
    public static string? ResolveRepo(string logGroupName, string githubOrg) =>
        ServiceIdentity.ConventionalRepo(logGroupName, githubOrg);

    /// <summary>
    /// The tree/flat scoring path for one row. Only the flat path catches a
    /// rejected row here: tree mode lets a total scoring failure reach the
    /// caller uncaught, so Step Functions retries the triage state instead of
    /// treating the fingerprint as decided.
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
    /// Plans the fetch and drafts the issue for one escalated defect. Never
    /// calls GitHub itself: the caller runs FetchRequests and opens the issue in
    /// Draft. Only the per-frame "could this code throw here" questions go to
    /// the model.
    /// </summary>
    public static async Task<EscalateResult> EscalateAsync(
        IBedrockInvoker client, string modelArn, EscalateRequest request, CancellationToken cancellationToken = default)
    {
        var paths = SourceFetch.PathsFor(request.Trace, request.RawTrace);
        var requests = SourceFetch.RequestsFor(request.Repo, request.Commitish, paths);

        var verdicts = new List<FrameVerdict>();
        for (var i = 0; i < request.Trace.Frames.Count; i++)
        {
            var frame = request.Trace.Frames[i];
            var source = i < request.FrameSources.Count ? request.FrameSources[i] : null;
            if (!frame.InApp || string.IsNullOrEmpty(source))
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
        var draft = IssueDraft.Build(
            request.Trace, request.Verdict, request.Occurrences, request.FirstSeen, summary, request.RootCause, paths);

        return new EscalateResult(requests, verdicts, draft);
    }
}
