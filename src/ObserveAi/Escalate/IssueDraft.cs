using System.Text;
using System.Text.Json.Serialization;

namespace ObserveAi;

/// <summary>A drafted GitHub issue, not yet sent anywhere.</summary>
public sealed record Draft(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body);

/// <summary>
/// Builds the title and body of a triage issue from what the pipeline already
/// knows: the parsed trace, the verdict that escalated it, how often it has
/// fired, whether the checked-out source actually verified, and the root cause
/// the model read from that source. No network call, no template engine, just
/// the string a human reads before deciding whether to act on it.
/// </summary>
public static class IssueDraft
{
    /// <summary>
    /// The title is exception type plus the top in-app frame, and nothing that
    /// varies between occurrences of the same defect (not the count, not the
    /// timestamp, not the root cause wording), because a stable title is the
    /// only thing that lets a human notice "this again" instead of filing a
    /// second issue for something already open.
    /// </summary>
    public static Draft Build(
        ParsedTrace trace,
        CombineResult verdict,
        long occurrences,
        DateTimeOffset firstSeen,
        VerificationSummary verification,
        string rootCause,
        IReadOnlyList<string> framesRead)
    {
        var topFrame = trace.Frames.FirstOrDefault(f => f.InApp)?.Method
            ?? trace.Frames.FirstOrDefault()?.Method
            ?? "unknown location";
        var title = $"{trace.ExceptionType} in {topFrame}";

        var body = new StringBuilder();

        // What failed.
        body.Append('`').Append(topFrame).Append("` threw ").Append(trace.ExceptionType)
            .Append(" (").Append(trace.Runtime).Append(").\n\n");

        // How often, and since when.
        var times = occurrences == 1 ? "time" : "times";
        body.Append("Seen ").Append(occurrences).Append(' ').Append(times)
            .Append(", first on ").Append(firstSeen.ToString("yyyy-MM-dd"))
            .Append(". P(bug) = ").Append(verdict.Bug.ToString("0.00")).Append(".\n\n");

        // The verification sentence, stating its own confidence in the checkout.
        body.Append(verification.Sentence).Append("\n\n");

        // The model's root cause, when it produced one.
        if (rootCause.Trim().Length > 0)
        {
            body.Append("**Root cause**\n\n").Append(rootCause.Trim()).Append("\n\n");
        }

        // The frames it actually read, so a human can check the work.
        body.Append("**Frames read**\n\n");
        if (framesRead.Count == 0)
        {
            body.Append("(none; no file named by the trace could be fetched)\n");
        }
        else
        {
            foreach (var path in framesRead)
            {
                body.Append("- `").Append(path).Append("`\n");
            }
        }

        return new Draft(title, body.ToString().TrimEnd());
    }
}
