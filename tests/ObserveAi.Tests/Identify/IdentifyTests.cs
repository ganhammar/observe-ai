using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Checks Pipeline.Identify against the real fixture (eval/logs.jsonl): the
/// labelled runtime and a fingerprint matching TraceParser/Fingerprint run
/// directly, plus the log group parsed into resource kind and name. A message
/// with no trace in it (a fixture row's short one-line message field, not its
/// full stack_trace) must report failure rather than throw.
/// </summary>
public class IdentifyTests
{
    private const string LogGroupName = "/aws/lambda/checkout-api";

    public static IEnumerable<object[]> FixtureRows() => EvalFixture.Load().Select(row => new object[] { row });

    [Theory]
    [MemberData(nameof(FixtureRows))]
    public void ParsesEveryFixtureRowToItsLabelledRuntimeAndAStableFingerprint(EvalFixture.Row row)
    {
        var expected = TraceParser.Parse(row.StackTrace, [])!;

        var result = Pipeline.Identify(LogGroupName, row.StackTrace, []);

        Assert.True(result.Parsed);
        Assert.Null(result.Error);
        Assert.Equal(row.Runtime, result.Runtime);
        Assert.Equal(expected.ExceptionType, result.ExceptionType);
        Assert.Equal(Fingerprint.Compute(expected), result.Fingerprint);
        Assert.Equal(Fingerprint.Signature(expected), result.Signature);
    }

    [Fact]
    public void ReportsFailureRatherThanThrowingOnAMessageWithNoTraceInIt()
    {
        // dn-01's short "message" is one line of exception text with no frames
        // at all, unlike its full "stack_trace"; TraceParser recognises none of
        // the five runtimes from that alone.
        var row = EvalFixture.Load().Single(r => r.Id == "dn-01");

        var result = Pipeline.Identify(LogGroupName, row.Message, []);

        Assert.False(result.Parsed);
        Assert.NotNull(result.Error);
        Assert.Null(result.Runtime);
        Assert.Null(result.Fingerprint);
        Assert.Null(result.Frames);
    }

    [Fact]
    public void ResourceKindAndNameComeFromTheLogGroupName()
    {
        var row = EvalFixture.Load().Single(r => r.Id == "bg-01");

        var result = Pipeline.Identify(LogGroupName, row.StackTrace, []);

        Assert.Equal(LogGroupKind.Lambda, result.ResourceKind);
        Assert.Equal("checkout-api", result.ResourceName);
    }

    [Fact]
    public void AnUnrecognisedLogGroupStillParsesTheTraceWithNoResourceInfo()
    {
        var row = EvalFixture.Load().Single(r => r.Id == "bg-01");

        var result = Pipeline.Identify("some-custom-log-group", row.StackTrace, []);

        Assert.True(result.Parsed);
        Assert.Null(result.ResourceKind);
        Assert.Null(result.ResourceName);
    }
}
