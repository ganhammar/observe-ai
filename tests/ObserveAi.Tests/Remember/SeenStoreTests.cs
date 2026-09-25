using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ObserveAi;

namespace ObserveAi.Tests;

public class SeenStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>A counter that behaves the way DynamoDB's ADD does.</summary>
    private static SeenStore.UpdateItem Counter(Dictionary<string, long> store, List<UpdateItemRequest> seen)
    {
        return (request, _) =>
        {
            seen.Add(request);
            var key = $"{request.Key["repo"].S}/{request.Key["fingerprint"].S}";
            store[key] = store.GetValueOrDefault(key) + 1;
            return Task.FromResult(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>
                {
                    ["occurrences"] = new() { N = store[key].ToString() },
                },
            });
        };
    }

    [Fact]
    public async Task FirstSightingIsReportedOnceAndOnlyOnce()
    {
        Dictionary<string, long> store = [];
        List<UpdateItemRequest> seen = [];
        var counter = Counter(store, seen);

        var first = await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc123", Now, Retention);
        var second = await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc123", Now, Retention);
        var third = await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc123", Now, Retention);

        Assert.True(first.IsFirst);
        Assert.False(second.IsFirst);
        Assert.False(third.IsFirst);
        Assert.Equal(3, third.Occurrences);
    }

    [Fact]
    public async Task TheSameExceptionInTwoRepositoriesIsTwoDefects()
    {
        Dictionary<string, long> store = [];
        var counter = Counter(store, []);

        var one = await SeenStore.RecordAsync(counter, "t", "acme/orders", "same", Now, Retention);
        var other = await SeenStore.RecordAsync(counter, "t", "acme/billing", "same", Now, Retention);

        Assert.True(one.IsFirst);
        Assert.True(other.IsFirst);
    }

    [Fact]
    public async Task CountingIsOneRequestSoConcurrentArrivalsCannotBothBeFirst()
    {
        Dictionary<string, long> store = [];
        List<UpdateItemRequest> seen = [];

        await SeenStore.RecordAsync(Counter(store, seen), "t", "acme/orders", "abc", Now, Retention);

        // A read followed by a write would let two log lines both see zero and
        // both trigger triage. The increment and the first-sighting test have to
        // be the same operation.
        var request = Assert.Single(seen);
        Assert.Contains("ADD occurrences :one", request.UpdateExpression);
        Assert.Equal(ReturnValue.ALL_NEW, request.ReturnValues);
    }

    [Fact]
    public async Task RecordsSetAnExpirySoDormantFailuresDoNotAccumulate()
    {
        List<UpdateItemRequest> seen = [];
        await SeenStore.RecordAsync(Counter([], seen), "t", "acme/orders", "abc", Now, Retention);

        var ttl = long.Parse(seen[0].ExpressionAttributeValues[":ttl"].N);
        Assert.Equal(Now.Add(Retention).ToUnixTimeSeconds(), ttl);
    }
}
