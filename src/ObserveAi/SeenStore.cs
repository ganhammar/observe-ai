using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace ObserveAi;

/// <summary>What the store knew about a fingerprint before this occurrence.</summary>
public sealed record SeenResult(bool IsFirst, long Occurrences);

/// <summary>
/// Counts occurrences per distinct failure so the triage tree runs once rather
/// than once per log line. The nine model calls are only affordable because
/// almost every log arriving here is a repeat of something already decided.
///
/// The counter is also the first rate limiter: a defect firing ten thousand
/// times increments a number instead of opening ten thousand issues.
/// </summary>
public static class SeenStore
{
    /// <summary>
    /// Records one occurrence and reports whether it is the first.
    ///
    /// The key is repository plus fingerprint, not fingerprint alone: two
    /// services throwing the same framework exception are not the same defect,
    /// and a global key would merge them silently.
    ///
    /// A single UpdateItem does the read, the increment and the first-sighting
    /// test together, so two log lines arriving at once cannot both believe they
    /// are first and both trigger triage.
    /// </summary>
    /// <summary>The one DynamoDB operation this needs, so a test can supply it directly.</summary>
    public delegate Task<UpdateItemResponse> UpdateItem(UpdateItemRequest request, CancellationToken cancellationToken);

    public static UpdateItem Against(IAmazonDynamoDB dynamo) => dynamo.UpdateItemAsync;

    public static async Task<SeenResult> RecordAsync(
        UpdateItem updateItem, string table, string repo, string fingerprint,
        DateTimeOffset now, TimeSpan retention, CancellationToken cancellationToken = default)
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
                "ADD occurrences :one SET first_seen = if_not_exists(first_seen, :now), last_seen = :now, expires_at = :ttl",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":one"] = new() { N = "1" },
                [":now"] = new(now.ToString("O")),
                [":ttl"] = new() { N = now.Add(retention).ToUnixTimeSeconds().ToString() },
            },
            ReturnValues = ReturnValue.ALL_NEW,
        }, cancellationToken).ConfigureAwait(false);

        var occurrences = long.Parse(response.Attributes["occurrences"].N);
        return new SeenResult(occurrences == 1, occurrences);
    }
}
