using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace ObserveAi;

/// <summary>What the store knew about a fingerprint before this occurrence.</summary>
public sealed record SeenResult(bool IsFirst, long Occurrences, bool ShouldTriage);

/// <summary>
/// Counts occurrences per repository and fingerprint so the nine-call triage tree runs once per failure. A
/// repeat is re-triaged when its evidence signature changes: a database out of connections and a service
/// leaking them can throw the same stack trace, and only the evidence tells them apart.
/// </summary>
public static class SeenStore
{
    /// <summary>The one DynamoDB operation SeenStore uses. Tests substitute a fake.</summary>
    public delegate Task<UpdateItemResponse> UpdateItem(UpdateItemRequest request, CancellationToken cancellationToken);

    public static UpdateItem Against(IAmazonDynamoDB dynamo) => dynamo.UpdateItemAsync;

    /// <summary>
    /// Records one occurrence and reports whether to triage it: on first sighting, or when the evidence
    /// signature differs from the stored one. The key is repository plus fingerprint, since two services
    /// throwing the same framework exception are separate defects. One UpdateItem does the increment, the
    /// first-sighting test and the signature swap, so two concurrent lines cannot both trigger triage.
    /// </summary>
    /// <param name="evidenceSignature">
    /// EvidenceSignature.Compute's result for this occurrence. Left empty, the signature never changes and
    /// triage runs on first sighting only.
    /// </param>
    public static async Task<SeenResult> RecordAsync(
        UpdateItem updateItem, string table, string repo, string fingerprint,
        DateTimeOffset now, TimeSpan retention, string evidenceSignature = "",
        CancellationToken cancellationToken = default)
    {
        var response = await updateItem(new UpdateItemRequest
        {
            TableName = table,
            Key = new Dictionary<string, AttributeValue>
            {
                ["repo"] = new(repo),
                ["fingerprint"] = new(fingerprint),
            },
            UpdateExpression =
                "ADD occurrences :one SET first_seen = if_not_exists(first_seen, :now), last_seen = :now, "
                + "expires_at = :ttl, previous_signature = if_not_exists(evidence_signature, :sig), "
                + "evidence_signature = :sig",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":one"] = new() { N = "1" },
                [":now"] = new(now.ToString("O")),
                [":ttl"] = new() { N = now.Add(retention).ToUnixTimeSeconds().ToString() },
                [":sig"] = new(evidenceSignature),
            },
            ReturnValues = ReturnValue.ALL_NEW,
        }, cancellationToken).ConfigureAwait(false);

        var occurrences = long.Parse(response.Attributes["occurrences"].N);
        var isFirst = occurrences == 1;
        // A missing previous_signature counts as unchanged.
        var previousSignature = response.Attributes.TryGetValue("previous_signature", out var previous)
            ? previous.S
            : evidenceSignature;
        return new SeenResult(isFirst, occurrences, isFirst || previousSignature != evidenceSignature);
    }
}
