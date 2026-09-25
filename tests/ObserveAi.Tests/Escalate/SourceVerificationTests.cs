using ObserveAi;

namespace ObserveAi.Tests;

public class SourceVerificationTests
{
    private static readonly Frame SampleFrame = new("Pricing.Tiers.TierResolver.Resolve", true);

    [Fact]
    public void BuiltRowIsAValidTwoOptionYesNoQuestion()
    {
        var row = SourceVerification.BuildRow(
            "row-1", SampleFrame, "System.IndexOutOfRangeException", "public void Resolve(...) { ... }");

        Semif.ValidateRow(row); // Throws on anything malformed; a passing call is the assertion.
        var options = row.GetProperty("options");
        Assert.Equal(2, options.GetArrayLength());
        Assert.Contains(options.EnumerateArray(), o => o.GetProperty("id").GetString() == "yes");
        Assert.Contains(options.EnumerateArray(), o => o.GetProperty("id").GetString() == "no");
        Assert.Contains(SampleFrame.Method, row.GetProperty("state").GetString());
        Assert.Contains("System.IndexOutOfRangeException", row.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(0.9, 0.1, true)]
    [InlineData(0.1, 0.9, false)]
    [InlineData(0.5, 0.5, true)] // A tie reads as "not ruled out", not as a failure to verify.
    public void MatchedFollowsTheHigherOption(double yes, double no, bool expected)
    {
        var probabilities = new Dictionary<string, double> { ["yes"] = yes, ["no"] = no };

        Assert.Equal(expected, SourceVerification.Matched(probabilities));
    }

    [Fact]
    public void SummariseSentenceForAllFramesMatching()
    {
        var summary = SourceVerification.Summarise("main@abc1234", [true, true, true]);

        Assert.Equal(3, summary.Matched);
        Assert.Equal(3, summary.Total);
        Assert.Equal(
            "Analysed against main@abc1234. Frame verification: 3 of 3 matched.",
            summary.Sentence);
    }

    [Fact]
    public void SummariseSentenceForOneOfThreeMatching()
    {
        var summary = SourceVerification.Summarise("main@abc1234", [true, false, false]);

        Assert.Equal(1, summary.Matched);
        Assert.Equal(
            "Analysed against main@abc1234. Frame verification: 1 of 3 matched, so this may not be the code that ran.",
            summary.Sentence);
    }

    [Fact]
    public void SummariseSentenceForZeroOfThreeMatching()
    {
        var summary = SourceVerification.Summarise("main@abc1234", [false, false, false]);

        Assert.Equal(0, summary.Matched);
        Assert.Equal(
            "Analysed against main@abc1234. Frame verification: 0 of 3 matched, so this is likely not the code that ran.",
            summary.Sentence);
    }
}
