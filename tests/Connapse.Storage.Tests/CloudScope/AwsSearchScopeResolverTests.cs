using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope;

/// <summary>
/// What a Connapse user may search, resolved from S3 access grants with Connapse's own identity.
/// </summary>
/// <remarks>
/// Weighted towards the ways this can refuse. Returning too little is a person saying they cannot
/// find a document; returning too much is a disclosure nobody reports, so every path that cannot
/// produce a confident answer is pinned here rather than left to inspection.
/// </remarks>
[Trait("Category", "Unit")]
public class AwsSearchScopeResolverTests
{
    private const string DirectoryUserId = "a1b2c3d4-5678-90ab-cdef-EXAMPLE11111";

    private readonly IAwsIdentityLinkReader links = Substitute.For<IAwsIdentityLinkReader>();
    private readonly IDirectoryUserLookup directory = Substitute.For<IDirectoryUserLookup>();
    private readonly IAccessGrantsReader grants = Substitute.For<IAccessGrantsReader>();

    private static SamlSignInSettings Configured() => new()
    {
        EntityId = "https://connapse.example.com/saml/connapse",
        AcsUrl = "https://connapse.example.com/api/v1/auth/cloud/aws/acs",
        IdpEntityId = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE",
        IdpSingleSignOnUrl = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE",
        IdpSigningCertificate = "MIIDBTCCAe2gAwIBAgIFEXAMPLE",
    };

    private static IOptionsMonitor<SamlSignInSettings> Monitor(SamlSignInSettings settings)
    {
        var monitor = Substitute.For<IOptionsMonitor<SamlSignInSettings>>();
        monitor.CurrentValue.Returns(settings);
        return monitor;
    }

    private AwsSearchScopeResolver Build(SamlSignInSettings? settings = null) =>
        new(links, directory, grants,
            Monitor(settings ?? Configured()),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AwsSearchScopeResolver>.Instance);

