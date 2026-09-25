using System.Text.Json.Serialization;

namespace ObserveAi;

/// <summary>Escalate's input. Sources maps each path SourceFetch.PathsFor names to its fetched contents.</summary>
public sealed record EscalateRequest(
    string Repo, string Commitish, ParsedTrace Trace, string RawTrace, IReadOnlyDictionary<string, string> Sources,
    double Bug, DateTimeOffset FirstSeen);

/// <summary>Escalate's outcome: a verdict per verified frame, and the drafted issue.</summary>
public sealed record EscalateResult(
    [property: JsonPropertyName("frameVerdicts")] IReadOnlyList<FrameVerdict> FrameVerdicts,
    [property: JsonPropertyName("draft")] Draft Draft);

/// <summary>The escalate stage over source files it is given as data.</summary>
public static class Escalation
{
    /// <summary>
    /// Verifies the fetched source frame by frame, asks the diagnosis model for a root cause, and drafts the
    /// issue. The per-frame "could this code throw here" questions go to the readout model; the root cause
    /// paragraph is the only generative call. Never calls GitHub.
    /// </summary>
    public static async Task<EscalateResult> RunAsync(
        BedrockBackend.Invoke invoke, string modelArn, Diagnosis.Converse converse, string diagnosisModelId,
        EscalateRequest request, CancellationToken cancellationToken = default)
    {
        var framePaths = SourceFetch.FramePaths(request.Trace, request.RawTrace);

        var verdicts = new List<FrameVerdict>();
        for (var i = 0; i < request.Trace.Frames.Count; i++)
        {
            var frame = request.Trace.Frames[i];
            if (framePaths[i] is not { } path || !request.Sources.TryGetValue(path, out var source))
            {
                continue;
            }

            var row = SourceVerification.BuildRow($"verify::{i}", frame, request.Trace.ExceptionType, source);
            var score = await BedrockBackend.ScoreAsync(invoke, modelArn, row, cancellationToken).ConfigureAwait(false);
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
            request.Trace, request.Bug, request.FirstSeen, summary, rootCause,
            [.. request.Sources.Keys]);

        return new EscalateResult(verdicts, draft);
    }
}
