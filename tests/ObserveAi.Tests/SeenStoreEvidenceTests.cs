using Amazon.DynamoDBv2.Model;
using ObserveAi;

namespace ObserveAi.Tests;

public class SeenStoreEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>A counter that also tracks the last stored signature, the way ADD plus if_not_exists does.</summary>
    private static SeenStore.UpdateItem Counter(Dictionary<string, (long Count, string? Signature)> store)
    {
        return (request, _) =>
        {
            var key = $"{request.Key["repo"].S}/{request.Key["fingerprint"].S}";
            var incoming = request.ExpressionAttributeValues[":sig"].S;
            var (count, storedSignature) = store.GetValueOrDefault(key, (0, null));
            var previous = storedSignature ?? incoming;
            store[key] = (count + 1, incoming);

            return Task.FromResult(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>
                {
                    ["occurrences"] = new() { N = (count + 1).ToString() },
                    ["previous_signature"] = new(previous),
                },
            });
        };
    }

    [Fact]
    public async Task FirstSightingAlwaysTriages()
    {
        var result = await SeenStore.RecordAsync(Counter([]), "t", "acme/orders", "abc", Now, Retention, "sig-a");

        Assert.True(result.IsFirst);
        Assert.True(result.ShouldTriage);
    }

    [Fact]
    public async Task ARepeatWithTheSameSignatureDoesNotRetriage()
    {
        var counter = Counter([]);

        await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc", Now, Retention, "sig-a");
        var repeat = await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc", Now, Retention, "sig-a");

        Assert.False(repeat.IsFirst);
        Assert.False(repeat.ShouldTriage);
    }

    [Fact]
    public async Task ARepeatWithADifferentSignatureRetriagesAndUpdatesTheStoredSignature()
    {
        var store = new Dictionary<string, (long Count, string? Signature)>();
        var counter = Counter(store);

        await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc", Now, Retention, "sig-a");
        var changed = await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc", Now, Retention, "sig-b");
        var third = await SeenStore.RecordAsync(counter, "t", "acme/orders", "abc", Now, Retention, "sig-b");

        Assert.True(changed.ShouldTriage);
        Assert.False(third.ShouldTriage);
        Assert.Equal("sig-b", store["acme/orders/abc"].Signature);
    }

    [Fact]
    public async Task ACallerThatNeverPassesASignatureOnlyTriagesOnFirstSighting()
    {
        // A caller with no notion of evidence signatures gets a response with no
        // previous_signature attribute at all, the same shape SeenStoreTests uses.
        Dictionary<string, long> store = [];
        SeenStore.UpdateItem legacy = (request, _) =>
        {
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

        var first = await SeenStore.RecordAsync(legacy, "t", "acme/orders", "abc", Now, Retention);
        var second = await SeenStore.RecordAsync(legacy, "t", "acme/orders", "abc", Now, Retention);

        Assert.True(first.ShouldTriage);
        Assert.False(second.ShouldTriage);
    }
}
