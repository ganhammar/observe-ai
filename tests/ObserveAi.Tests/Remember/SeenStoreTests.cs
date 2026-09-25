using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ObserveAi;

namespace ObserveAi.Tests;

public class SeenStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>A counter that behaves the way DynamoDB's ADD and if_not_exists(first_seen) do.</summary>
    private static Dynamo.UpdateItem Counter(Dictionary<string, (long Count, string FirstSeen)> store, List<UpdateItemRequest> seen)
    {
        return (request, _) =>
        {
            seen.Add(request);
            var key = $"{request.Key["repo"].S}/{request.Key["fingerprint"].S}";
            var (count, firstSeen) = store.GetValueOrDefault(key, (0, request.ExpressionAttributeValues[":now"].S));
            store[key] = (count + 1, firstSeen);
            return Task.FromResult(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>
                {
                    ["occurrences"] = new() { N = (count + 1).ToString() },
                    ["first_seen"] = new(firstSeen),
                },
            });
        };
    }

    [Fact]
    public async Task FirstSightingIsReportedOnceAndOnlyOnce()
    {
        var counter = Counter([], []);

        var first = await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc123", Now, Retention);
        var second = await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc123", Now, Retention);
        var third = await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc123", Now, Retention);

        Assert.Equal(1, first.Occurrences);
        Assert.Equal(2, second.Occurrences);
        Assert.Equal(3, third.Occurrences);
    }

    [Fact]
    public async Task TheSameExceptionInTwoRepositoriesIsTwoDefects()
    {
        var counter = Counter([], []);

        var one = await SeenStore.RecordAsync(counter, "t", "acme/orders", "same", Now, Retention);
        var other = await SeenStore.RecordAsync(counter, "t", "acme/billing", "same", Now, Retention);

        Assert.Equal(1, one.Occurrences);
        Assert.Equal(1, other.Occurrences);
    }

    [Fact]
    public async Task CountingIsOneRequestSoConcurrentArrivalsCannotBothBeFirst()
    {
        List<UpdateItemRequest> seen = [];

        await SeenStore.RecordAsync(Counter([], seen), "t", "acme/orders", "abc", Now, Retention);

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

    [Fact]
    public async Task ARepeatReportsTheFirstSightingsTime()
    {
        var counter = Counter([], []);

        await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc", Now, Retention);
        var repeat = await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc", Now.AddDays(3), Retention);

        Assert.Equal(Now, repeat.FirstSeen);
    }
}
