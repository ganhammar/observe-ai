using System.Text.RegularExpressions;

namespace ObserveAi;

/// <summary>
/// Turns a parsed trace back into the minimal set of files worth reading, and
/// builds the git commands that fetch only those.
///
/// TraceParser's Frame carries only the method, never a path, since fingerprinting
/// never needed one. Recovering a path means reading the same raw trace text a
/// second time with a path-capturing counterpart to each of TraceParser's frame
/// regexes, rather than growing Frame past what fingerprinting needs.
///
/// Every input here originates somewhere an attacker can reach: paths come from
/// stack trace text, which anyone able to trigger a log line can shape; repo and
/// commitish come from a resource tag or a model call. Git treats a leading
/// dash as an option rather than a value, so a crafted "--upload-pack=..." frame
/// or ref is remote code execution, not a formatting nuisance, and a ".."
/// segment can walk a sparse-checkout pattern outside the paths meant to be
/// fetched. Both are dropped rather than run. A container-absolute path is not
/// itself a threat, so it is mapped to a repository-relative guess instead of
/// dropped; repo and commitish are single values with no safe partial form, so
/// a bad one throws instead of being silently cleaned up.
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
    private static readonly Regex ShaCommitish = new(@"^[0-9a-fA-F]{7,40}$", RegexOptions.Compiled);
    private static readonly Regex RefCommitish = new(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.Compiled);

    /// <summary>Common container mount points, longest first among any that match, so a caller can add its own without losing these.</summary>
    public static readonly IReadOnlyList<string> DefaultMountPrefixes =
        ["/app/", "/var/task/", "/usr/src/app/", "/home/app/", "/workspace/", "/src/"];

    /// <summary>
    /// The distinct file paths behind the trace's in-app frames, in frame order,
    /// capped at maxFiles. rawTrace must be the same text TraceParser parsed into
    /// trace; it is needed here because Frame does not retain a path to read back.
    /// A .NET frame with no "in ...:line N" clause carries no path at all and
    /// contributes nothing, same as a Java frame contributes a bare filename
    /// rather than a full repository path: both are the raw trace text telling
    /// the truth about what it does and does not know.
    ///
    /// A path rooted at a known container mount (mountPrefixes, defaulting to
    /// DefaultMountPrefixes) has that mount stripped to guess the repository-
    /// relative path; one still rooted afterwards has the leading slash dropped
    /// as a best effort, since a sparse-checkout pattern that matches nothing in
    /// the repo just costs that one file rather than failing the checkout. What
    /// survives mapping is checked again: a result starting with "-" or
    /// containing ".." is dropped, since the trace text is attacker-shaped, not
    /// trusted input, and no mount-prefix guess should be allowed to reintroduce
    /// either.
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

    /// <summary>
    /// The three argument lists for a blobless sparse checkout: clone with no
    /// blob content, narrow the sparse set to just the frame paths, then move to
    /// the commit the log line ran at. repo is "owner/name", never a URL, so the
    /// URL is built here instead of accepted from a caller, and a "--"
    /// end-of-options marker guards every positional argument against flag
    /// smuggling. Returned as data rather than executed, so this stays testable
    /// with no process or network dependency; the caller supplies the process
    /// runner later.
    /// </summary>
    public static IReadOnlyList<string[]> CloneCommands(string repo, string commitish, IReadOnlyList<string> paths)
    {
        if (!RepoPattern.IsMatch(repo))
        {
            throw new ArgumentException($"repo must look like 'owner/name': '{repo}'", nameof(repo));
        }
        if (!ShaCommitish.IsMatch(commitish) && !IsSafeRef(commitish))
        {
            throw new ArgumentException($"commitish is not a commit sha or a safe ref name: '{commitish}'", nameof(commitish));
        }

        var url = $"https://github.com/{repo}.git";
        return
        [
            ["clone", "--depth", "1", "--filter=blob:none", "--sparse", "--", url],
            ["sparse-checkout", "set", "--", ..paths],
            ["checkout", "--", commitish],
        ];
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

    private static bool IsSafePath(string path) =>
        path.Length > 0 && path[0] != '-' && !path.Split('/', '\\').Contains("..");

    private static bool IsSafeRef(string value) =>
        RefCommitish.IsMatch(value) && !value.Contains("..") && !value.EndsWith('/') && !value.EndsWith(".lock");

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
