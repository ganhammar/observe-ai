namespace ObserveAi;

/// <summary>
/// Turns a log group name into the repository that owns it, without an AWS call. One deployment watches
/// every log group in the account, so this recognises only the shapes AWS imposes on log group names and
/// holds no table of known services.
/// </summary>
public static class ServiceIdentity
{
    /// <summary>
    /// The resource name from a Lambda, ECS or EKS log group. Returns null for anything else, including a
    /// recognised prefix with no resource name after it.
    /// </summary>
    public static string? ParseLogGroup(string logGroupName) =>
        TryStrip(logGroupName, "/aws/lambda/", out var name)
        || TryStrip(logGroupName, "/aws/ecs/", out name)
        || TryStrip(logGroupName, "/ecs/", out name)
        || TryStrip(logGroupName, "/aws/eks/", out name)
            ? name
            : null;

    /// <summary>
    /// The owning repository by convention: the GitHub organisation plus the log group's resource name
    /// (/aws/lambda/billing-sync -> {org}/billing-sync). Returns null for an unrecognised log group, so the
    /// state machine stops at UnknownRepo. The result is wrong for a service named differently from its repository.
    /// </summary>
    public static string? ConventionalRepo(string logGroupName, string githubOrg)
    {
        var resourceName = ParseLogGroup(logGroupName);
        return resourceName is null ? null : $"{githubOrg}/{resourceName}";
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
}
