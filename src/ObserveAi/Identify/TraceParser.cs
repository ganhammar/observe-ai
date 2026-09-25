using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ObserveAi;

/// <summary>A single stack frame: the method it names, and whether that method is this service's own code.</summary>
public sealed record Frame(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("inApp")] bool InApp);

/// <summary>A trace reduced to what fingerprinting needs. Frames[0] is the throw site for every runtime.</summary>
public sealed record ParsedTrace(string Runtime, string ExceptionType, IReadOnlyList<Frame> Frames);

/// <summary>
/// Parses raw stack traces from five runtimes into a common shape. Each runtime is one row in Specs: a
/// detector, a frame regex with a "method" group, and an exception-type extractor. Returns null for an
/// unrecognised trace.
/// </summary>
public static class TraceParser
{
    // A library missing here counts as app code and merges its callers' defects.
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
    private static readonly Regex GoFrame = new(@"^(?<method>[A-Za-z_][\w./-]*(?:\.\(\*?[A-Za-z_]\w*\))?\.[A-Za-z_]\w*)\(", RegexOptions.Compiled);
    private static readonly Regex JavaFrame = new(@"^\s*at (?<method>[\w.$/]+)\(.*\.java:\d+\)", RegexOptions.Compiled);
    private static readonly Regex DotnetFrameWithLine = new(@"^\s*at (?<method>[^\s(]+)\(.*\) in .+:line \d+", RegexOptions.Compiled);
    private static readonly Regex DotnetFramePlain = new(@"^\s*at (?<method>[A-Z][\w.<>`\[\]]*)\(", RegexOptions.Compiled);
    private static readonly Regex NodeStyleLocation = new(@":\d+:\d+\)\s*$", RegexOptions.Compiled);
    private static readonly Regex NodeFrame = new(@"^\s*at (?<method>.+?) \(.*:\d+:\d+\)\s*$", RegexOptions.Compiled);

    // First match wins. dotnet runs before node, and MatchDotnetFrame rejects Node-style locations, because
    // Node's PascalCase callback classes (GetAddrInfoReqWrap) otherwise pass for .NET frames.
    private static readonly RuntimeSpec[] Specs =
    [
        new("python", lines => lines.Any(l => l.Contains("Traceback (most recent call last)")), PythonFrame.Match, ExtractPythonType),
        new("go", lines => lines.Any(l => l.StartsWith("goroutine ") || l.StartsWith("panic:") || l.StartsWith("fatal error:")), GoFrame.Match, ExtractGoType),
        new("java", lines => lines.Any(JavaFrame.IsMatch), JavaFrame.Match, ExtractJavaType),
        new("dotnet", lines => lines.Any(l => MatchDotnetFrame(l).Success), MatchDotnetFrame, ExtractFirstLineType),
        new("node", lines => lines.Any(NodeFrame.IsMatch), NodeFrame.Match, ExtractFirstLineType),
    ];

    public static ParsedTrace? Parse(string text)
    {
        // The Python Lambda runtime joins a traceback's lines with a bare carriage return.
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var spec = Specs.FirstOrDefault(s => s.Detect(lines));
        if (spec is null) return null;

        // About half of real Java traces wrap the defect, so frames are read from the last "Caused by:".
        var scoped = spec.Name == "java" ? SliceAtLastCause(lines) : lines;
        var exceptionType = spec.ExceptionType(scoped);

        var frames = new List<Frame>();
        foreach (var line in scoped)
        {
            var match = spec.MatchFrame(line);
            if (!match.Success) continue;

            var method = Normalize(match.Groups["method"].Value);
            frames.Add(new Frame(method, IsInApp(method, line)));
        }
        // Frames[0] is the throw site. Python prints outermost first, so its frames are reversed.
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

    // A native Python traceback ends with the exception line. The Lambda runtime logs that line first,
    // prefixed "[ERROR]", and ends with the innermost source line.
    private static string ExtractPythonType(string[] lines)
    {
        var first = lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return first.StartsWith("[ERROR] ", StringComparison.Ordinal)
            ? BeforeFirstColon(first["[ERROR] ".Length..])
            : BeforeFirstColon(lines.LastOrDefault(l => l.Trim().Length > 0) ?? "");
    }

    // Go's exception type is the panic message, with strings, hex and digits masked so it groups across values.
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

        // A bare goroutine dump with no panic or fatal error line uses its first line.
        return BeforeFirstColon(lines.FirstOrDefault() ?? "");
    }

    private static string ExtractJavaType(string[] lines)
    {
        var first = (lines.FirstOrDefault() ?? "").TrimStart();
        return BeforeFirstColon(first.StartsWith("Caused by:") ? first["Caused by:".Length..].Trim() : first);
    }

    private static string ExtractFirstLineType(string[] lines) => BeforeFirstColon(lines.FirstOrDefault() ?? "");

    // node_modules is part of the path, so it is matched against the raw line.
    private static bool IsInApp(string method, string line) =>
        !VendorPrefixes.Any(method.StartsWith) && !line.Contains("node_modules");

    // Strips names that vary between occurrences of one call site: generics, .NET async and closure names, Java lambdas.
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
