using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.Core;

namespace ObserveAi;

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
    /// Scores one row with the question tree. A total scoring failure propagates, so Step Functions retries
    /// the Triage state and the fingerprint stays undecided.
    /// </summary>
    public static async Task<RowResultDto> TriageRowAsync(
        JsonElement row, IBedrockInvoker client, string modelArn, QuestionTree tree, ILambdaContext context)
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

        var result = await Triage.RunAsync(client, modelArn, row, tree).ConfigureAwait(false);
        foreach (var failed in result.Answers.Where(answer => answer.Error is not null))
        {
            context.Logger.LogWarning($"Row {rowId} signal {failed.Key} failed: {failed.Error}");
        }
        // Mass off the option letters means the model is answering something else, as after a model swap.
        if (result.DeclaredMass < 0.9)
        {
            context.Logger.LogWarning($"Row {rowId} declared mass below 0.9: {result.DeclaredMass:0.000}");
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
                client, modelArn, row, cancellationToken).ConfigureAwait(false);
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
