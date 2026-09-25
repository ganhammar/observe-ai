using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Over-merging is the failure mode that matters: a fingerprint covering two
/// defects hides one inside the other, and the hidden one is never fixed.
/// </summary>
public class FingerprintCollisionTests
{
    [Fact]
    public void TracesThatDifferFingerprintDifferently()
    {
        Dictionary<string, List<EvalFixture.Row>> byFingerprint = [];
        foreach (var row in EvalFixture.Load())
        {
            var trace = TraceParser.Parse(row.StackTrace, []);
            if (trace is null) continue;
            var fingerprint = Fingerprint.Compute(trace);
            byFingerprint.TryAdd(fingerprint, []);
            byFingerprint[fingerprint].Add(row);
        }

        // Rows sharing a fingerprint are only acceptable when their traces are
        // genuinely the same text. The fixture contains one such pair on purpose:
        // the same pool exhaustion reported once as the database running out of
        // connections and once as this service leaking them. Nothing in the trace
        // separates those, which is a property of stack traces, not of hashing.
        var wrong = byFingerprint.Values
            .Where(group => group.Select(row => row.StackTrace.Trim()).Distinct().Count() > 1)
            .Select(group => string.Join(", ", group.Select(row => row.Id)))
            .ToList();

        Assert.True(wrong.Count == 0, "different traces share a fingerprint:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void IdenticalTracesWithDifferentCausesStillGroupTogether()
    {
        // The consequence of the above, asserted so it cannot regress unnoticed:
        // triage runs on first sighting, so whichever of these arrives first
        // decides the verdict for both. Separating them needs the evidence, which
        // the fingerprint deliberately does not read.
        var rows = EvalFixture.Load().ToDictionary(row => row.Id);
        var downstream = TraceParser.Parse(rows["dn-04"].StackTrace, []);
        var ourLeak = TraceParser.Parse(rows["am-05"].StackTrace, []);

        Assert.NotNull(downstream);
        Assert.NotNull(ourLeak);
        Assert.Equal(Fingerprint.Compute(downstream!), Fingerprint.Compute(ourLeak!));
    }

    [Fact]
    public void NoLibraryFrameReadsAsApplicationCode()
    {
        // Every third party namespace the fixture contains is now covered by the
        // fallback vendor list, so the top in-app frame is the caller's own code
        // and the fingerprint groups by that rather than by the client it failed
        // inside. This passes because the list happens to cover this fixture. It
        // is not evidence the approach scales, which is why the list carries a
        // comment pointing at the cached model call instead.
        var misattributed = EvalFixture.Load()
            .Select(row => (row.Id, Top: TraceParser.Parse(row.StackTrace, [])?.Frames.FirstOrDefault(f => f.InApp)))
            .Where(pair => pair.Top is not null && IsThirdParty(pair.Top.Method))
            .Select(pair => $"{pair.Id}: {pair.Top!.Method}")
            .ToList();

        Assert.True(misattributed.Count == 0,
            "library frames read as application code:\n" + string.Join("\n", misattributed));
    }

    private static bool IsThirdParty(string method) =>
        method.StartsWith("Npgsql.") || method.StartsWith("okhttp3.") || method.StartsWith("Polly.");
}
