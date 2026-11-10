using FluentAssertions;
using Hangfire.PostgreSql;

namespace Connapse.Background.Tests;

[Trait("Category", "Unit")]
public class HangfireStorageOptionsTests
{
    [Fact]
    public void CreateStorageOptions_Default_KeepsLeaseOfLongRunningJobs()
    {
        PostgreSqlStorageOptions options = HangfireServiceCollectionExtensions.CreateStorageOptions();

        options.UseSlidingInvisibilityTimeout.Should().BeTrue(
            "without it a job running past the invisibility timeout is fetched again and the first run cancelled (CON-317)");
        options.InvisibilityTimeout.Should().Be(TimeSpan.FromMinutes(30));
    }
}
