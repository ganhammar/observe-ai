using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ObserveAi;

/// <summary>Which action IssueFiler took for a drafted issue.</summary>
public enum FilingOutcome { Created, Commented }

/// <summary>The issue a draft ended up as, and how.</summary>
public sealed record FilingResult(FilingOutcome Outcome, long IssueNumber);

/// <summary>
/// Files a drafted issue against a repository's tracker, or, when an open
/// issue with the same title is already there, comments on it instead: the
/// same defect firing again after a fix has landed is a reopen of that
/// defect, not a fresh one, and IssueDraft.Build keeps a title stable across
/// occurrences for exactly this comparison.
///
/// Every GitHub call goes through one delegate so nothing here touches the
/// network or holds a token: an empty jsonBody means GET, anything else
/// means POST with that body. The delegate returns the raw response text,
/// which this reads only far enough to find a title match or an issue
/// number.
/// </summary>
public static class IssueFiler
{
    public delegate Task<string> CallGitHub(string url, string jsonBody, CancellationToken cancellationToken);

    private static readonly Regex RepoPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    /// <summary>The real CallGitHub: an empty jsonBody is a bearer-token GET, anything else a bearer-token POST.</summary>
    public static CallGitHub Against(HttpClient http, string token) => async (url, jsonBody, cancellationToken) =>
    {
        using var request = new HttpRequestMessage(jsonBody.Length == 0 ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("observe-ai");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (jsonBody.Length > 0)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    };

    /// <summary>repo must be "owner/name"; anything else, including a full URL, is rejected before it reaches a request.</summary>
    public static async Task<FilingResult> FileAsync(
        CallGitHub callGitHub, string repo, Draft draft, CancellationToken cancellationToken = default)
    {
        if (!RepoPattern.IsMatch(repo))
        {
            throw new ArgumentException($"repo must look like 'owner/name': '{repo}'", nameof(repo));
        }

        var searchResponse = await callGitHub($"https://api.github.com/repos/{repo}/issues?state=open", "", cancellationToken)
            .ConfigureAwait(false);
        if (FindByTitle(searchResponse, draft.Title) is { } number)
        {
            var commentBody = WriteJson(writer => writer.WriteString("body", $"Recurred.\n\n{draft.Body}"));
            await callGitHub($"https://api.github.com/repos/{repo}/issues/{number}/comments", commentBody, cancellationToken)
                .ConfigureAwait(false);
            return new FilingResult(FilingOutcome.Commented, number);
        }

        var issueBody = WriteJson(writer =>
        {
            writer.WriteString("title", draft.Title);
            writer.WriteString("body", draft.Body);
        });
        var createResponse = await callGitHub($"https://api.github.com/repos/{repo}/issues", issueBody, cancellationToken)
            .ConfigureAwait(false);
        using var created = JsonDocument.Parse(createResponse);
        return new FilingResult(FilingOutcome.Created, created.RootElement.GetProperty("number").GetInt64());
    }

    /// <summary>The first open issue whose title matches exactly, or null when none does.</summary>
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
