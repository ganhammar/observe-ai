using System.Text.Json;
using ObserveAi;

namespace ObserveAi.Tests;

public class EvidenceSignatureTests
{
    [Fact]
    public void TheSameShapeWithDifferentValuesProducesTheSameSignature()
    {
        var dbExhausted = Parse("""{"pool_in_use_over_run": [4, 19, 47], "connections_opened": 2841, "leaking": false}""");
        var laterIncident = Parse("""{"pool_in_use_over_run": [83, 100, 100, 100], "connections_opened": 12, "leaking": true}""");

        Assert.Equal(EvidenceSignature.Compute(dbExhausted), EvidenceSignature.Compute(laterIncident));
    }

    [Fact]
    public void KeyOrderDoesNotAffectTheSignature()
    {
        var a = Parse("""{"connections_opened": 1, "pool_in_use_over_run": [1]}""");
        var b = Parse("""{"pool_in_use_over_run": [9, 9], "connections_opened": 2}""");

        Assert.Equal(EvidenceSignature.Compute(a), EvidenceSignature.Compute(b));
    }

    [Fact]
    public void AChangedKeySetProducesADifferentSignature()
    {
        // dn-04 and am-05 are the fixture rows this guards: byte-identical stack
        // traces, but only one of them carries an evidence object at all.
        var withoutEvidence = Parse("""{"message": "boom", "stack_trace": "..."}""");
        var withEvidence = Parse(
            """{"message": "boom", "stack_trace": "...", "evidence": {"connections_opened": 2841}}""");

        Assert.NotEqual(EvidenceSignature.Compute(withoutEvidence), EvidenceSignature.Compute(withEvidence));
    }

    [Fact]
    public void AChangedValueTypeForTheSameKeyProducesADifferentSignature()
    {
        var numeric = Parse("""{"count": 5}""");
        var textual = Parse("""{"count": "5"}""");

        Assert.NotEqual(EvidenceSignature.Compute(numeric), EvidenceSignature.Compute(textual));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
