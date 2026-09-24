namespace ObserveAi;

/// <summary>The AWS compute shape a log group name identifies, enough to know what kind of resource owns its tags.</summary>
public enum LogGroupKind { Lambda, Ecs, Eks }

/// <summary>A log group name reduced to what a tag lookup needs: the resource's kind and its own name.</summary>
public sealed record ParsedLogGroup(LogGroupKind Kind, string ResourceName);

/// <summary>
/// Turns AWS-owned identifiers, a log group name and a parsed stack trace, into the
/// two pieces RepoResolver needs to find a repository, without making an AWS call
/// itself. One deployment watches every log group in the account, so nothing here
/// can be a lookup table of known services; it only recognises shapes that AWS
/// itself imposes on log group names and stack frames.
/// </summary>
public static class ServiceIdentity
{
    /// <summary>
    /// Recognises the log group naming conventions of Lambda, ECS and EKS. Returns
    /// null for anything else, including a recognised prefix with no name after it,
    /// since RepoResolver's tag step has nothing to build an ARN from either way.
    /// </summary>
    public static ParsedLogGroup? ParseLogGroup(string logGroupName)
    {
        if (TryStrip(logGroupName, "/aws/lambda/", out var lambdaName))
        {
            return new ParsedLogGroup(LogGroupKind.Lambda, lambdaName);
        }
        if (TryStrip(logGroupName, "/aws/ecs/", out var ecsName) || TryStrip(logGroupName, "/ecs/", out ecsName))
        {
            return new ParsedLogGroup(LogGroupKind.Ecs, ecsName);
        }
        if (TryStrip(logGroupName, "/aws/eks/", out var eksName))
        {
            return new ParsedLogGroup(LogGroupKind.Eks, eksName);
        }
        return null;
    }

    private static bool TryStrip(string logGroupName, string prefix, out string rest)
    {
        if (logGroupName.StartsWith(prefix, StringComparison.Ordinal) && logGroupName.Length > prefix.Length)
        {
            rest = logGroupName[prefix.Length..];
            return true;
        }
        rest = "";
        return false;
    }

    /// <summary>
    /// The namespace owning the top in-app frame: everything up to and including
    /// the last dot before the final two segments (the class and the method
    /// itself). A method with fewer than three segments has nothing to report,
    /// which is the ordinary case for node, python, and often go, none of which
    /// carry an organisation-style dotted namespace on every frame.
    ///
    /// Frames are taken exactly as TraceParser produced them: this only reads
    /// InApp, it does not decide it.
    /// </summary>
    public static string? NamespacePrefix(ParsedTrace trace)
    {
        var topInApp = trace.Frames.FirstOrDefault(frame => frame.InApp);
        if (topInApp is null)
        {
            return null;
        }

        var segments = topInApp.Method.Split('.');
        return segments.Length < 3 ? null : string.Join('.', segments[..^2]) + ".";
    }
}
