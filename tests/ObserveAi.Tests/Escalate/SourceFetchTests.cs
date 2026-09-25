using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Checks path extraction against the real fixture (one representative row per
/// runtime, every one of which reports an absolute container path, mapped back
/// to a repository-relative guess), plus hand-written traces for shapes the
/// fixture does not contain on its own: an already repo-relative path per
/// runtime, an absolute path under no known mount, and a .NET frame with
/// "in X:line N" mixed with one that has no line info at all. The remaining
/// tests cover the GitHub Contents/Trees API requests built from those paths,
/// the monorepo basename fallback, and FetchAsync's use of both.
/// </summary>
public class SourceFetchTests
{
    private static EvalFixture.Row Row(string id) => EvalFixture.Load().Single(r => r.Id == id);

    [Fact]
    public void PythonPathComesFromTheFileLineWhenItIsRepoRelative()
    {
        const string trace = """
            Traceback (most recent call last):
              File "report/summarise.py", line 47, in build_row
                total = record["total_amount"]
            KeyError: 'total_amount'
            """;
        var parsed = TraceParser.Parse(trace);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        Assert.Equal(["report/summarise.py"], paths);
    }

    [Fact]
    public void PythonAbsolutePathFromTheRealFixtureIsMappedToARepoRelativePath()
    {
        // am-03's frame is /var/task/report/summarise.py: a real Lambda mount
        // path, stripped as a known mount rather than dropped outright.
        var row = Row("am-03");
        var trace = TraceParser.Parse(row.StackTrace);

        var paths = SourceFetch.PathsFor(trace!, row.StackTrace);

        Assert.Equal(["report/summarise.py"], paths);
    }

    [Fact]
    public void JavaPathsAreFilenamesInFrameOrder()
    {
        var row = Row("bg-02");
        var trace = TraceParser.Parse(row.StackTrace);

        var paths = SourceFetch.PathsFor(trace!, row.StackTrace);

        Assert.Equal(["Discount.java", "PriceCalculator.java", "ProductService.java"], paths);
    }

    [Fact]
    public void JavaPathsAreDeduplicatedWhenTheSameFrameRecurses()
    {
        var row = Row("bg-07"); // PolicyResolver.expand appears four times, same file each time.
        var trace = TraceParser.Parse(row.StackTrace);

        var paths = SourceFetch.PathsFor(trace!, row.StackTrace);

        Assert.Equal(["PolicyResolver.java"], paths);
    }

    [Fact]
    public void GoPathComesFromTheLineAfterTheFrameWhenItIsRepoRelative()
    {
        const string trace = """
            fatal error: runtime: out of memory
            goroutine 1 [running]:
            main.(*Ingestor).readAll(0xc0000b4000)
            	ingest/read.go:41 +0x88
            """;
        var parsed = TraceParser.Parse(trace);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        Assert.Equal(["ingest/read.go"], paths);
    }

    [Fact]
    public void GoAbsolutePathFromTheRealFixtureIsMappedToARepoRelativePath()
    {
        var row = Row("am-06"); // /app/ingest/read.go: an absolute container path.
        var trace = TraceParser.Parse(row.StackTrace);

        var paths = SourceFetch.PathsFor(trace!, row.StackTrace);

        Assert.Equal(["ingest/read.go"], paths);
    }

    [Fact]
    public void GoPathsForMultipleFramesStayInFrameOrder()
    {
        const string trace = """
            panic: runtime error: index out of range [5] with length 3
            goroutine 88 [running]:
            main.parseColumns(0xc000124060, 0x3)
            	ingest/parse.go:118 +0x2a4
            main.(*Ingestor).handleLine(0xc0000b4000, 0xc000130040)
            	ingest/run.go:64 +0x110
            """;
        var parsed = TraceParser.Parse(trace);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        Assert.Equal(["ingest/parse.go", "ingest/run.go"], paths);
    }

    [Fact]
    public void NodeSkipsAFrameLineThatHasNoParenthesisedLocation()
    {
        const string trace = """
            TypeError: Cannot read properties of undefined (reading 'map')
                at buildFacets (src/bff/facets.js:31:28)
                at renderSearch (src/bff/search.js:112:19)
                at async src/bff/routes.js:64:20
            """;
        var parsed = TraceParser.Parse(trace);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        // The third line names a real file but carries no "(...)" location, so
        // it never became a frame at all; this is unrelated to path safety.
        Assert.Equal(["src/bff/facets.js", "src/bff/search.js"], paths);
    }

    [Fact]
    public void NodeAbsolutePathsFromTheRealFixtureAreMappedToRepoRelativePaths()
    {
        var row = Row("bg-04"); // /app/src/bff/facets.js and friends: absolute container paths.
        var trace = TraceParser.Parse(row.StackTrace);

        var paths = SourceFetch.PathsFor(trace!, row.StackTrace);

        Assert.Equal(["src/bff/facets.js", "src/bff/search.js"], paths);
    }

    [Fact]
    public void AnAbsolutePathUnderNoKnownMountKeepsItsStructureWithTheLeadingSlashRemoved()
    {
        const string trace = """
            Traceback (most recent call last):
              File "/opt/custom/place/report.py", line 12, in build_row
            RuntimeError: boom
            """;
        var parsed = TraceParser.Parse(trace);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        Assert.Equal(["opt/custom/place/report.py"], paths);
    }

    [Fact]
    public void DotnetFrameWithNoLineInfoContributesNoPath()
    {
        const string trace = """
            System.NullReferenceException: Object reference not set to an instance of an object.
               at Orders.Billing.InvoiceBuilder.Build(Invoice invoice) in src/Orders/Billing/InvoiceBuilder.cs:line 42
               at Orders.Api.InvoiceController.Post(InvoiceRequest body)
            """;
        var parsed = TraceParser.Parse(trace);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        // The second frame is real (TraceParser keeps it) but the raw text never
        // says which file it lives in, so it cannot contribute a path.
        Assert.Equal(["src/Orders/Billing/InvoiceBuilder.cs"], paths);
    }

    [Fact]
    public void MaxFilesCapsTheDistinctPathCount()
    {
        var row = Row("bg-02");
        var trace = TraceParser.Parse(row.StackTrace);

        var paths = SourceFetch.PathsFor(trace!, row.StackTrace, maxFiles: 2);

        Assert.Equal(["Discount.java", "PriceCalculator.java"], paths);
    }

    [Fact]
    public void RequestsForBuildsOneContentsRequestPerPathAtTheGivenRef()
    {
        var requests = SourceFetch.RequestsFor("acme/catalog", "abc1234", ["Discount.java", "PriceCalculator.java"]);

        Assert.Equal(2, requests.Count);
        Assert.Equal("Discount.java", requests[0].Path);
        Assert.Equal("https://api.github.com/repos/acme/catalog/contents/Discount.java?ref=abc1234", requests[0].Url);
        Assert.Equal("PriceCalculator.java", requests[1].Path);
        Assert.Equal("https://api.github.com/repos/acme/catalog/contents/PriceCalculator.java?ref=abc1234", requests[1].Url);
    }

    [Fact]
    public void RequestsForPercentEncodesASpaceAndAPlusInAPathSegment()
    {
        var request = Assert.Single(SourceFetch.RequestsFor("acme/catalog", "main", ["src/My File+Name.cs"]));

        Assert.Equal(
            "https://api.github.com/repos/acme/catalog/contents/src/My%20File%2BName.cs?ref=main",
            request.Url);
    }

    [Fact]
    public void RequestsForRejectsARepoThatIsAUrlRatherThanOwnerSlashName()
    {
        Assert.Throws<ArgumentException>(() => SourceFetch.RequestsFor("https://evil.example/x", "main", []));
    }

    [Fact]
    public void TreeRequestBuildsTheRecursiveGitTreesUrl()
    {
        var url = SourceFetch.TreeRequest("acme/catalog", "main");

        Assert.Equal("https://api.github.com/repos/acme/catalog/git/trees/main?recursive=1", url);
    }

    [Fact]
    public void MatchByBasenameFindsAMonorepoPrefixedPath()
    {
        string[] treePaths = ["services/orders/payment.py", "services/billing/payment.py"];

        var match = SourceFetch.MatchByBasename(treePaths, "orders/payment.py");

        Assert.Equal("services/orders/payment.py", match);
    }

    [Fact]
    public void MatchByBasenameReturnsNullWhenTwoCandidatesTieOnTheSameSuffixLength()
    {
        string[] treePaths = ["services/orders/payment.py", "services/billing/payment.py"];

        var match = SourceFetch.MatchByBasename(treePaths, "payment.py");

        Assert.Null(match);
    }

    [Fact]
    public void MatchByBasenameReturnsNullWhenNothingSharesTheFilename()
    {
        string[] treePaths = ["services/orders/invoice.py"];

        var match = SourceFetch.MatchByBasename(treePaths, "orders/payment.py");

        Assert.Null(match);
    }

    [Fact]
    public async Task FetchAsyncFallsBackToTheTreeWhenTheDirectRequestMisses()
    {
        var directUrl = SourceFetch.RequestsFor("acme/mono", "main", ["orders/payment.py"]).Single().Url;
        var treeUrl = SourceFetch.TreeRequest("acme/mono", "main");
        var retryUrl = SourceFetch.RequestsFor("acme/mono", "main", ["services/orders/payment.py"]).Single().Url;
        const string treeJson = """{"tree":[{"path":"services/orders/payment.py","type":"blob"},{"path":"services/orders/README.md","type":"blob"}]}""";

        Task<string?> Get(string url, string? body, CancellationToken _) => Task.FromResult<string?>(url switch
        {
            _ when url == directUrl => null,
            _ when url == treeUrl => treeJson,
            _ when url == retryUrl => "def pay(): ...",
            _ => throw new InvalidOperationException($"unexpected request: {url}"),
        });

        var result = await SourceFetch.FetchAsync(Get, "acme/mono", "main", ["orders/payment.py"]);

        Assert.Equal("def pay(): ...", result.Sources["orders/payment.py"]);
        Assert.Empty(result.NotFound);
    }

    [Fact]
    public async Task FetchAsyncReportsAMissingFileAsNotFoundInsteadOfThrowing()
    {
        Task<string?> Get(string url, string? body, CancellationToken _) => Task.FromResult<string?>(null);

        var result = await SourceFetch.FetchAsync(Get, "acme/catalog", "main", ["missing.py"]);

        Assert.Empty(result.Sources);
        Assert.Equal(["missing.py"], result.NotFound);
    }

    [Fact]
    public void ATraversalPathIsDropped()
    {
        const string trace = """
            Traceback (most recent call last):
              File "../../etc/passwd", line 1, in evil_func
            RuntimeError: boom
            """;
        var parsed = TraceParser.Parse(trace);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        Assert.Empty(paths);
    }

    [Fact]
    public void FramePathsLinesUpWithTheFramesAndLeavesVendorFramesNull()
    {
        const string raw = """
            System.IndexOutOfRangeException: Index was outside the bounds of the array.
               at Pricing.Tiers.TierResolver.Resolve(Int32 index) in /app/src/Pricing/Tiers/TierResolver.cs:line 42
               at System.Linq.Enumerable.First[TSource](IEnumerable`1 source)
               at Pricing.Api.Quote.Handle(Request r) in /app/src/Pricing/Api/Quote.cs:line 10
            """;
        var trace = new ParsedTrace("dotnet", "System.IndexOutOfRangeException",
        [
            new Frame("Pricing.Tiers.TierResolver.Resolve", true),
            new Frame("System.Linq.Enumerable.First", false),
            new Frame("Pricing.Api.Quote.Handle", true),
        ]);

        var paths = SourceFetch.FramePaths(trace, raw);

        Assert.Equal(["src/Pricing/Tiers/TierResolver.cs", null, "src/Pricing/Api/Quote.cs"], paths);
        Assert.Equal(["src/Pricing/Tiers/TierResolver.cs", "src/Pricing/Api/Quote.cs"], SourceFetch.PathsFor(trace, raw));
    }
}
