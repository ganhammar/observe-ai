namespace ObserveAi;

/// <summary>The kind of AWS compute resource a log group name identifies.</summary>
public enum LogGroupKind { Lambda, Ecs, Eks }

/// <summary>A log group name reduced to the owning resource's kind and name.</summary>
public sealed record ParsedLogGroup(LogGroupKind Kind, string ResourceName);

/// <summary>
/// Turns a log group name and a parsed stack trace into what the pipeline needs to find a repository,
/// without an AWS call. One deployment watches every log group in the account, so this recognises only
/// the shapes AWS imposes on log group names and stack frames and holds no table of known services.
/// </summary>
public static class ServiceIdentity
{
    /// <summary>
    /// Recognises the Lambda, ECS and EKS log group conventions. Returns null for anything else, including a
    /// recognised prefix with no resource name after it.
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

    /// <summary>
    /// The owning repository by convention: the GitHub organisation plus the log group's resource name
    /// (/aws/lambda/billing-sync -> {org}/billing-sync). Returns null for an unrecognised log group, so the
    /// state machine stops at UnknownRepo. The result is wrong for a service named differently from its repository.
    /// </summary>
    public static string? ConventionalRepo(string logGroupName, string githubOrg)
    {
        var logGroup = ParseLogGroup(logGroupName);
        return logGroup is null ? null : $"{githubOrg}/{logGroup.ResourceName}";
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
    /// The namespace of the top in-app frame: everything through the last dot before the class and method
    /// segments. Null for a method with fewer than three segments, the usual case for node, python and go.
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
