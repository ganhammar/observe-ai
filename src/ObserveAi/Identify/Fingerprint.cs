using System.Security.Cryptography;
using System.Text;

namespace ObserveAi;

/// <summary>
/// Collapses a parsed trace down to a stable identifier for "this defect".
///
/// The triage tree costs nine Bedrock calls, so it has to run per distinct
/// failure rather than per log line. This is the deterministic step that turns
/// thousands of log lines into the handful of distinct failures actually worth
/// spending those calls on.
/// </summary>
public static class Fingerprint
{
    /// <summary>
    /// The plain-text string the hash is computed over. Storing this alongside
    /// the hash is what lets a human look at two records and see why they were
    /// grouped together, instead of trusting an opaque hash to have done it right.
    /// </summary>
    public static string Signature(ParsedTrace trace, int frames = 5)
    {
        var inApp = trace.Frames.Where(f => f.InApp).Select(f => f.Method).Take(frames).ToList();

        // A trace that never leaves a framework (every frame is vendor code)
        // still represents one distinct failure and still needs to group, so
        // fall back to the first N frames of any kind rather than none at all.
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
