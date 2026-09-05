using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class GrantPlannerTests
{
    [Fact]
    public void Plan_LocationWithNoExistingGrant_IsToCreateAsSubPrefixStar()
    {
        var plan = GrantPlanner.Plan(["my-bucket/docs"], existingScopes: []);

        plan.ToCreate.Should().ContainSingle().Which.Should().Be("my-bucket/docs/*");
        plan.AlreadyGranted.Should().BeEmpty();
    }

    [Fact]
    public void Plan_LocationAlreadyGranted_IsSkipped()
    {
        var plan = GrantPlanner.Plan(
            ["my-bucket/docs"],
            existingScopes: ["s3://my-bucket/docs/*"]);

        plan.ToCreate.Should().BeEmpty();
        plan.AlreadyGranted.Should().ContainSingle().Which.Should().Be("my-bucket/docs/*");
    }

    [Fact]
    public void Plan_BucketRoot_BecomesBucketStar()
    {
        var plan = GrantPlanner.Plan(["my-bucket"], existingScopes: []);

        plan.ToCreate.Should().ContainSingle().Which.Should().Be("my-bucket/*");
    }

    [Fact]
    public void Plan_TrailingSlashAndDuplicates_AreNormalisedAndDeduped()
    {
        var plan = GrantPlanner.Plan(
            ["my-bucket/docs/", "my-bucket/docs"],
            existingScopes: []);

        plan.ToCreate.Should().ContainSingle().Which.Should().Be("my-bucket/docs/*");
    }

    [Fact]
    public void Plan_UnsafeLocation_IsDropped()
    {
        var plan = GrantPlanner.Plan(["bad bucket$name"], existingScopes: []);

        plan.ToCreate.Should().BeEmpty();
        plan.AlreadyGranted.Should().BeEmpty();
    }
}
