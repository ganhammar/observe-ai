using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ObserveAi;

/// <summary>One file's GitHub Contents API request: Path is repository-relative, Url is ready to GET.</summary>
public sealed record SourceFetchRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("url")] string Url);

/// <summary>FetchAsync's outcome: source by path, and the paths found nowhere.</summary>
public sealed record FetchResult(IReadOnlyDictionary<string, string> Sources, IReadOnlyList<string> NotFound);

/// <summary>
/// Maps a parsed trace to the few files worth reading and the GitHub requests that fetch them. Paths come
/// from trace text an attacker can shape and end up in URLs, so any ".." segment is dropped; repo forms
/// part of the URL path, so it must match "owner/name".
/// </summary>
public static class SourceFetch
{
    private static readonly Regex PythonFrame = new(@"^\s*File ""(?<path>[^""]+)"", line \d+, in .+$", RegexOptions.Compiled);
    private static readonly Regex GoFrameLine = new(@"^[A-Za-z_]\w*(?:\.\(\*?[A-Za-z_]\w*\))?\.[A-Za-z_]\w*\(", RegexOptions.Compiled);
    private static readonly Regex GoPathLine = new(@"^\s*(?<path>\S+\.go):\d+", RegexOptions.Compiled);
    private static readonly Regex JavaFrame = new(@"^\s*at [\w.$/]+\((?<path>[^:()]+\.java):\d+\)", RegexOptions.Compiled);
    private static readonly Regex DotnetWithLine = new(@"^\s*at [^\s(]+\(.*\) in (?<path>.+):line \d+", RegexOptions.Compiled);
    private static readonly Regex DotnetPlain = new(@"^\s*at [A-Z][\w.<>`\[\]]*\(", RegexOptions.Compiled);
    private static readonly Regex NodeStyleLocation = new(@":\d+:\d+\)\s*$", RegexOptions.Compiled);
    private static readonly Regex NodeFrame = new(@"^\s*at .+? \((?<path>[^)]+):\d+:\d+\)\s*$", RegexOptions.Compiled);

    private static readonly Regex RepoPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    /// <summary>Common container mount points. When several match, the longest is stripped.</summary>
    public static readonly IReadOnlyList<string> DefaultMountPrefixes =
        ["/app/", "/var/task/", "/usr/src/app/", "/home/app/", "/workspace/", "/src/"];

    /// <summary>One GET returning the response text, or null when the resource does not exist.</summary>
    public delegate Task<string?> Get(string url, CancellationToken cancellationToken);

