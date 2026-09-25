using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>Checks log group parsing, namespace extraction, and the repository naming convention built on top of parsing.</summary>
public class ServiceIdentityTests
{
    [Theory]
    [InlineData("/aws/lambda/billing-sync", "acme/billing-sync")]
    [InlineData("/ecs/orders", "acme/orders")]
    public void ConventionalRepoCombinesTheOrgWithTheResourceName(string logGroupName, string expectedRepo)
    {
        Assert.Equal(expectedRepo, ServiceIdentity.ConventionalRepo(logGroupName, "acme"));
    }

    [Fact]
    public void ConventionalRepoIsNullWhenTheLogGroupDoesNotMatchAKnownShape()
    {
        Assert.Null(ServiceIdentity.ConventionalRepo("some-custom-log-group", "acme"));
    }

    [Theory]
    [InlineData("/aws/lambda/checkout-api", LogGroupKind.Lambda, "checkout-api")]
    [InlineData("/aws/ecs/orders", LogGroupKind.Ecs, "orders")]
    [InlineData("/ecs/orders", LogGroupKind.Ecs, "orders")]
    [InlineData("/aws/eks/orders-cluster", LogGroupKind.Eks, "orders-cluster")]
    public void ParsesEachKnownLogGroupShape(string logGroupName, LogGroupKind kind, string resourceName)
    {
        var parsed = ServiceIdentity.ParseLogGroup(logGroupName);

        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(resourceName, parsed.ResourceName);
    }

    [Theory]
    [InlineData("/aws/rds/orders-db")]
    [InlineData("/aws/eks/")]
    [InlineData("/aws/lambda/")]
    [InlineData("some-custom-log-group")]
    [InlineData("")]
    public void UnrecognisedLogGroupsReturnNull(string logGroupName)
    {
        Assert.Null(ServiceIdentity.ParseLogGroup(logGroupName));
    }

    private static ParsedTrace TraceWithTopFrame(string method) =>
        new("dotnet", "System.Exception", [new Frame(method, InApp: true), new Frame("System.Void.Vendor", InApp: false)]);

    [Fact]
    public void ThreeSegmentMethodGivesTheLeadingSegmentAsNamespace()
    {
        Assert.Equal("Orders.", ServiceIdentity.NamespacePrefix(TraceWithTopFrame("Orders.PaymentService.Charge")));
    }

    [Fact]
    public void DeeplyDottedMethodKeepsEverythingBeforeTheFinalTwoSegments()
    {
        Assert.Equal(
            "com.acme.orders.",
            ServiceIdentity.NamespacePrefix(TraceWithTopFrame("com.acme.orders.OrderWorker.process")));
    }

    [Fact]
    public void TwoSegmentMethodHasNoNamespaceToReport()
    {
        Assert.Null(ServiceIdentity.NamespacePrefix(TraceWithTopFrame("SMTPConnection._onError")));
    }

    [Fact]
    public void BareFunctionNameHasNoNamespaceToReport()
    {
        Assert.Null(ServiceIdentity.NamespacePrefix(TraceWithTopFrame("buildFacets")));
    }

    [Fact]
    public void NoInAppFrameMeansNoNamespace()
    {
        var trace = new ParsedTrace("go", "panic", [new Frame("main.parseColumns", InApp: false)]);

        Assert.Null(ServiceIdentity.NamespacePrefix(trace));
    }

    [Fact]
    public void EmptyFrameListMeansNoNamespace()
    {
        Assert.Null(ServiceIdentity.NamespacePrefix(new ParsedTrace("node", "TypeError", [])));
    }

    private static ParsedTrace Parse(string id, IReadOnlyCollection<string> appPrefixes) =>
        TraceParser.Parse(EvalFixture.Load().Single(r => r.Id == id).StackTrace, appPrefixes)!;

    [Fact]
    public void DotnetNamespaceComesFromTheFirstFrameInsideTheAppPrefix()
    {
        // dn-04's innermost frame is Npgsql's own connection pool, a dependency
        // with no reason to be in VendorPrefixes; a real per-service app prefix
        // is what keeps that frame from being mistaken for the app's own code.
        var trace = Parse("dn-04", ["Billing."]);

        Assert.Equal("Billing.Data.", ServiceIdentity.NamespacePrefix(trace));
    }

    [Fact]
    public void JavaNamespaceComesFromTheFirstFrameInsideTheAppPrefix()
    {
        var trace = Parse("dn-02", ["com.acme."]);

        Assert.Equal("com.acme.orders.client.", ServiceIdentity.NamespacePrefix(trace));
    }

    [Fact]
    public void PythonFramesCarryNoNamespace()
    {
        var trace = Parse("dn-09", []);

        Assert.Null(ServiceIdentity.NamespacePrefix(trace));
    }

    [Fact]
    public void NodeFramesCarryNoNamespace()
    {
        var trace = Parse("bg-04", []);

        Assert.Null(ServiceIdentity.NamespacePrefix(trace));
    }

    [Fact]
    public void GoPackageLevelFramesCarryNoNamespace()
    {
        // main.parseColumns is a package function, not a method on a receiver;
        // two segments is all Go gives here, so there is nothing before them.
        var trace = Parse("bg-06", []);

        Assert.Null(ServiceIdentity.NamespacePrefix(trace));
    }
}
