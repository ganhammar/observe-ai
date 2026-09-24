using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ObserveAi;

/// <summary>One file's GitHub Contents API request: Path is repository-relative, Url is ready to GET.</summary>
public sealed record SourceFetchRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("url")] string Url);

/// <summary>What FetchAsync found: the source per path it resolved, and the paths that were not found anywhere.</summary>
public sealed record FetchResult(IReadOnlyDictionary<string, string> Sources, IReadOnlyList<string> NotFound);

/// <summary>
/// Turns a parsed trace back into the minimal set of files worth reading, and
/// the GitHub API requests that fetch only those. Every path comes from stack
/// trace text an attacker can shape and ends up in a URL, so a ".." segment is
/// dropped; repo goes into the URL path itself, so it must match "owner/name".
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

    /// <summary>Common container mount points, longest first among any that match, so a caller can add its own without losing these.</summary>
    public static readonly IReadOnlyList<string> DefaultMountPrefixes =
        ["/app/", "/var/task/", "/usr/src/app/", "/home/app/", "/workspace/", "/src/"];

    /// <summary>
    /// The distinct file paths behind the trace's in-app frames, in frame order,
    /// capped at maxFiles. rawTrace must be the text TraceParser parsed into
    /// trace, since Frame does not retain a path to read back. A path rooted at a
    /// known container mount (mountPrefixes, defaulting to DefaultMountPrefixes)
    /// has that mount stripped to guess the repository-relative path.
    /// </summary>
    public static IReadOnlyList<string> PathsFor(
        ParsedTrace trace, string rawTrace, int maxFiles = 5, IReadOnlyList<string>? mountPrefixes = null)
    {
        var lines = rawTrace.Replace("\r\n", "\n").Split('\n');
        if (trace.Runtime == "java")
        {
            var lastCause = Array.FindLastIndex(lines, l => l.TrimStart().StartsWith("Caused by:"));
            if (lastCause >= 0) lines = lines[lastCause..];
        }

        var perFrame = trace.Runtime switch
        {
            "python" => MatchLines(lines, PythonFrame),
            "java" => MatchLines(lines, JavaFrame),
            "node" => MatchLines(lines, NodeFrame),
            "dotnet" => MatchDotnetLines(lines),
            "go" => MatchGoLines(lines),
            _ => [],
        };

        var prefixes = mountPrefixes ?? DefaultMountPrefixes;
        var paths = new List<string>();
        for (var i = 0; i < trace.Frames.Count && i < perFrame.Count; i++)
        {
            if (!trace.Frames[i].InApp || perFrame[i] is not { } raw) continue;
            var path = MapMountPath(raw, prefixes);
            if (!IsSafePath(path) || paths.Contains(path)) continue;
            paths.Add(path);
            if (paths.Count == maxFiles) break;
        }
        return paths;
    }

    /// <summary>The Contents API request for each path at the given ref, returned as data for the caller's HTTP delegate to run.</summary>
    public static IReadOnlyList<SourceFetchRequest> RequestsFor(string repo, string commitish, IReadOnlyList<string> paths)
    {
        ValidateRepo(repo);
        return paths.Select(path => new SourceFetchRequest(path, ContentsUrl(repo, commitish, path))).ToList();
    }

    /// <summary>The recursive Git Trees API request, used to find a path the Contents API could not resolve directly.</summary>
    public static string TreeRequest(string repo, string commitish)
    {
        ValidateRepo(repo);
        return $"https://api.github.com/repos/{repo}/git/trees/{Uri.EscapeDataString(commitish)}?recursive=1";
    }

    /// <summary>
    /// The tree path with the longest run of segments matching wantedPath from the
    /// filename backwards (a monorepo holds "services/orders/payment.py" for a
    /// trace naming "orders/payment.py"), or null on no match or a tie, since
    /// guessing between two candidates is worse than reporting none found.
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
    /// Fetches each path's source, falling back once to a tree lookup and retry
    /// when the direct request misses. get performs one GET and returns null for
    /// a 404; nothing here touches HTTP itself, so a test can supply a fake.
    /// </summary>
    public static async Task<FetchResult> FetchAsync(
        Func<string, CancellationToken, Task<string?>> get, string repo, string commitish, IReadOnlyList<string> paths,
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
        Func<string, CancellationToken, Task<string?>> get, string repo, string commitish, CancellationToken cancellationToken)
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

    /// <summary>Strips the longest matching mount prefix, or failing that a lone leading slash, to guess a repo-relative path.</summary>
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
