using System.Text;
using System.Text.Json.Serialization;

namespace ObserveAi;

/// <summary>A drafted GitHub issue, not yet sent anywhere.</summary>
public sealed record Draft(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body);

/// <summary>
/// Builds a triage issue's title and body from the parsed trace, the bug probability, the occurrence
/// count, the source verification and the model's root cause. Makes no network call.
/// </summary>
public static class IssueDraft
{
    /// <summary>
    /// The title is the exception type plus the top in-app frame and nothing that varies between
    /// occurrences, so IssueFiler can match a recurrence to its open issue by title.
    /// </summary>
    public static Draft Build(
        ParsedTrace trace,
        double bug,
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

        body.Append('`').Append(topFrame).Append("` threw ").Append(trace.ExceptionType)
            .Append(" (").Append(trace.Runtime).Append(").\n\n");

        body.Append("First seen ").Append(firstSeen.ToString("yyyy-MM-dd"))
            .Append(". P(bug) = ").Append(bug.ToString("0.00")).Append(".\n\n");

        body.Append(verification.Sentence).Append("\n\n");

        if (rootCause.Trim().Length > 0)
        {
            body.Append("**Root cause**\n\n").Append(rootCause.Trim()).Append("\n\n");
        }

        // The files the diagnosis read, so a reader can check its work.
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
