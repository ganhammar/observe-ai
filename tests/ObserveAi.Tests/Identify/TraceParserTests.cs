using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Checks TraceParser against the real fixture (eval/logs.jsonl, eval/labels.json),
/// plus hand-written traces for the one shape that fixture does not contain: a
/// Java exception with a "Caused by:" wrapping the thing that actually broke.
/// </summary>
public class TraceParserTests
{
    public static IEnumerable<object[]> FixtureRows() => EvalFixture.Load().Select(row => new object[] { row });

    [Theory]
    [MemberData(nameof(FixtureRows))]
    public void ParsesEveryFixtureRowAsItsLabelledRuntime(EvalFixture.Row row)
    {
        var parsed = TraceParser.Parse(row.StackTrace, []);

        Assert.NotNull(parsed);
        Assert.Equal(row.Runtime, parsed!.Runtime);
        Assert.NotEmpty(parsed.Frames);
    }

    [Theory]
    [InlineData("bg-03", "KeyError")] // python
    [InlineData("am-06", "runtime: out of memory")] // go, "fatal error: " line
    [InlineData("bg-02", "java.lang.ArithmeticException")]
    [InlineData("bg-01", "System.IndexOutOfRangeException")]
    [InlineData("bg-04", "TypeError")]
    public void ExtractsExceptionTypeForARepresentativeRowPerRuntime(string id, string expected)
    {
        var row = EvalFixture.Load().Single(r => r.Id == id);

        var parsed = TraceParser.Parse(row.StackTrace, []);

        Assert.Equal(expected, parsed!.ExceptionType);
    }

    [Fact]
    public void JavaCausedByWinsOverTheOuterWrapper()
    {
        const string trace = """
            com.acme.orders.OrderProcessingException: Failed to process order 4471
                at com.acme.orders.OrderWorker.process(OrderWorker.java:141)
                at com.acme.orders.OrderService.run(OrderService.java:58)
            Caused by: java.lang.NullPointerException: Cannot invoke "String.trim()" because "raw" is null
                at com.acme.orders.util.Normalizer.clean(Normalizer.java:22)
                at com.acme.orders.OrderWorker.process(OrderWorker.java:139)
                ... 3 more
            """;

        var parsed = TraceParser.Parse(trace, []);

        Assert.NotNull(parsed);
        Assert.Equal("java", parsed!.Runtime);
        Assert.Equal("java.lang.NullPointerException", parsed.ExceptionType);
        Assert.Contains(parsed.Frames, f => f.Method == "com.acme.orders.util.Normalizer.clean");
        // Only appears before "Caused by:", in the outer wrapper's own stack, so
        // it must not survive scoping to the cause.
        Assert.DoesNotContain(parsed.Frames, f => f.Method == "com.acme.orders.OrderService.run");
    }

    [Fact]
    public void JavaWithNoCausedByUsesTheOuterExceptionAndAllFrames()
    {
        const string trace = """
            java.lang.ArithmeticException: / by zero
                at com.acme.catalog.pricing.Discount.percentOff(Discount.java:44)
                at com.acme.catalog.pricing.PriceCalculator.apply(PriceCalculator.java:88)
            """;

        var parsed = TraceParser.Parse(trace, []);

        Assert.NotNull(parsed);
        Assert.Equal("java.lang.ArithmeticException", parsed!.ExceptionType);
        Assert.Equal(2, parsed.Frames.Count);
    }

    [Fact]
    public void UnrecognisedTextDoesNotParse()
    {
        Assert.Null(TraceParser.Parse("just a plain log line with no stack trace at all", []));
    }
}
