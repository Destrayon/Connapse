using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureSearchScopeResolverTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly AzureIdentityRef Link = new("oid-1", "tid-1");

    private static IOptionsMonitor<T> Opt<T>(T value) where T : class
    {
        var m = Substitute.For<IOptionsMonitor<T>>();
        m.CurrentValue.Returns(value);
        return m;
    }

    private static AzureSearchScopeResolver Build(
        IAzureIdentityLinkReader? links = null,
        IAzureDirectoryReader? directory = null,
        bool azureAdConfigured = true,
        bool isEnforcing = true,
        bool determined = true)
    {
        var azureAd = Opt(new AzureAdSignInSettings
        {
            TenantId = azureAdConfigured ? "t" : null,
            ClientId = azureAdConfigured ? "c" : null,
            RedirectUri = azureAdConfigured ? "https://x/cb" : null,
            ClientCertificatePath = azureAdConfigured ? "cert.pem" : null,
        });
        // The Azure resolver gates on the independent Azure latch, not the SAML/AWS one.
        var enforcement = Opt(new PermissionEnforcementSettings { AzureEnforcing = isEnforcing });
        EnforcementMigration migration = determined ? EnforcementMigration.Completed() : new EnforcementMigration();

        return new AzureSearchScopeResolver(
            links ?? Substitute.For<IAzureIdentityLinkReader>(),
            directory ?? Substitute.For<IAzureDirectoryReader>(),
            azureAd, enforcement, migration,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AzureSearchScopeResolver>.Instance);
    }

    [Fact]
    public async Task NotEnforcing_IsUnrestricted()
    {
        var r = Build(isEnforcing: false);
        (await r.ResolveAsync(User)).IsUnrestricted.Should().BeTrue();
    }

    [Fact]
    public async Task Enforcing_ButAzureAdNotConfigured_Fails()
    {
        var r = Build(azureAdConfigured: false);
        (await r.ResolveAsync(User)).Outcome.Should().Be(ScopeOutcome.ResolverFailed);
    }

    [Fact]
    public async Task Undetermined_Fails()
    {
        var r = Build(determined: false);
        (await r.ResolveAsync(User)).Outcome.Should().Be(ScopeOutcome.ResolverFailed);
    }

    [Fact]
    public async Task NullUser_IsNoPrincipal()
    {
        var r = Build();
        (await r.ResolveAsync(null)).Outcome.Should().Be(ScopeOutcome.NoPrincipal);
    }

    [Fact]
    public async Task NoLink_IsNoPrincipal()
    {
        var links = Substitute.For<IAzureIdentityLinkReader>();
        links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns((AzureIdentityRef?)null);
        var r = Build(links: links);
        (await r.ResolveAsync(User)).Outcome.Should().Be(ScopeOutcome.NoPrincipal);
    }

    [Fact]
    public async Task Deprovisioned_IsNoPrincipal()
    {
        var links = Substitute.For<IAzureIdentityLinkReader>();
        links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns(Link);
        var directory = Substitute.For<IAzureDirectoryReader>();
        directory.ResolveAsync(Link, Arg.Any<CancellationToken>()).Returns(AzureIdentitySet.Deprovisioned());
        var r = Build(links: links, directory: directory);
        (await r.ResolveAsync(User)).Outcome.Should().Be(ScopeOutcome.NoPrincipal);
    }

    [Fact]
    public async Task IdentityResolutionFailed_Fails()
    {
        var links = Substitute.For<IAzureIdentityLinkReader>();
        links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns(Link);
        var directory = Substitute.For<IAzureDirectoryReader>();
        directory.ResolveAsync(Link, Arg.Any<CancellationToken>()).Returns(AzureIdentitySet.Failed());
        var r = Build(links: links, directory: directory);
        (await r.ResolveAsync(User)).Outcome.Should().Be(ScopeOutcome.ResolverFailed);
    }

    [Fact]
    public async Task ValidEnforcingIdentity_RetrievesAllAzblob_ForTheVerifierToTighten()
    {
        var links = Substitute.For<IAzureIdentityLinkReader>();
        links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns(Link);
        var directory = Substitute.For<IAzureDirectoryReader>();
        directory.ResolveAsync(Link, Arg.Any<CancellationToken>()).Returns(AzureIdentitySet.Resolved(["oid-1"]));
        var r = Build(links: links, directory: directory);

        SearchScopes s = await r.ResolveAsync(User);

        s.Outcome.Should().Be(ScopeOutcome.Granted);
        s.Matches.Should().ContainSingle().Which.Value.Should().Be("azblob://");
        s.Matches[0].IsExact.Should().BeFalse();
    }
}
