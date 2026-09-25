using System.Text;
using System.Text.Json;

namespace ObserveAi;

/// <summary>Which action IssueFiler took for a drafted issue.</summary>
public enum FilingOutcome { Created, Commented }

/// <summary>The issue a draft ended up as, and how.</summary>
public sealed record FilingResult(FilingOutcome Outcome, long IssueNumber);

/// <summary>
/// Files a drafted issue, or comments on an open issue with the same title; IssueDraft.Build keeps titles
/// stable across occurrences for this match. A 404 from any call is an error.
/// </summary>
public static class IssueFiler
{
    /// <summary>repo must be "owner/name"; anything else is rejected before any request is built.</summary>
    public static async Task<FilingResult> FileAsync(
        GitHub.Call gitHub, string repo, Draft draft, CancellationToken cancellationToken = default)
    {
        GitHub.ValidateRepo(repo);

        async Task<string> Send(string url, string? jsonBody) =>
            await gitHub(url, jsonBody, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"GitHub returned 404 for {url}");

        var searchResponse = await Send($"https://api.github.com/repos/{repo}/issues?state=open", null).ConfigureAwait(false);
        if (FindByTitle(searchResponse, draft.Title) is { } number)
        {
            var commentBody = WriteJson(writer => writer.WriteString("body", $"Recurred.\n\n{draft.Body}"));
            await Send($"https://api.github.com/repos/{repo}/issues/{number}/comments", commentBody).ConfigureAwait(false);
            return new FilingResult(FilingOutcome.Commented, number);
        }

        var issueBody = WriteJson(writer =>
        {
            writer.WriteString("title", draft.Title);
            writer.WriteString("body", draft.Body);
        });
        var createResponse = await Send($"https://api.github.com/repos/{repo}/issues", issueBody).ConfigureAwait(false);
        using var created = JsonDocument.Parse(createResponse);
        return new FilingResult(FilingOutcome.Created, created.RootElement.GetProperty("number").GetInt64());
    }

    private static long? FindByTitle(string searchResponse, string title)
    {
        using var document = JsonDocument.Parse(searchResponse);
        foreach (var issue in document.RootElement.EnumerateArray())
        {
            if (issue.GetProperty("title").GetString() == title)
            {
                return issue.GetProperty("number").GetInt64();
            }
        }
        return null;
    }

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
