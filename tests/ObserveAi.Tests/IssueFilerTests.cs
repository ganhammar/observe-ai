using System.Text.Json;
using ObserveAi;

namespace ObserveAi.Tests;

public class IssueFilerTests
{
    private static readonly Draft SampleDraft = new("NullReferenceException in Orders.Billing.Build", "Body text.");

    [Fact]
    public async Task ANewTitleFilesAFreshIssue()
    {
        List<(string Url, string Body)> calls = [];
        Task<string> CallGitHub(string url, string body, CancellationToken _)
        {
            calls.Add((url, body));
            return Task.FromResult(url.Contains("state=open") ? "[]" : """{"number":42}""");
        }

        var result = await IssueFiler.FileAsync(CallGitHub, "acme/orders", SampleDraft);

        Assert.Equal(FilingOutcome.Created, result.Outcome);
        Assert.Equal(42, result.IssueNumber);
        Assert.Equal(2, calls.Count);
        Assert.Equal("https://api.github.com/repos/acme/orders/issues?state=open", calls[0].Url);
        Assert.Equal("", calls[0].Body);
        Assert.Equal("https://api.github.com/repos/acme/orders/issues", calls[1].Url);

        using var body = JsonDocument.Parse(calls[1].Body);
        Assert.Equal(SampleDraft.Title, body.RootElement.GetProperty("title").GetString());
        Assert.Equal(SampleDraft.Body, body.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task AMatchingOpenTitleCommentsInsteadOfCreatingADuplicate()
    {
        List<(string Url, string Body)> calls = [];
        var existing = $$"""[{"number":7,"title":"{{SampleDraft.Title}}"}]""";
        Task<string> CallGitHub(string url, string body, CancellationToken _)
        {
            calls.Add((url, body));
            return Task.FromResult(url.Contains("state=open") ? existing : "{}");
        }

        var result = await IssueFiler.FileAsync(CallGitHub, "acme/orders", SampleDraft);

        Assert.Equal(FilingOutcome.Commented, result.Outcome);
        Assert.Equal(7, result.IssueNumber);
        Assert.Equal(2, calls.Count);
        Assert.Equal("https://api.github.com/repos/acme/orders/issues/7/comments", calls[1].Url);

        using var body = JsonDocument.Parse(calls[1].Body);
        Assert.Contains(SampleDraft.Body, body.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task ABadRepoIsRejectedBeforeAnyCallIsMade()
    {
        var calls = 0;
        Task<string> CallGitHub(string _, string __, CancellationToken ___)
        {
            calls++;
            return Task.FromResult("");
        }

        await Assert.ThrowsAsync<ArgumentException>(() => IssueFiler.FileAsync(CallGitHub, "https://evil.example/x", SampleDraft));
        Assert.Equal(0, calls);
    }
}
