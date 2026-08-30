using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Storage.Tests.CloudScope;

/// <summary>
/// What an administrator is told about the grants covering a connection's buckets.
/// </summary>
[Trait("Category", "Unit")]
public class GrantCoverageReporterTests
{
    private readonly IAccessGrantsReader grants = Substitute.For<IAccessGrantsReader>();

    private static IOptionsMonitor<SamlSignInSettings> SignIn(bool configured)
    {
        var monitor = Substitute.For<IOptionsMonitor<SamlSignInSettings>>();
        monitor.CurrentValue.Returns(configured
            ? new SamlSignInSettings
            {
                EntityId = "https://connapse.example/saml/connapse",
                AcsUrl = "https://connapse.example/api/v1/auth/cloud/aws/acs",
                IdpEntityId = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/X",
                IdpSingleSignOnUrl = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/X",
                IdpSigningCertificate = "MIICert",
            }
            : new SamlSignInSettings());

        return monitor;
    }

    private GrantCoverageReporter Reporter(bool configured = true) =>
        new(grants, SignIn(configured), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<GrantCoverageReporter>.Instance);

    [Fact]
    public async Task CheckAsync_WithSignInUnconfigured_SaysNothingAndDoesNotAskAws()
    {
        // A deployment that does not filter expects no grants. Warning there would put a permanent
        // complaint on every S3 connection in every installation not using this feature.
        var report = await Reporter(configured: false).CheckAsync(["reports"]);

        report.Outcome.Should().Be(CoverageOutcome.NotFiltering);
        report.HasWarning.Should().BeFalse();
        await grants.DidNotReceive().ListAllScopesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_WithAGrantCoveringTheBucket_HasNoWarning()
    {
        grants.ListAllScopesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(["s3://reports/*"]);

        var report = await Reporter().CheckAsync(["reports"]);

        report.Outcome.Should().Be(CoverageOutcome.Checked);
        report.HasWarning.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_WithNoGrantTouchingTheBucket_NamesIt()
    {
        grants.ListAllScopesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(["s3://elsewhere/*"]);

        var report = await Reporter().CheckAsync(["reports", "elsewhere"]);

        report.HasWarning.Should().BeTrue();
        report.Ungranted.Should().Equal("reports");
    }

    [Fact]
    public async Task CheckAsync_WhenAwsCannotBeAsked_ClaimsNothingEitherWay()
    {
        // Reporting every bucket as ungranted on an outage would send somebody to author grants
        // that already exist, and teach them the warning is noise.
        grants.ListAllScopesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(_ => throw new InvalidOperationException("throttled"));

        var report = await Reporter().CheckAsync(["reports"]);

        report.Outcome.Should().Be(CoverageOutcome.Unavailable);
        report.HasWarning.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_AsksAwsOnceForSeveralConnections()
    {
        // A page listing connections asks this repeatedly, and the answer is the same for all of
        // them. One call, not one per row.
        grants.ListAllScopesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(["s3://reports/*"]);

        var reporter = Reporter();
        await reporter.CheckAsync(["reports"]);
        await reporter.CheckAsync(["other"]);
        await reporter.CheckAsync(["third"]);

        await grants.Received(1).ListAllScopesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_WithNoGrantsAtAll_NamesEveryBucket()
    {
        // The state a fresh Access Grants instance is in, and the one this exists to surface.
        grants.ListAllScopesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>([]);

        var report = await Reporter().CheckAsync(["alpha", "beta"]);

        report.Ungranted.Should().Equal("alpha", "beta");
    }
}
