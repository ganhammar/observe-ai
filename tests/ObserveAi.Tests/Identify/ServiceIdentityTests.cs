using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>Checks log group parsing and the repository naming convention built on top of it.</summary>
public class ServiceIdentityTests
{
    [Theory]
    [InlineData("/aws/lambda/billing-sync", "acme/billing-sync")]
    [InlineData("/ecs/orders", "acme/orders")]
    public void ConventionalRepoCombinesTheOrgWithTheResourceName(string logGroupName, string expectedRepo)
    {
        Assert.Equal(expectedRepo, ServiceIdentity.ConventionalRepo(logGroupName, "acme"));
    }

    [Fact]
    public void ConventionalRepoIsNullWhenTheLogGroupDoesNotMatchAKnownShape()
    {
        Assert.Null(ServiceIdentity.ConventionalRepo("some-custom-log-group", "acme"));
    }

    [Theory]
    [InlineData("/aws/lambda/checkout-api", "checkout-api")]
    [InlineData("/aws/ecs/orders", "orders")]
    [InlineData("/ecs/orders", "orders")]
    [InlineData("/aws/eks/orders-cluster", "orders-cluster")]
    public void ParsesEachKnownLogGroupShape(string logGroupName, string resourceName)
    {
        Assert.Equal(resourceName, ServiceIdentity.ParseLogGroup(logGroupName));
    }

    [Theory]
    [InlineData("/aws/rds/orders-db")]
    [InlineData("/aws/eks/")]
    [InlineData("/aws/lambda/")]
    [InlineData("some-custom-log-group")]
    [InlineData("")]
    public void UnrecognisedLogGroupsReturnNull(string logGroupName)
    {
        Assert.Null(ServiceIdentity.ParseLogGroup(logGroupName));
    }
}
