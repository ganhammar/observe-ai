using System.Text.Json;

namespace ObserveAi;

/// <summary>Whether the checked-out source could have produced this frame's throw.</summary>
public sealed record FrameVerdict(Frame Frame, bool Matched);

/// <summary>A whole trace's verification, as the sentence the issue states.</summary>
public sealed record VerificationSummary(string Sentence);

/// <summary>
/// Builds the "could this code throw here" question for one frame and reduces the answers to the sentence
/// an issue states about its checkout. Each frame is one SemIf row, since the frame already names the
/// file and method to ask about.
/// </summary>
public static class SourceVerification
{
    private const string Yes = "yes";
    private const string No = "no";

    /// <summary>
    /// The SemIf row for one frame: the method, the exception type and the file span. The question asks
    /// whether the code could throw, since the checkout may be a different commit from the one that ran.
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
    /// True when the model favours "yes", meaning the method is present and could throw. Line numbers drift
    /// between commits, so only a missing method counts against the checkout.
    /// </summary>
    public static bool Matched(IReadOnlyDictionary<string, double> probabilities) =>
        probabilities.GetValueOrDefault(Yes, 0.0) >= probabilities.GetValueOrDefault(No, 0.0);

    /// <summary>
    /// Reduces the per-frame verdicts to the sentence an issue states about them. The
    /// sentence carries its own caveat, so a reader knows how far to trust the checkout the root cause came from.
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
        return new VerificationSummary(sentence);
    }

    private static void WriteOption(Utf8JsonWriter writer, string id, string description)
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }
}
