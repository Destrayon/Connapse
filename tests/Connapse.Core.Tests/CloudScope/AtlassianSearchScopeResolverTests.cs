using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using NSubstitute;
using Xunit;
using static Connapse.Core.Tests.CloudScope.ConfluencePermissionCheckerTests;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public sealed class AtlassianSearchScopeResolverTests
{
    private readonly IAtlassianIdentityLinkReader _links = Substitute.For<IAtlassianIdentityLinkReader>();
    private readonly AtlassianSearchScopeResolver _resolver;
    private readonly Guid _linked = Guid.NewGuid();

    public AtlassianSearchScopeResolverTests()
    {
        _links.GetLinkAsync(_linked, Arg.Any<CancellationToken>()).Returns(new AtlassianIdentityRef("557058:abc", "Ada"));
        _resolver = new AtlassianSearchScopeResolver(AtlassianConnectionStore(), _links);
    }

    [Fact]
    public async Task Resolve_NoPrincipal_IsNoPrincipal() =>
        (await _resolver.ResolveAsync(null)).Should().Be(SearchScopes.NoPrincipal);

    [Fact]
    public async Task Resolve_Unlinked_IsNone() =>
        (await _resolver.ResolveAsync(Guid.NewGuid())).Outcome.Should().Be(SearchScopes.None.Outcome);

    [Fact]
    public async Task Resolve_Linked_GrantsEachSitePrefix()
    {
        var scopes = await _resolver.ResolveAsync(_linked);

        scopes.Outcome.Should().Be(ScopeOutcome.Granted);
        scopes.Matches.Select(m => m.Value).Should().Equal($"atlassian://{CloudId}/");
    }
}
