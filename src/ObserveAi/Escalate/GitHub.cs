using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace ObserveAi;

/// <summary>The one way the pipeline reaches GitHub, shared by SourceFetch and IssueFiler.</summary>
public static class GitHub
{
    /// <summary>
    /// One GitHub API request returning the response text, or null for a 404. A null jsonBody means GET,
    /// anything else a POST with that body.
    /// </summary>
    public delegate Task<string?> Call(string url, string? jsonBody, CancellationToken cancellationToken);

    private static readonly Regex RepoPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    /// <summary>
    /// Call over HTTP with a bearer token. A GET asks for the raw media type, under which the Contents API
    /// returns the file body; the other endpoints ignore it and return JSON.
    /// </summary>
    public static Call Against(HttpClient http, string token) => async (url, jsonBody, cancellationToken) =>
    {
        using var request = new HttpRequestMessage(jsonBody is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("observe-ai");
        request.Headers.Accept.ParseAdd(jsonBody is null ? "application/vnd.github.raw+json" : "application/vnd.github+json");
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    };

    /// <summary>repo forms part of every URL path, so anything but "owner/name", including a full URL, is rejected.</summary>
    public static void ValidateRepo(string repo)
    {
        if (!RepoPattern.IsMatch(repo)) throw new ArgumentException($"repo must look like 'owner/name': '{repo}'", nameof(repo));
    }
}
