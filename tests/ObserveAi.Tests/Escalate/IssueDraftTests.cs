using ObserveAi;

namespace ObserveAi.Tests;

public class IssueDraftTests
{
    private static readonly ParsedTrace Trace = new(
        "dotnet",
        "System.IndexOutOfRangeException",
        [
            new Frame("Pricing.Tiers.TierResolver.Resolve", true),
            new Frame("Pricing.Quote.QuoteBuilder.Build", true),
        ]);

    private static readonly CombineResult Verdict = new(Bug: 0.87, Downstream: 0.13, Fallback: false);

    [Fact]
    public void TitleIsExceptionTypePlusTheTopInAppFrame()
    {
        var summary = SourceVerification.Summarise("main@abc1234", [true, true]);

        var draft = IssueDraft.Build(
            Trace, Verdict, occurrences: 1, DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            summary, "The tier list can be shorter than the resolved index.", ["Pricing/Tiers/TierResolver.cs"]);

        Assert.Equal("System.IndexOutOfRangeException in Pricing.Tiers.TierResolver.Resolve", draft.Title);
    }

    [Fact]
    public void TheSameDefectProducesTheSameTitleAcrossOccurrences()
    {
        var earlySummary = SourceVerification.Summarise("main@abc1234", [true, true]);
        var laterSummary = SourceVerification.Summarise("main@def5678", [true, false]);

        var first = IssueDraft.Build(
            Trace, Verdict, occurrences: 1, DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            earlySummary, "First read of the root cause.", ["Pricing/Tiers/TierResolver.cs"]);
        var repeat = IssueDraft.Build(
            Trace, Verdict, occurrences: 42, DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            laterSummary, "A differently worded read of the same root cause.", ["Pricing/Tiers/TierResolver.cs"]);

        Assert.Equal(first.Title, repeat.Title);
    }

    [Fact]
    public void BodyStatesTheOccurrenceCountAndTheVerificationSentence()
    {
        var summary = SourceVerification.Summarise("main@abc1234", [true, false, false]);

        var draft = IssueDraft.Build(
            Trace, Verdict, occurrences: 42, DateTimeOffset.Parse("2026-08-15T00:00:00Z"),
            summary, "The tier list can be shorter than the resolved index.", ["Pricing/Tiers/TierResolver.cs"]);

        Assert.Contains("42", draft.Body);
        Assert.Contains(summary.Sentence, draft.Body);
        Assert.Contains("The tier list can be shorter than the resolved index.", draft.Body);
        Assert.Contains("Pricing/Tiers/TierResolver.cs", draft.Body);
    }

    [Fact]
    public void MissingInAppFrameFallsBackToTheFirstFrame()
    {
        var vendorOnlyTrace = new ParsedTrace(
            "java", "java.lang.NullPointerException", [new Frame("java.base/java.util.Objects.requireNonNull", false)]);
        var summary = SourceVerification.Summarise("main@abc1234", [false]);

        var draft = IssueDraft.Build(
            vendorOnlyTrace, Verdict, occurrences: 1, DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            summary, "Unclear; every frame is vendor code.", []);

        Assert.Equal("java.lang.NullPointerException in java.base/java.util.Objects.requireNonNull", draft.Title);
        Assert.Contains("(none; no file named by the trace could be fetched)", draft.Body);
    }

    [Fact]
    public void AnEmptyRootCauseLeavesTheSectionOut()
    {
        var summary = SourceVerification.Summarise("main@abc1234", []);

        var draft = IssueDraft.Build(
            Trace, Verdict, occurrences: 1, DateTimeOffset.Parse("2026-09-01T00:00:00Z"), summary, "", []);

        Assert.DoesNotContain("Root cause", draft.Body);
        Assert.Contains("(none; no file named by the trace could be fetched)", draft.Body);
    }
}
