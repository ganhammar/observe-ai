using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Checks that traces are never over-merged: a fingerprint covering two
/// defects would hide one inside the other, leaving it unfixed.
/// </summary>
public class FingerprintCollisionTests
{
    [Fact]
    public void TracesThatDifferFingerprintDifferently()
    {
        Dictionary<string, List<EvalFixture.Row>> byFingerprint = [];
        foreach (var row in EvalFixture.Load())
        {
            var trace = TraceParser.Parse(row.StackTrace);
            if (trace is null) continue;
            var fingerprint = Fingerprint.Compute(trace);
            byFingerprint.TryAdd(fingerprint, []);
            byFingerprint[fingerprint].Add(row);
        }

        // Rows may share a fingerprint only when their traces are the same text;
        // the fixture holds one such pair, the same pool exhaustion reported
        // once as the database running out of connections and once as this
        // service leaking them, which the trace text alone does not distinguish.
        var wrong = byFingerprint.Values
            .Where(group => group.Select(row => row.StackTrace.Trim()).Distinct().Count() > 1)
            .Select(group => string.Join(", ", group.Select(row => row.Id)))
            .ToList();

        Assert.True(wrong.Count == 0, "different traces share a fingerprint:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void IdenticalTracesWithDifferentCausesStillGroupTogether()
    {
        // Triage runs on first sighting, so whichever of these two arrives first
        // decides the verdict for both, and separating them needs the evidence,
        // which the fingerprint does not read.
        var rows = EvalFixture.Load().ToDictionary(row => row.Id);
        var downstream = TraceParser.Parse(rows["dn-04"].StackTrace);
        var ourLeak = TraceParser.Parse(rows["am-05"].StackTrace);

        Assert.NotNull(downstream);
        Assert.NotNull(ourLeak);
        Assert.Equal(Fingerprint.Compute(downstream!), Fingerprint.Compute(ourLeak!));
    }

    [Fact]
    public void NoLibraryFrameReadsAsApplicationCode()
    {
        // Every third-party namespace the fixture contains is covered by the
        // fallback vendor list, so the top in-app frame is the caller's own
        // code and the fingerprint groups by that frame; the list covers only
        // this fixture, so a namespace outside it would not be caught here.
        var misattributed = EvalFixture.Load()
            .Select(row => (row.Id, Top: TraceParser.Parse(row.StackTrace)?.Frames.FirstOrDefault(f => f.InApp)))
            .Where(pair => pair.Top is not null && IsThirdParty(pair.Top.Method))
            .Select(pair => $"{pair.Id}: {pair.Top!.Method}")
            .ToList();

        Assert.True(misattributed.Count == 0,
            "library frames read as application code:\n" + string.Join("\n", misattributed));
    }

    private static bool IsThirdParty(string method) =>
        method.StartsWith("Npgsql.") || method.StartsWith("okhttp3.") || method.StartsWith("Polly.");
}
