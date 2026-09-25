using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ObserveAi;

/// <summary>A single stack frame: the method it names, and whether that method is this service's own code.</summary>
public sealed record Frame(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("inApp")] bool InApp);

/// <summary>A trace reduced to what fingerprinting needs: the runtime, the exception type, and the call stack.</summary>
public sealed record ParsedTrace(string Runtime, string ExceptionType, IReadOnlyList<Frame> Frames);

/// <summary>
/// Parses raw stack traces from five runtimes into a common shape. The method name sits on one
/// line in all of them, so one frame regex with a "method" capture group per runtime is enough;
/// exception-type extraction is the only real difference, so a sixth runtime is a row in Specs,
/// not a class. Returns null for anything unrecognised; the model is the right fallback for
/// that, but is not built here since it needs a Bedrock call this parser has no business making.
/// </summary>
public static class TraceParser
{
    // Fallback for when the caller has no per-service app-code prefix list.
    //
    // A list like this cannot be complete: there are more libraries than anyone
    // will enumerate, and a library that is missing from it reads as application
    // code, which makes the fingerprint group by the library rather than by the
    // code that called it. Two unrelated defects failing inside the same client
    // then merge. Resolving application namespaces through a cached model call
    // is the real answer; these entries only cover what the fixture proved.
    private static readonly string[] VendorPrefixes =
    [
        "System.", "Microsoft.", "java.", "javax.", "jdk.", "sun.",
        "org.springframework.", "com.fasterxml.", "node:", "runtime.",
        "Npgsql.", "Polly.", "okhttp3.", "elasticsearch", "botocore.",
        "psycopg", "axios", "nodemailer", "org.apache.kafka.",
    ];

    private static readonly string[] GoPanicMarkers = ["panic: ", "fatal error: "];

    private sealed record RuntimeSpec(string Name, Func<string[], bool> Detect, Func<string, Match> MatchFrame, Func<string[], string> ExceptionType);

    private static readonly Regex PythonFrame = new(@"^\s*File ""[^""]+"", line \d+, in (?<method>.+)$", RegexOptions.Compiled);
    private static readonly Regex GoFrame = new(@"^(?<method>[A-Za-z_]\w*(?:\.\(\*?[A-Za-z_]\w*\))?\.[A-Za-z_]\w*)\(", RegexOptions.Compiled);
    private static readonly Regex JavaFrame = new(@"^\s*at (?<method>[\w.$/]+)\(.*\.java:\d+\)", RegexOptions.Compiled);
    private static readonly Regex DotnetFrameWithLine = new(@"^\s*at (?<method>[^\s(]+)\(.*\) in .+:line \d+", RegexOptions.Compiled);
    private static readonly Regex DotnetFramePlain = new(@"^\s*at (?<method>[A-Z][\w.<>`\[\]]*)\(", RegexOptions.Compiled);
    private static readonly Regex NodeStyleLocation = new(@":\d+:\d+\)\s*$", RegexOptions.Compiled);
    private static readonly Regex NodeFrame = new(@"^\s*at (?<method>.+?) \(.*:\d+:\d+\)\s*$", RegexOptions.Compiled);

    // Order matters: python and go have unambiguous marker lines, checked first, then java's
    // ".java:" source reference. dotnet is checked before node because Node's built-in callback
    // classes (GetAddrInfoReqWrap and friends) are PascalCase and would otherwise pass for a
    // .NET frame; DotnetFramePlain excludes anything already shaped like a Node location instead.
    private static readonly RuntimeSpec[] Specs =
    [
        new("python", lines => lines.Any(l => l.Contains("Traceback (most recent call last)")), PythonFrame.Match, ExtractPythonType),
        new("go", lines => lines.Any(l => l.StartsWith("goroutine ") || l.StartsWith("panic:") || l.StartsWith("fatal error:")), GoFrame.Match, ExtractGoType),
        new("java", lines => lines.Any(JavaFrame.IsMatch), JavaFrame.Match, ExtractJavaType),
        new("dotnet", lines => lines.Any(l => MatchDotnetFrame(l).Success), MatchDotnetFrame, ExtractFirstLineType),
        new("node", lines => lines.Any(NodeFrame.IsMatch), NodeFrame.Match, ExtractFirstLineType),
    ];

