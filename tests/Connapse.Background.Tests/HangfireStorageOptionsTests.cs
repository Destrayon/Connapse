using FluentAssertions;
using Hangfire.PostgreSql;

namespace Connapse.Background.Tests;

[Trait("Category", "Unit")]
public class HangfireStorageOptionsTests
{
    [Fact]
    public void StorageOptions_LongJobs_KeepTheirFetchRenewedInsteadOfBeingHandedToAnotherWorker()
    {
        PostgreSqlStorageOptions options = HangfireServiceCollectionExtensions.StorageOptions();

        options.UseSlidingInvisibilityTimeout.Should().BeTrue(
            "a fixed timeout re-delivers an ingestion still running after it, which cancels the first run");
        options.InvisibilityTimeout.Should().Be(TimeSpan.FromMinutes(30));
    }
}
