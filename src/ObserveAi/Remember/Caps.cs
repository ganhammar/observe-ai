using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace ObserveAi;

/// <summary>What the rate limits decided for one filing attempt.</summary>
public sealed record CapResult(bool Allowed, long Count, long Limit, bool Tripped);

/// <summary>
/// Two brakes on how many issues one bad deploy can file: a per-repository hourly cap whose overflow rolls
/// into one incident issue, and a global breaker that stops filing and tells the caller to notify. Each is a
/// DynamoDB counter bumped with ADD and ALL_NEW in one UpdateItem, so racing invocations cannot both see
/// themselves under the limit.
/// </summary>
public static class Caps
{
    /// <summary>The one DynamoDB operation Caps uses. Tests substitute a fake.</summary>
    public delegate Task<UpdateItemResponse> UpdateItem(UpdateItemRequest request, CancellationToken cancellationToken);

    public static UpdateItem Against(IAmazonDynamoDB dynamo) => dynamo.UpdateItemAsync;

    /// <summary>
    /// Increments this repository's bucket and the global bucket for the current UTC hour, so a bad deploy at
    /// 09:00 does not spend the budget a failure at 14:00 needs.
    /// </summary>
    public static async Task<CapResult> TryConsumeAsync(
        UpdateItem updateItem, string table, string repo,
        long perRepoLimit, long globalLimit,
        DateTimeOffset now, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        var hour = now.ToUniversalTime().ToString("yyyyMMddHH");

        var repoCount = await IncrementAsync(updateItem, table, $"repo#{repo}#{hour}", now, retention, cancellationToken)
            .ConfigureAwait(false);
        var globalCount = await IncrementAsync(updateItem, table, $"global#{hour}", now, retention, cancellationToken)
            .ConfigureAwait(false);

        return new CapResult(repoCount <= perRepoLimit, repoCount, perRepoLimit, globalCount > globalLimit);
    }

    private static async Task<long> IncrementAsync(
        UpdateItem updateItem, string table, string bucket, DateTimeOffset now, TimeSpan retention,
        CancellationToken cancellationToken)
    {
        var response = await updateItem(new UpdateItemRequest
        {
            TableName = table,
            Key = new Dictionary<string, AttributeValue> { ["bucket"] = new(bucket) },
            UpdateExpression = "ADD occurrences :one SET expires_at = :ttl",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":one"] = new() { N = "1" },
                [":ttl"] = new() { N = now.Add(retention).ToUnixTimeSeconds().ToString() },
            },
            ReturnValues = ReturnValue.ALL_NEW,
        }, cancellationToken).ConfigureAwait(false);

        return long.Parse(response.Attributes["occurrences"].N);
    }
}