    public static ParsedTrace? Parse(string text, IReadOnlyCollection<string> appPrefixes)
    {
        // The Python Lambda runtime joins a traceback's lines with a bare carriage return.
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var spec = Specs.FirstOrDefault(s => s.Detect(lines));
        if (spec is null) return null;

        // Half of real Java traces wrap the thing that actually broke; scoping to the last
        // "Caused by:" keeps the fingerprint on the real defect instead of the wrapper.
        var scoped = spec.Name == "java" ? SliceAtLastCause(lines) : lines;
        var exceptionType = spec.ExceptionType(scoped);

        var frames = new List<Frame>();
        foreach (var line in scoped)
        {
            var match = spec.MatchFrame(line);
            if (!match.Success) continue;

            var method = Normalize(match.Groups["method"].Value);
            frames.Add(new Frame(method, IsInApp(method, line, appPrefixes)));
        }
        // Frames[0] is the throw site for every runtime. Python is the one that
        // prints its traceback outermost first, so it is reversed to match.
        if (spec.Name == "python") frames.Reverse();

        return new ParsedTrace(spec.Name, exceptionType, frames);
    }

    private static Match MatchDotnetFrame(string line)
    {
        var withLine = DotnetFrameWithLine.Match(line);
        if (withLine.Success) return withLine;

        var plain = DotnetFramePlain.Match(line);
        return plain.Success && !NodeStyleLocation.IsMatch(line) ? plain : Match.Empty;
    }

    private static string[] SliceAtLastCause(string[] lines)
    {
        var lastCause = Array.FindLastIndex(lines, l => l.TrimStart().StartsWith("Caused by:"));
        return lastCause < 0 ? lines : lines[lastCause..];
    }

    private static string BeforeFirstColon(string text) => text.Split(':')[0].Trim();

    // A native Python traceback ends with the exception line. The Python Lambda
    // runtime logs it first instead, prefixed with "[ERROR]", and ends with the
    // innermost source line.
    private static string ExtractPythonType(string[] lines)
    {
        var first = lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return first.StartsWith("[ERROR] ", StringComparison.Ordinal)
            ? BeforeFirstColon(first["[ERROR] ".Length..])
            : BeforeFirstColon(lines.LastOrDefault(l => l.Trim().Length > 0) ?? "");
    }

    // Go is the one runtime whose exception type carries a message, and "index out
    // of range [5] with length 3" would otherwise fingerprint separately for every
    // index the defect happens to hit. The message is kept because it separates one
    // panic kind from another, with the varying parts replaced so it still groups.
    private static readonly Regex Quoted = new("\"[^\"]*\"|'[^']*'", RegexOptions.Compiled);
    private static readonly Regex Hex = new(@"\b0x[0-9a-fA-F]+\b", RegexOptions.Compiled);
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    private static string NormaliseMessage(string message) =>
        Digits.Replace(Hex.Replace(Quoted.Replace(message, "<s>"), "<x>"), "<n>");

    private static string ExtractGoType(string[] lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var marker = GoPanicMarkers.FirstOrDefault(trimmed.StartsWith);
            if (marker is not null) return NormaliseMessage(trimmed[marker.Length..].Trim());
        }

        // A bare "goroutine " dump with no panic/fatal error line: fall back to the first line.
        return BeforeFirstColon(lines.FirstOrDefault() ?? "");
    }

    private static string ExtractJavaType(string[] lines)
    {
        var first = (lines.FirstOrDefault() ?? "").TrimStart();
        return BeforeFirstColon(first.StartsWith("Caused by:") ? first["Caused by:".Length..].Trim() : first);
    }

    private static string ExtractFirstLineType(string[] lines) => BeforeFirstColon(lines.FirstOrDefault() ?? "");

    // node_modules is a path, not part of the captured method name, so it is checked against
    // the raw line rather than against the method itself.
    private static bool IsInApp(string method, string line, IReadOnlyCollection<string> appPrefixes) =>
        appPrefixes.Count > 0
            ? appPrefixes.Any(method.StartsWith)
            : !VendorPrefixes.Any(method.StartsWith) && !line.Contains("node_modules");

    // Strips decorations that vary between occurrences of the same call site: generic arity and
    // type arguments, .NET's async state-machine and closure naming, and Java's lambda suffix.
    // Without this the same defect fingerprints differently every time it fires.
    private static string Normalize(string method)
    {
        method = Regex.Replace(method, @"<(\w+)>d__\d+\.MoveNext", "$1");
        method = Regex.Replace(method, @"<>c__DisplayClass\d+_\d+\.?", "");
        method = Regex.Replace(method, @"<(\w+)>b__\d+", "$1");
        method = Regex.Replace(method, @"`\d+", "");
        method = Regex.Replace(method, @"\[[^\]]*\]", "");
        method = Regex.Replace(method, @"lambda\$(\w+)\$\d+", "lambda$$$1");
        return method;
    }
}