    /// <summary>
    /// Get over HTTP with a bearer token, requesting the raw media type, under which the Contents API returns
    /// the file body. The Trees endpoint ignores that media type and returns JSON.
    /// </summary>
    public static Get Against(HttpClient http, string token) => async (url, cancellationToken) =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("observe-ai");
        request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    };

    /// <summary>
    /// The repository-relative path behind each of trace.Frames, or null for a vendor frame, a frame with no
    /// file, or an unsafe path. rawTrace must be the text TraceParser parsed, since Frame keeps no path. A path
    /// under a known container mount (mountPrefixes, default DefaultMountPrefixes) has the mount stripped.
    /// </summary>
    public static IReadOnlyList<string?> FramePaths(
        ParsedTrace trace, string rawTrace, IReadOnlyList<string>? mountPrefixes = null)
    {
        var lines = rawTrace.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (trace.Runtime == "java")
        {
            var lastCause = Array.FindLastIndex(lines, l => l.TrimStart().StartsWith("Caused by:"));
            if (lastCause >= 0) lines = lines[lastCause..];
        }

        // Matches trace.Frames order: Python's outermost-first lines are reversed.
        var perFrame = trace.Runtime switch
        {
            "python" => Enumerable.Reverse(MatchLines(lines, PythonFrame)).ToList(),
            "java" => MatchLines(lines, JavaFrame),
            "node" => MatchLines(lines, NodeFrame),
            "dotnet" => MatchDotnetLines(lines),
            "go" => MatchGoLines(lines),
            _ => [],
        };

        var prefixes = mountPrefixes ?? DefaultMountPrefixes;
        var paths = new string?[trace.Frames.Count];
        for (var i = 0; i < trace.Frames.Count && i < perFrame.Count; i++)
        {
            if (!trace.Frames[i].InApp || perFrame[i] is not { } raw) continue;
            var path = MapMountPath(raw, prefixes);
            paths[i] = IsSafePath(path) ? path : null;
        }
        return paths;
    }

    /// <summary>The distinct paths from FramePaths in frame order, capped at maxFiles.</summary>
    public static IReadOnlyList<string> PathsFor(
        ParsedTrace trace, string rawTrace, int maxFiles = 5, IReadOnlyList<string>? mountPrefixes = null) =>
        FramePaths(trace, rawTrace, mountPrefixes).OfType<string>().Distinct().Take(maxFiles).ToList();

    /// <summary>The Contents API request for each path at the given ref.</summary>
    public static IReadOnlyList<SourceFetchRequest> RequestsFor(string repo, string commitish, IReadOnlyList<string> paths)
    {
        ValidateRepo(repo);
        return paths.Select(path => new SourceFetchRequest(path, ContentsUrl(repo, commitish, path))).ToList();
    }

    /// <summary>The recursive Git Trees API URL, used to locate a path the Contents API missed.</summary>
    public static string TreeRequest(string repo, string commitish)
    {
        ValidateRepo(repo);
        return $"https://api.github.com/repos/{repo}/git/trees/{Uri.EscapeDataString(commitish)}?recursive=1";
    }

    /// <summary>
    /// The tree path sharing the longest run of trailing segments with wantedPath, as when a monorepo holds
    /// "services/orders/payment.py" for a trace naming "orders/payment.py". Returns null on no match or a tie.
    /// </summary>
    public static string? MatchByBasename(IReadOnlyList<string> treePaths, string wantedPath)
    {
        var wanted = wantedPath.Split('/');
        var matches = treePaths
            .Select(path => (Path: path, Length: CommonSuffixLength(wanted, path.Split('/'))))
            .Where(m => m.Length > 0).ToList();
        if (matches.Count == 0) return null;

        var longest = matches.Max(m => m.Length);
        var winners = matches.Where(m => m.Length == longest).ToList();
        return winners.Count == 1 ? winners[0].Path : null;
    }

    /// <summary>
    /// Fetches each path's source. On a miss it fetches the repository tree once and retries with the best
    /// basename match. get returns null for a 404.
    /// </summary>
    public static async Task<FetchResult> FetchAsync(
        Get get, string repo, string commitish, IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        var sources = new Dictionary<string, string>();
        var notFound = new List<string>();
        IReadOnlyList<string>? tree = null;

        foreach (var request in RequestsFor(repo, commitish, paths))
        {
            var body = await get(request.Url, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                tree ??= await FetchTreeAsync(get, repo, commitish, cancellationToken).ConfigureAwait(false);
                var match = tree is null ? null : MatchByBasename(tree, request.Path);
                body = match is null ? null : await get(ContentsUrl(repo, commitish, match), cancellationToken).ConfigureAwait(false);
            }
            if (body is null) notFound.Add(request.Path); else sources[request.Path] = body;
        }
        return new FetchResult(sources, notFound);
    }

    private static async Task<IReadOnlyList<string>?> FetchTreeAsync(
        Get get, string repo, string commitish, CancellationToken cancellationToken)
    {
        var body = await get(TreeRequest(repo, commitish), cancellationToken).ConfigureAwait(false);
        if (body is null) return null;
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("tree").EnumerateArray()
            .Where(entry => entry.GetProperty("type").GetString() == "blob")
            .Select(entry => entry.GetProperty("path").GetString()!).ToList();
    }

    private static void ValidateRepo(string repo)
    {
        if (!RepoPattern.IsMatch(repo)) throw new ArgumentException($"repo must look like 'owner/name': '{repo}'", nameof(repo));
    }

    private static string ContentsUrl(string repo, string commitish, string path)
    {
        var encodedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return $"https://api.github.com/repos/{repo}/contents/{encodedPath}?ref={Uri.EscapeDataString(commitish)}";
    }

    private static int CommonSuffixLength(string[] a, string[] b)
    {
        var count = 0;
        for (int i = a.Length - 1, j = b.Length - 1; i >= 0 && j >= 0 && a[i] == b[j]; i--, j--) count++;
        return count;
    }

    /// <summary>Strips the longest matching mount prefix, or else a leading slash.</summary>
    private static string MapMountPath(string path, IReadOnlyList<string> mountPrefixes)
    {
        var prefix = mountPrefixes
            .Where(p => path.StartsWith(p, StringComparison.Ordinal))
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();
        if (prefix is not null) return path[prefix.Length..];
        return path.StartsWith('/') ? path[1..] : path;
    }

    private static bool IsSafePath(string path) => path.Length > 0 && !path.Split('/', '\\').Contains("..");

    private static List<string?> MatchLines(string[] lines, Regex frame) =>
        lines.Select(line => frame.Match(line)).Where(m => m.Success).Select(m => (string?)m.Groups["path"].Value).ToList();

    private static List<string?> MatchDotnetLines(string[] lines)
    {
        var result = new List<string?>();
        foreach (var line in lines)
        {
            var withLine = DotnetWithLine.Match(line);
            if (withLine.Success)
            {
                result.Add(withLine.Groups["path"].Value);
                continue;
            }
            if (DotnetPlain.IsMatch(line) && !NodeStyleLocation.IsMatch(line))
            {
                result.Add(null);
            }
        }
        return result;
    }

    private static List<string?> MatchGoLines(string[] lines)
    {
        var result = new List<string?>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!GoFrameLine.IsMatch(lines[i])) continue;
            var path = i + 1 < lines.Length ? GoPathLine.Match(lines[i + 1]) : Match.Empty;
            result.Add(path.Success ? path.Groups["path"].Value : null);
        }
        return result;
    }
}
