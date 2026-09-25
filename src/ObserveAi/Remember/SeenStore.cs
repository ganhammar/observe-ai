using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace ObserveAi;

/// <summary>The fingerprint's occurrence count including this one, and when it was first seen.</summary>
public sealed record SeenResult(long Occurrences, DateTimeOffset FirstSeen);

/// <summary>
/// Counts occurrences per repository and fingerprint so the triage tree runs once per failure: on the
/// occurrence that brings the count to one.
/// </summary>
public static class SeenStore
{
    /// <summary>The one DynamoDB operation SeenStore uses. Tests substitute a fake.</summary>
    public delegate Task<UpdateItemResponse> UpdateItem(UpdateItemRequest request, CancellationToken cancellationToken);

    public static UpdateItem Against(IAmazonDynamoDB dynamo) => dynamo.UpdateItemAsync;

    /// <summary>
    /// Records one occurrence. The key is repository plus fingerprint, since two services throwing the same
    /// framework exception are separate defects. One UpdateItem does the increment and returns the new
    /// count, so two concurrent lines cannot both see a count of one.
    /// </summary>
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

        return new SeenResult(
            long.Parse(response.Attributes["occurrences"].N),
            DateTimeOffset.Parse(response.Attributes["first_seen"].S, CultureInfo.InvariantCulture));
    }
}
