using System.Security.Cryptography;
using System.Text;

namespace ObserveAi;

/// <summary>
/// Collapses a parsed trace to a stable identifier for one defect. The triage tree costs up to nine Bedrock
/// calls, so it runs once per fingerprint.
/// </summary>
public static class Fingerprint
{
    private const int Frames = 5;

    /// <summary>The plain-text string the hash covers, stored beside the hash so a reader can see why two records grouped.</summary>
    public static string Signature(ParsedTrace trace)
    {
        var inApp = trace.Frames.Where(f => f.InApp).Select(f => f.Method).Take(Frames).ToList();

        // A trace with no in-app frame still groups, on its first N frames of any kind.
        var chosen = inApp.Count > 0 ? inApp : trace.Frames.Select(f => f.Method).Take(Frames).ToList();

        return $"{trace.Runtime}|{trace.ExceptionType}|{string.Join(">", chosen)}";
    }

    /// <summary>The first 16 hex characters of the signature's SHA-256, used as the fingerprint id.</summary>
    public static string Compute(ParsedTrace trace)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Signature(trace)));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