    private void LinkedAndEnabled()
    {
        links.GetDirectoryUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(DirectoryUserId);
        directory.DescribeAsync(DirectoryUserId, Arg.Any<CancellationToken>())
            .Returns(new DirectoryUser(DirectoryUserId, "diviel", "diviel@example.com", Enabled: true));
        directory.ListGroupIdsAsync(DirectoryUserId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
    }

    [Fact]
    public async Task NotConfigured_IsUnrestricted()
    {
        // The one legitimate Unrestricted. Filtering is opt-in, and denying here would leave every
        // installation that upgraded without setting this up unable to search anything.
        var result = await Build(new SamlSignInSettings()).ResolveAsync(Guid.NewGuid());

        result.IsUnrestricted.Should().BeTrue();
        result.Outcome.Should().Be(ScopeOutcome.Unrestricted);
    }

    [Fact]
    public async Task NoUser_IsNoPrincipal_NotUnrestricted()
    {
        var result = await Build().ResolveAsync(null);

        result.IsUnrestricted.Should().BeFalse();
        result.Outcome.Should().Be(ScopeOutcome.NoPrincipal);
    }

    [Fact]
    public async Task NoLinkedIdentity_IsNoPrincipal()
    {
        links.GetDirectoryUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await Build().ResolveAsync(Guid.NewGuid());

        result.Outcome.Should().Be(ScopeOutcome.NoPrincipal);
        result.IsUnrestricted.Should().BeFalse();
    }

    [Fact]
    public async Task DeletedDirectoryUser_IsRefused()
    {
        // Revocation is detected rather than awaited: no credential remains to expire, so this call
        // noticing is the only thing that stops a deprovisioned person searching.
        links.GetDirectoryUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(DirectoryUserId);
        directory.DescribeAsync(DirectoryUserId, Arg.Any<CancellationToken>())
            .Returns((DirectoryUser?)null);

        var result = await Build().ResolveAsync(Guid.NewGuid());

        result.Outcome.Should().Be(ScopeOutcome.NoPrincipal);
    }

    [Fact]
    public async Task DisabledDirectoryUser_IsRefused()
    {
        links.GetDirectoryUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(DirectoryUserId);
        directory.DescribeAsync(DirectoryUserId, Arg.Any<CancellationToken>())
            .Returns(new DirectoryUser(DirectoryUserId, "diviel", null, Enabled: false));

        var result = await Build().ResolveAsync(Guid.NewGuid());

        result.Outcome.Should().Be(ScopeOutcome.NoPrincipal);
    }

    [Fact]
    public async Task WhenAwsThrows_FailsClosed()
    {
        // The single most important test here. An AWS outage, a missing permission or a throttle
        // must never widen a search — Failed and Unrestricted are opposites.
        LinkedAndEnabled();
        grants.ListForGranteeAsync(Arg.Any<AccessGrantee>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<AccessGrantRecord>>>(_ => throw new InvalidOperationException("boom"));

        var result = await Build().ResolveAsync(Guid.NewGuid());

        result.Outcome.Should().Be(ScopeOutcome.ResolverFailed);
        result.IsUnrestricted.Should().BeFalse();
        result.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task NoGrants_IsNoGrants_NotUnrestricted()
    {
        LinkedAndEnabled();
        grants.ListForGranteeAsync(Arg.Any<AccessGrantee>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await Build().ResolveAsync(Guid.NewGuid());

        result.Outcome.Should().Be(ScopeOutcome.NoGrants);
        result.IsUnrestricted.Should().BeFalse();
    }

    [Fact]
    public async Task DirectGrants_BecomePrefixes()
    {
        LinkedAndEnabled();
        grants.ListForGranteeAsync(
                Arg.Is<AccessGrantee>(g => !g.IsGroup), Arg.Any<CancellationToken>())
            .Returns([new AccessGrantRecord("s3://acme/team/*", false, null)]);

        var result = await Build().ResolveAsync(Guid.NewGuid());

        result.Outcome.Should().Be(ScopeOutcome.Granted);
        result.Matches.Should().ContainSingle()
            .Which.Value.Should().Be("s3://acme/team/");
    }

    [Fact]
    public async Task GroupHeldGrants_AreIncluded()
    {
        // ListAccessGrants matches the grant record literally and does not expand membership, so a
        // grant made to a group is invisible when asking about one of its members. Missing this
        // would look like a permissions bug in AWS rather than an omission here.
        links.GetDirectoryUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(DirectoryUserId);
        directory.DescribeAsync(DirectoryUserId, Arg.Any<CancellationToken>())
            .Returns(new DirectoryUser(DirectoryUserId, "diviel", null, Enabled: true));
        directory.ListGroupIdsAsync(DirectoryUserId, Arg.Any<CancellationToken>())
            .Returns(new[] { "group-1" });

        grants.ListForGranteeAsync(
                Arg.Is<AccessGrantee>(g => !g.IsGroup), Arg.Any<CancellationToken>())
            .Returns([]);
        grants.ListForGranteeAsync(
                Arg.Is<AccessGrantee>(g => g.IsGroup && g.Id == "group-1"), Arg.Any<CancellationToken>())
            .Returns([new AccessGrantRecord("s3://shared/*", false, null)]);

        var result = await Build().ResolveAsync(Guid.NewGuid());

        result.Matches.Should().ContainSingle()
            .Which.Value.Should().Be("s3://shared/");
    }

    [Fact]
    public async Task GrantBoundToAnotherApplication_IsNotHonoured()
    {
        // Connapse presents no application identity, so AWS would refuse this grant anywhere it
        // was actually exercised. Showing the documents anyway would be a disclosure that looks
        // like a correctly configured grant.
        LinkedAndEnabled();
        grants.ListForGranteeAsync(Arg.Any<AccessGrantee>(), Arg.Any<CancellationToken>())
            .Returns([
                new AccessGrantRecord("s3://acme/team/*", false,
                    "arn:aws:sso::1:application/ssoins-1/apl-other"),
                new AccessGrantRecord("s3://acme/open/*", false, "ALL"),
            ]);

        var result = await Build().ResolveAsync(Guid.NewGuid());

        result.Matches.Should().ContainSingle()
            .Which.Value.Should().Be("s3://acme/open/");
    }

    [Fact]
    public async Task ObjectGrant_MatchesExactly()
    {
        // No trailing asterisk, so it names one object. AWS never reports S3PrefixType back, and
        // treating this as a prefix would also admit "report.pdf.bak".
        LinkedAndEnabled();
        grants.ListForGranteeAsync(Arg.Any<AccessGrantee>(), Arg.Any<CancellationToken>())
            .Returns([new AccessGrantRecord("s3://acme/report.pdf", true, null)]);

        var result = await Build().ResolveAsync(Guid.NewGuid());

        var match = result.Matches.Should().ContainSingle().Subject;
        match.Value.Should().Be("s3://acme/report.pdf");
        match.IsExact.Should().BeTrue();
    }
}
