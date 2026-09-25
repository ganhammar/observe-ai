using System.Security.Cryptography;
using System.Text;

namespace ObserveAi;

/// <summary>
/// Collapses a parsed trace to a stable identifier for one defect. The triage tree costs nine Bedrock
/// calls, so it runs once per fingerprint.
/// </summary>
public static class Fingerprint
{
    /// <summary>The plain-text string the hash covers, stored beside the hash so a reader can see why two records grouped.</summary>
    public static string Signature(ParsedTrace trace, int frames = 5)
    {
        var inApp = trace.Frames.Where(f => f.InApp).Select(f => f.Method).Take(frames).ToList();

        // A trace with no in-app frame still groups, on its first N frames of any kind.
        var chosen = inApp.Count > 0 ? inApp : trace.Frames.Select(f => f.Method).Take(frames).ToList();

        return $"{trace.Runtime}|{trace.ExceptionType}|{string.Join(">", chosen)}";
    }

    /// <summary>The first 16 hex characters of the signature's SHA-256, used as the fingerprint id.</summary>
    public static string Compute(ParsedTrace trace, int frames = 5)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Signature(trace, frames)));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
