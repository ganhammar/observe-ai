using Amazon.DynamoDBv2.Model;
using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>Checks the resolution order (tag, then cache, then model) and that only a model answer is cached.</summary>
public class RepoResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly IReadOnlyList<string> OrgRepositories = ["acme/orders", "acme/billing"];

    private static RepoResolver.ReadResourceTags Tags(IReadOnlyDictionary<string, string> tags, List<string> calls) =>
        (arn, _) =>
        {
            calls.Add(arn);
            return Task.FromResult(tags);
        };

    private static RepoResolver.ReadCache Cache(Dictionary<string, string> store, List<GetItemRequest> calls) =>
        (request, _) =>
        {
            calls.Add(request);
            var response = new GetItemResponse();
            if (store.TryGetValue(request.Key["namespace"].S, out var repo))
            {
                response.Item = new Dictionary<string, AttributeValue> { ["repo"] = new(repo) };
            }
            return Task.FromResult(response);
        };

    private static RepoResolver.WriteCache Writer(List<PutItemRequest> calls) =>
        (request, _) =>
        {
            calls.Add(request);
            return Task.FromResult(new PutItemResponse());
        };

    private static RepoResolver.ResolveWithModel Model(string answer, List<string> calls) =>
        (namespacePrefix, repos, _) =>
        {
            calls.Add(namespacePrefix);
            Assert.Equal(OrgRepositories, repos);
            return Task.FromResult(answer);
        };

    private static RepoResolver.ResolveWithModel NeverCalledModel() =>
        (_, _, _) => throw new InvalidOperationException("the model should not have been asked");

    [Fact]
    public async Task ATagHitWinsAndNeitherReadsNorWritesTheCache()
    {
        var tagCalls = new List<string>();
        var readCalls = new List<GetItemRequest>();
        var writeCalls = new List<PutItemRequest>();

        var result = await RepoResolver.ResolveAsync(
            "arn:aws:lambda:eu-central-1:1:function:checkout-api", "Checkout.",
            "cache-table", OrgRepositories,
            Tags(new Dictionary<string, string> { [RepoResolver.RepoTagKey] = "acme/checkout" }, tagCalls),
            Cache([], readCalls), Writer(writeCalls), NeverCalledModel(),
            Now, Retention);

        Assert.Equal(new RepoResolution("acme/checkout", RepoSource.Tag), result);
        Assert.Single(tagCalls);
        Assert.Empty(readCalls);
        Assert.Empty(writeCalls);
    }

    [Fact]
    public async Task NoTagFallsThroughToACacheHitAndDoesNotWriteIt()
    {
        var readCalls = new List<GetItemRequest>();
        var writeCalls = new List<PutItemRequest>();

        var result = await RepoResolver.ResolveAsync(
            "arn:aws:lambda:eu-central-1:1:function:orders-worker", "Orders.",
            "cache-table", OrgRepositories,
            Tags(new Dictionary<string, string>(), []),
            Cache(new Dictionary<string, string> { ["Orders."] = "acme/orders" }, readCalls),
            Writer(writeCalls), NeverCalledModel(),
            Now, Retention);

        Assert.Equal(new RepoResolution("acme/orders", RepoSource.Cache), result);
        Assert.Single(readCalls);
        Assert.Empty(writeCalls);
    }

    [Fact]
    public async Task NoResourceArnSkipsTheTagLookupEntirely()
    {
        var tagCalls = new List<string>();
        var readCalls = new List<GetItemRequest>();

        var result = await RepoResolver.ResolveAsync(
            null, "Orders.",
            "cache-table", OrgRepositories,
            Tags(new Dictionary<string, string>(), tagCalls),
            Cache(new Dictionary<string, string> { ["Orders."] = "acme/orders" }, readCalls),
            Writer([]), NeverCalledModel(),
            Now, Retention);

        Assert.Equal(new RepoResolution("acme/orders", RepoSource.Cache), result);
        Assert.Empty(tagCalls);
        Assert.Single(readCalls);
    }

    [Fact]
    public async Task NoTagAndNoCacheAsksTheModelAndWritesTheAnswerBack()
    {
        var modelCalls = new List<string>();
        var writeCalls = new List<PutItemRequest>();

        var result = await RepoResolver.ResolveAsync(
            "arn:aws:lambda:eu-central-1:1:function:orders-worker", "Orders.",
            "namespace-cache", OrgRepositories,
            Tags(new Dictionary<string, string>(), []),
            Cache([], []),
            Writer(writeCalls),
            Model("acme/orders", modelCalls),
            Now, Retention);

        Assert.Equal(new RepoResolution("acme/orders", RepoSource.Model), result);
        Assert.Equal(["Orders."], modelCalls);

        var written = Assert.Single(writeCalls);
        Assert.Equal("namespace-cache", written.TableName);
        Assert.Equal("Orders.", written.Item["namespace"].S);
        Assert.Equal("acme/orders", written.Item["repo"].S);
        Assert.Equal(Now.Add(Retention).ToUnixTimeSeconds(), long.Parse(written.Item["expires_at"].N));
    }

    [Fact]
    public async Task NoTagAndNoNamespaceResolvesToNothing()
    {
        var result = await RepoResolver.ResolveAsync(
            "arn:aws:lambda:eu-central-1:1:function:mystery", null,
            "namespace-cache", OrgRepositories,
            Tags(new Dictionary<string, string>(), []),
            Cache([], []), Writer([]), NeverCalledModel(),
            Now, Retention);

        Assert.Null(result);
    }

    [Fact]
    public async Task UnimplementedModelResolutionThrowsAndSaysWhy()
    {
        var error = await Assert.ThrowsAsync<NotImplementedException>(
            () => RepoResolver.ResolveWithModelAsync("Orders.", OrgRepositories));

        Assert.Contains("org repository list", error.Message);
    }
}
