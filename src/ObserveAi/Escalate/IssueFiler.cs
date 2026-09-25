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
/// Files a drafted issue, or comments on an open issue with the same title; IssueDraft.Build keeps titles
/// stable across occurrences for this match. Every GitHub call goes through one delegate that returns the
/// raw response text: an empty jsonBody means GET, anything else a POST with that body.
/// </summary>
public static class IssueFiler
{
    public delegate Task<string> CallGitHub(string url, string jsonBody, CancellationToken cancellationToken);

    private static readonly Regex RepoPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    /// <summary>CallGitHub over HTTP with a bearer token.</summary>
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

    /// <summary>repo must be "owner/name"; anything else, including a full URL, is rejected before any request is built.</summary>
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
