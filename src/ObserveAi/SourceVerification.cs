using System.Text.Json;

namespace ObserveAi;

/// <summary>Whether the checked-out source could have produced this frame's throw.</summary>
public sealed record FrameVerdict(Frame Frame, bool Matched);

/// <summary>The reduced verdict for a whole trace: how many frames matched, and the sentence a drafted issue states about it.</summary>
public sealed record VerificationSummary(int Matched, int Total, string Sentence);

/// <summary>
/// Builds the "could this code throw here" question for one frame, and reduces
/// the answers into the sentence a drafted issue states about its own
/// confidence in the checkout it read.
///
/// This is judgment over one already-named span of one already-named file, not
/// exploration, so it is a single SemIf row per frame rather than a tool loop:
/// the frame tells us exactly what to ask about before the question is built.
/// </summary>
public static class SourceVerification
{
    private const string Yes = "yes";
    private const string No = "no";

    /// <summary>
    /// The SemIf row for one frame. The state carries the method, the exception
    /// type, and the span of the file around where the trace says it happened;
    /// the question is deliberately about capability, not certainty, because the
    /// checkout may be a different commit than the one that actually ran.
    /// </summary>
    public static JsonElement BuildRow(string rowId, Frame frame, string exceptionType, string fileSpan)
    {
        var state = $"Method: {frame.Method}\nException type: {exceptionType}\n\nSource:\n{fileSpan}";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", rowId);
            writer.WriteString("state", state);
            writer.WriteString("question", $"Could this code throw a {exceptionType} at {frame.Method}?");
            writer.WriteStartArray("options");
            WriteOption(writer, Yes, "The method is present here and capable of raising or propagating that exception.");
            WriteOption(writer, No, "The method is missing from this span, or nothing here could produce that exception.");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// True when the model favours "yes". This reads as the method being present
    /// and capable of the throw, not as an exact line-number match: line numbers
    /// drift between commits even when the checkout is exactly right, so a
    /// missing method is the real signal, not a moved one.
    /// </summary>
    public static bool Matched(IReadOnlyDictionary<string, double> probabilities) =>
        probabilities.GetValueOrDefault(Yes, 0.0) >= probabilities.GetValueOrDefault(No, 0.0);

    /// <summary>
    /// Reduces the per-frame verdicts to a match count and the exact sentence a
    /// drafted issue states about them. The wording carries the system's own
    /// uncertainty, so a reader without any other context still knows how much
    /// to trust the checkout the root cause below was read from.
    /// </summary>
    public static VerificationSummary Summarise(string commitLabel, IReadOnlyList<bool> frameMatches)
    {
        var total = frameMatches.Count;
        var matched = frameMatches.Count(m => m);
        var caveat = total == 0 || matched == total
            ? "."
            : matched == 0
                ? ", so this is likely not the code that ran."
                : ", so this may not be the code that ran.";
        var sentence = $"Analysed against {commitLabel}. Frame verification: {matched} of {total} matched{caveat}";
        return new VerificationSummary(matched, total, sentence);
    }

    private static void WriteOption(Utf8JsonWriter writer, string id, string description)
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }
}
