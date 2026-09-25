using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>Checks that Fingerprint groups repeats of the same defect together and keeps different defects apart.</summary>
public class FingerprintTests
{
    private static ParsedTrace Parse(string trace) => TraceParser.Parse(trace)!;

    [Fact]
    public void SameDefectWithDifferentVariableDataProducesTheSameFingerprint()
    {
        const string traceA = """
            System.NullReferenceException: Object reference not set to an instance of an object.
               at Orders.PaymentService.Charge(Order o) in /src/PaymentService.cs:line 88
               at Orders.Api.OrdersController.Post(OrderRequest body) in /src/OrdersController.cs:line 40
            """;
        const string traceB = """
            System.NullReferenceException: Object reference not set to an instance of an object. Order 9f2c8e21-4a11-4e2b-9a51-6b6b0b8e2f10 failed.
               at Orders.PaymentService.Charge(Order o) in /src/PaymentService.cs:line 92
               at Orders.Api.OrdersController.Post(OrderRequest body) in /src/OrdersController.cs:line 55
            """;

        var fingerprintA = Fingerprint.Compute(Parse(traceA));
        var fingerprintB = Fingerprint.Compute(Parse(traceB));

        Assert.Equal(fingerprintA, fingerprintB);
    }

    [Fact]
    public void DifferentDefectsProduceDifferentFingerprints()
    {
        var rows = EvalFixture.Load();
        var indexOutOfRange = Parse(rows.Single(r => r.Id == "bg-01").StackTrace);
        var sequenceEmpty = Parse(rows.Single(r => r.Id == "bg-05").StackTrace);

        Assert.NotEqual(Fingerprint.Compute(indexOutOfRange), Fingerprint.Compute(sequenceEmpty));
    }

    [Fact]
    public void DotnetAsyncFrameNormalisesToSameFingerprintAsNonAsyncEquivalent()
    {
        var asyncTrace = Parse(EvalFixture.Load().Single(r => r.Id == "am-01").StackTrace);
        const string nonAsyncTrace = """
            System.NullReferenceException: Object reference not set to an instance of an object.
               at Checkout.Clients.PaymentsClient.ChargeAsync(ChargeRequest request, CancellationToken ct)
               at Checkout.Handlers.CheckoutHandler.HandleAsync(CheckoutCommand cmd, CancellationToken ct)
               at Checkout.Api.CheckoutController.Post(CheckoutRequest body, CancellationToken ct)
            """;

        Assert.Equal(Fingerprint.Compute(asyncTrace), Fingerprint.Compute(Parse(nonAsyncTrace)));
    }

    [Fact]
    public void SignatureExposesRuntimeExceptionTypeAndInAppFrames()
    {
        var parsed = Parse("""
            System.IndexOutOfRangeException: Index was outside the bounds of the array.
               at Pricing.Tiers.TierResolver.Resolve(Decimal amount, Tier[] tiers)
               at Pricing.Quote.QuoteBuilder.Build(QuoteRequest request)
            """);

        Assert.Equal(
            "dotnet|System.IndexOutOfRangeException|Pricing.Tiers.TierResolver.Resolve>Pricing.Quote.QuoteBuilder.Build",
            Fingerprint.Signature(parsed));
    }

    [Fact]
    public void FallsBackToAnyFrameWhenNoneAreInApp()
    {
        var parsed = Parse("""
            System.InvalidOperationException: Sequence contains no elements
               at System.Linq.Enumerable.First[TSource](IEnumerable`1 source)
               at System.Linq.Enumerable.Single[TSource](IEnumerable`1 source)
            """);

        Assert.All(parsed.Frames, f => Assert.False(f.InApp));
        Assert.Equal(
            "dotnet|System.InvalidOperationException|System.Linq.Enumerable.First>System.Linq.Enumerable.Single",
            Fingerprint.Signature(parsed));
    }

    [Fact]
    public void GoPanicsGroupDespiteVaryingValuesInTheMessage()
    {
        // The same out-of-range defect hitting different indices is one defect.
        // Go keeps its message in the exception type, so without normalisation
        // each occurrence would open its own issue.
        const string first = """
            panic: runtime error: index out of range [5] with length 3
            goroutine 88 [running]:
            main.parseColumns(0xc000124060, 0x3)
            	/app/ingest/parse.go:118 +0x2a4
            """;
        const string second = """
            panic: runtime error: index out of range [91] with length 40
            goroutine 214 [running]:
            main.parseColumns(0xc000999111, 0x28)
            	/app/ingest/parse.go:118 +0x2a4
            """;

        var a = TraceParser.Parse(first);
        var b = TraceParser.Parse(second);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal("runtime error: index out of range [<n>] with length <n>", a!.ExceptionType);
        Assert.Equal(Fingerprint.Compute(a), Fingerprint.Compute(b!));
    }

    [Fact]
    public void DifferentGoPanicKindsStayApart()
    {
        var bounds = TraceParser.Parse("panic: runtime error: index out of range [5] with length 3\nmain.a()\n\t/app/a.go:1 +0x1");
        var nilMap = TraceParser.Parse("panic: assignment to entry in nil map\nmain.a()\n\t/app/a.go:1 +0x1");

        Assert.NotNull(bounds);
        Assert.NotNull(nilMap);
        Assert.NotEqual(Fingerprint.Compute(bounds!), Fingerprint.Compute(nilMap!));
    }
}
