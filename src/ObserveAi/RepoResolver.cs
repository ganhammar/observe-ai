using Amazon.DynamoDBv2.Model;

namespace ObserveAi;

/// <summary>How a repository answer was reached, cheapest signal first.</summary>
public enum RepoSource { Tag, Cache, Model }

/// <summary>The repository owning a log event, plus how sure that answer is.</summary>
public sealed record RepoResolution(string Repo, RepoSource Source);

/// <summary>
/// Turns a log event into a repository, cheapest signal first: the resource's own
/// "observe-ai:repo" tag, then a cache of namespace-to-repo answers the model has
/// already given, then the model itself. Every external operation is a delegate,
/// exactly as SeenStore takes UpdateItem, so a test supplies an in-memory fake
/// instead of a real tagging client or DynamoDB table.
/// </summary>
public static class RepoResolver
{
    public const string RepoTagKey = "observe-ai:repo";
    private const string NamespaceAttribute = "namespace";
    private const string RepoAttribute = "repo";

    /// <summary>Reads the tags on a resource ARN. An empty map means the resource carries none.</summary>
    public delegate Task<IReadOnlyDictionary<string, string>> ReadResourceTags(
        string resourceArn, CancellationToken cancellationToken);

    public delegate Task<GetItemResponse> ReadCache(GetItemRequest request, CancellationToken cancellationToken);

    public delegate Task<PutItemResponse> WriteCache(PutItemRequest request, CancellationToken cancellationToken);

    /// <summary>Asks the model which of the org's repositories owns a namespace.</summary>
    public delegate Task<string> ResolveWithModel(
        string namespacePrefix, IReadOnlyList<string> orgRepositories, CancellationToken cancellationToken);

    /// <param name="resourceArn">
    /// The ARN of the resource the log group named, or null when the log group did
    /// not parse to a known kind. Building the ARN from a ParsedLogGroup needs
    /// account and region context this resolver has no business holding, so the
    /// caller resolves that first.
    /// </param>
    /// <param name="namespacePrefix">ServiceIdentity.NamespacePrefix's result, or null when the trace carries none.</param>
    public static async Task<RepoResolution?> ResolveAsync(
        string? resourceArn,
        string? namespacePrefix,
        string cacheTable,
        IReadOnlyList<string> orgRepositories,
        ReadResourceTags readTags,
        ReadCache readCache,
        WriteCache writeCache,
        ResolveWithModel resolveWithModel,
        DateTimeOffset now,
        TimeSpan cacheRetention,
        CancellationToken cancellationToken = default)
    {
        if (resourceArn is not null)
        {
            var tags = await readTags(resourceArn, cancellationToken).ConfigureAwait(false);
            // The tag is the documented, exact answer, and it is already the cheap
            // path: caching it would only add a staleness problem for no gain.
            if (tags.TryGetValue(RepoTagKey, out var tagged))
            {
                return new RepoResolution(tagged, RepoSource.Tag);
            }
        }

        if (namespacePrefix is null)
        {
            return null;
        }

        var cached = await readCache(new GetItemRequest
        {
            TableName = cacheTable,
            Key = new Dictionary<string, AttributeValue> { [NamespaceAttribute] = new(namespacePrefix) },
        }, cancellationToken).ConfigureAwait(false);

        if (cached.Item is { Count: > 0 } item && item.TryGetValue(RepoAttribute, out var cachedRepo))
        {
            return new RepoResolution(cachedRepo.S, RepoSource.Cache);
        }

        var modelRepo = await resolveWithModel(namespacePrefix, orgRepositories, cancellationToken).ConfigureAwait(false);

        // Asked once per novel namespace, not once per log, which is what keeps
        // this step near zero at steady state.
        await writeCache(new PutItemRequest
        {
            TableName = cacheTable,
            Item = new Dictionary<string, AttributeValue>
            {
                [NamespaceAttribute] = new(namespacePrefix),
                [RepoAttribute] = new(modelRepo),
                ["expires_at"] = new() { N = now.Add(cacheRetention).ToUnixTimeSeconds().ToString() },
            },
        }, cancellationToken).ConfigureAwait(false);

        return new RepoResolution(modelRepo, RepoSource.Model);
    }

    /// <summary>
    /// TODO: not implemented. Deciding which repository owns a namespace needs the
    /// org's repository list, and nothing yet plumbs that through to this function
    /// (a GitHub App installation listing, or an equivalent config source, would
    /// supply it). The signature exists so callers and tests can sit above this
    /// seam already; inventing a Bedrock call against a guessed prompt shape would
    /// be worse than leaving it unbuilt.
    /// </summary>
    public static Task<string> ResolveWithModelAsync(
        string namespacePrefix, IReadOnlyList<string> orgRepositories, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "Model-based repo resolution needs the org repository list plumbed through first.");
}
