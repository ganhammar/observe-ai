using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace ObserveAi;

/// <summary>What the store knew about a fingerprint before this occurrence.</summary>
public sealed record SeenResult(bool IsFirst, long Occurrences, bool ShouldTriage);

/// <summary>
/// Counts occurrences per distinct failure so the triage tree runs once rather
/// than once per log line. The nine model calls are only affordable because
/// almost every log arriving here is a repeat of something already decided.
///
/// The counter is also the first rate limiter: a defect firing ten thousand
/// times increments a number instead of opening ten thousand issues.
///
/// A repeat is re-triaged, not just counted, when its evidence has moved: a
/// database genuinely out of connections and a service leaking them can throw
/// the byte-identical stack trace, and only the surrounding evidence tells
/// them apart, so whichever cause arrives first must not get the only say.
/// </summary>
public static class SeenStore
{
    /// <summary>The one DynamoDB operation this needs, so a test can supply it directly.</summary>
    public delegate Task<UpdateItemResponse> UpdateItem(UpdateItemRequest request, CancellationToken cancellationToken);

    public static UpdateItem Against(IAmazonDynamoDB dynamo) => dynamo.UpdateItemAsync;

    /// <summary>
    /// Records one occurrence and reports whether it is the first, and
    /// separately whether it should be triaged: first sighting, or the
    /// evidence signature has changed since the one last stored.
    ///
    /// The key is repository plus fingerprint, not fingerprint alone: two
    /// services throwing the same framework exception are not the same defect,
    /// and a global key would merge them silently.
    ///
    /// A single UpdateItem does the read, the increment, the first-sighting
    /// test and the signature comparison together, so two log lines arriving
    /// at once cannot both believe they are first and both trigger triage.
    /// </summary>
    /// <param name="evidenceSignature">
    /// EvidenceSignature.Compute's result for this occurrence's evidence.
    /// Callers that never pass one keep the pre-existing behaviour: triage
    /// runs on first sighting only, since an unset signature never changes.
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
        // A response with no previous_signature attribute counts as unchanged, so a
        // caller that never passes evidenceSignature only ever triages on first sighting.
        var previousSignature = response.Attributes.TryGetValue("previous_signature", out var previous)
            ? previous.S
            : evidenceSignature;
        return new SeenResult(isFirst, occurrences, isFirst || previousSignature != evidenceSignature);
    }
}
