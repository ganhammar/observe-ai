using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>The prompt the diagnosis model sees: the trace, then every fetched file, each bounded.</summary>
public class DiagnosisTests
{
    [Fact]
    public void PromptCarriesTheTraceThenEachFileUnderItsPath()
    {
        var prompt = Diagnosis.Prompt("KeyError: 'x'\n  File \"/var/task/handler.py\", line 19, in price_for",
            new Dictionary<string, string> { ["handler.py"] = "TIERS = {}\n", ["util.py"] = "def f(): ...\n" });

        Assert.StartsWith("Stack trace:\n\nKeyError: 'x'", prompt);
        Assert.Contains("File handler.py:\n\nTIERS = {}\n\n", prompt);
        Assert.Contains("File util.py:\n\ndef f(): ...\n", prompt);
        Assert.True(prompt.IndexOf("File handler.py") < prompt.IndexOf("File util.py"));
    }

    [Fact]
    public void PromptTruncatesAFileLargerThanTheCapAndSaysSo()
    {
        var huge = new string('x', Diagnosis.MaxCharsPerFile + 100);

        var prompt = Diagnosis.Prompt("trace", new Dictionary<string, string> { ["big.py"] = huge });

        Assert.Contains(new string('x', Diagnosis.MaxCharsPerFile) + "\n[truncated]", prompt);
        Assert.DoesNotContain(new string('x', Diagnosis.MaxCharsPerFile + 1), prompt);
    }

    [Fact]
    public void PromptSaysWhenNothingCouldBeFetched()
    {
        var prompt = Diagnosis.Prompt("trace", new Dictionary<string, string>());

        Assert.EndsWith("No source file named by the trace could be fetched.\n", prompt);
    }
}
