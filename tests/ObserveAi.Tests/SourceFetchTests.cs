using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// Checks path extraction against the real fixture (one representative row per
/// runtime, every one of which reports an absolute container path, mapped back
/// to a repository-relative guess), plus hand-written traces for shapes the
/// fixture does not contain on its own: an already repo-relative path per
/// runtime, an absolute path under no known mount, and a .NET frame with
/// "in X:line N" mixed with one that has no line info at all.
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
        var parsed = TraceParser.Parse(trace, []);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        Assert.Equal(["report/summarise.py"], paths);
    }

    [Fact]
    public void PythonAbsolutePathFromTheRealFixtureIsMappedToARepoRelativePath()
    {
        // am-03's frame is /var/task/report/summarise.py: a real Lambda mount
        // path, stripped by DefaultMountPrefixes rather than dropped outright.
        var row = Row("am-03");
        var trace = TraceParser.Parse(row.StackTrace, []);

        var paths = SourceFetch.PathsFor(trace!, row.StackTrace);

        Assert.Equal(["report/summarise.py"], paths);
    }

    [Fact]
    public void JavaPathsAreFilenamesInFrameOrder()
    {
        var row = Row("bg-02");
        var trace = TraceParser.Parse(row.StackTrace, []);

        var paths = SourceFetch.PathsFor(trace!, row.StackTrace);

        Assert.Equal(["Discount.java", "PriceCalculator.java", "ProductService.java"], paths);
    }

    [Fact]
    public void JavaPathsAreDeduplicatedWhenTheSameFrameRecurses()
    {
        var row = Row("bg-07"); // PolicyResolver.expand appears four times, same file each time.
        var trace = TraceParser.Parse(row.StackTrace, []);

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
        var parsed = TraceParser.Parse(trace, []);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        Assert.Equal(["ingest/read.go"], paths);
    }

    [Fact]
    public void GoAbsolutePathFromTheRealFixtureIsMappedToARepoRelativePath()
    {
        var row = Row("am-06"); // /app/ingest/read.go: an absolute container path.
        var trace = TraceParser.Parse(row.StackTrace, []);

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
        var parsed = TraceParser.Parse(trace, []);

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
        var parsed = TraceParser.Parse(trace, []);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        // The third line names a real file but carries no "(...)" location, so
        // it never became a frame at all; this is unrelated to path safety.
        Assert.Equal(["src/bff/facets.js", "src/bff/search.js"], paths);
    }

    [Fact]
    public void NodeAbsolutePathsFromTheRealFixtureAreMappedToRepoRelativePaths()
    {
        var row = Row("bg-04"); // /app/src/bff/facets.js and friends: absolute container paths.
        var trace = TraceParser.Parse(row.StackTrace, []);

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
        var parsed = TraceParser.Parse(trace, []);

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
        var parsed = TraceParser.Parse(trace, []);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        // The second frame is real (TraceParser keeps it) but the raw text never
        // says which file it lives in, so it cannot contribute a path.
        Assert.Equal(["src/Orders/Billing/InvoiceBuilder.cs"], paths);
    }

    [Fact]
    public void MaxFilesCapsTheDistinctPathCount()
    {
        var row = Row("bg-02");
        var trace = TraceParser.Parse(row.StackTrace, []);

        var paths = SourceFetch.PathsFor(trace!, row.StackTrace, maxFiles: 2);

        Assert.Equal(["Discount.java", "PriceCalculator.java"], paths);
    }

    [Fact]
    public void CloneCommandsAreBloblessAndSparseAndNameOnlyTheFramePaths()
    {
        string[] paths = ["Discount.java", "PriceCalculator.java"];

        var commands = SourceFetch.CloneCommands("acme/catalog", "abc1234", paths);

        Assert.Equal(3, commands.Count);
        Assert.Equal(
            ["clone", "--depth", "1", "--filter=blob:none", "--sparse", "--", "https://github.com/acme/catalog.git"],
            commands[0]);
        Assert.Equal(["sparse-checkout", "set", "--", "Discount.java", "PriceCalculator.java"], commands[1]);
        Assert.Equal(["checkout", "--", "abc1234"], commands[2]);
    }

    [Fact]
    public void CloneCommandsAllCarryAnEndOfOptionsMarkerBeforePositionalArguments()
    {
        var commands = SourceFetch.CloneCommands("acme/catalog", "main", ["a.cs", "-x"]);

        Assert.All(commands, command => Assert.Contains("--", command));
    }

    [Fact]
    public void CloneCommandsRejectsARepoThatIsAUrlRatherThanOwnerSlashName()
    {
        Assert.Throws<ArgumentException>(() => SourceFetch.CloneCommands("https://evil.example/x", "main", []));
    }

    [Fact]
    public void CloneCommandsRejectsARepoThatStartsWithADash()
    {
        Assert.Throws<ArgumentException>(() => SourceFetch.CloneCommands("-x/evil", "main", []));
    }

    [Fact]
    public void CloneCommandsRejectsACommitishThatIsNotAShaOrASafeRef()
    {
        Assert.Throws<ArgumentException>(() => SourceFetch.CloneCommands("acme/catalog", "--upload-pack=/bin/sh", []));
    }

    [Fact]
    public void CloneCommandsAcceptsAShaAndABranchName()
    {
        var byShaCommands = SourceFetch.CloneCommands("acme/catalog", "abc1234", []);
        var byBranchCommands = SourceFetch.CloneCommands("acme/catalog", "release/2026-09", []);

        Assert.Equal(["checkout", "--", "abc1234"], byShaCommands[2]);
        Assert.Equal(["checkout", "--", "release/2026-09"], byBranchCommands[2]);
    }

    [Fact]
    public void APathBeginningWithADashIsDropped()
    {
        const string trace = """
            Traceback (most recent call last):
              File "-rf ~", line 1, in evil_func
            RuntimeError: boom
            """;
        var parsed = TraceParser.Parse(trace, []);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        Assert.Empty(paths);
    }

    [Fact]
    public void ATraversalPathIsDropped()
    {
        const string trace = """
            Traceback (most recent call last):
              File "../../etc/passwd", line 1, in evil_func
            RuntimeError: boom
            """;
        var parsed = TraceParser.Parse(trace, []);

        var paths = SourceFetch.PathsFor(parsed!, trace);

        Assert.Empty(paths);
    }
}
