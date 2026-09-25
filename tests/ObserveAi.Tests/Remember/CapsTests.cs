using Amazon.DynamoDBv2.Model;
using ObserveAi;

namespace ObserveAi.Tests;

public class CapsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    /// <summary>A counter keyed by bucket, behaving the way DynamoDB's ADD does.</summary>
    private static Dynamo.UpdateItem Counter(Dictionary<string, long> store)
    {
        return (request, _) =>
        {
            var bucket = request.Key["bucket"].S;
            store[bucket] = store.GetValueOrDefault(bucket) + 1;
            return Task.FromResult(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>
                {
                    ["occurrences"] = new() { N = store[bucket].ToString() },
                },
            });
        };
    }

    [Fact]
    public async Task UnderTheLimitAllows()
    {
        var result = await Caps.TryConsumeAsync(
            Counter([]), "t", "acme/orders", perRepoLimit: 5, globalLimit: 20, Now, Retention);

        Assert.True(result.Allowed);
        Assert.False(result.Tripped);
        Assert.Equal(1, result.Count);
        Assert.Equal(5, result.Limit);
    }

    [Fact]
    public async Task CrossingTheLimitReportsRollupRatherThanThrowing()
    {
        var counter = Counter([]);
        CapResult result = null!;

        for (var i = 0; i < 7; i++)
        {
            result = await Caps.TryConsumeAsync(counter, "t", "acme/orders", perRepoLimit: 5, globalLimit: 100, Now, Retention);
        }

        Assert.False(result.Allowed);
        Assert.Equal(7, result.Count);
    }

    [Fact]
    public async Task TheHourBucketRollsOver()
    {
        var counter = Counter([]);

        var thisHour = await Caps.TryConsumeAsync(counter, "t", "acme/orders", perRepoLimit: 5, globalLimit: 100, Now, Retention);
        var nextHour = await Caps.TryConsumeAsync(
            counter, "t", "acme/orders", perRepoLimit: 5, globalLimit: 100, Now.AddHours(1), Retention);

        Assert.Equal(1, thisHour.Count);
        Assert.Equal(1, nextHour.Count);
    }

    [Fact]
    public async Task TwoRepositoriesHaveIndependentBudgets()
    {
        var counter = Counter([]);

        for (var i = 0; i < 5; i++)
        {
            await Caps.TryConsumeAsync(counter, "t", "acme/orders", perRepoLimit: 5, globalLimit: 100, Now, Retention);
        }
        var other = await Caps.TryConsumeAsync(counter, "t", "acme/billing", perRepoLimit: 5, globalLimit: 100, Now, Retention);

        Assert.True(other.Allowed);
        Assert.Equal(1, other.Count);
    }

    [Fact]
    public async Task TheGlobalBreakerTripsIndependentlyOfAnySingleRepositorysBudget()
    {
        var counter = Counter([]);
        string[] repos = ["acme/orders", "acme/billing", "acme/payments", "acme/search"];
        CapResult result = null!;

        foreach (var repo in repos)
        {
            result = await Caps.TryConsumeAsync(counter, "t", repo, perRepoLimit: 5, globalLimit: 3, Now, Retention);
        }

        Assert.True(result.Tripped);
        Assert.True(result.Allowed);
    }
}
