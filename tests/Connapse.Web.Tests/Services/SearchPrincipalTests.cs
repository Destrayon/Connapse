using System.Security.Claims;
using Connapse.Web.Services;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Services;

/// <summary>
/// Which person a search runs for, across the four surfaces that ask.
/// </summary>
/// <remarks>
/// The answer is currently carried and not consumed — per-user filtering is #421. These tests exist
/// now because the resolution rules are where the mistakes are, and they are much easier to get
/// right before something depends on them than after.
/// </remarks>
[Trait("Category", "Unit")]
public class SearchPrincipalTests
{
    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Agent = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Resolve_ForASignedInUser_ReturnsTheirId()
    {
        SearchPrincipal.Resolve(Authenticated(
            new Claim(ClaimTypes.NameIdentifier, User.ToString()),
            new Claim(SearchPrincipal.ActorTypeClaim, "user")))
            .Should().Be(User);
    }

    [Fact]
    public void Resolve_ForAnAgent_ReturnsTheUserItActsFor()
    {
        // The one that would be wrong by default. An agent authenticates as itself, so
        // NameIdentifier holds the agent's id -- reading that as a user id yields a Guid matching
        // no user, which resolves to no scopes: a denial by accident, indistinguishable from a
        // real one.
        SearchPrincipal.Resolve(Authenticated(
            new Claim(ClaimTypes.NameIdentifier, Agent.ToString()),
            new Claim(SearchPrincipal.ActorTypeClaim, "agent"),
            new Claim(SearchPrincipal.OnBehalfOfClaim, User.ToString())))
            .Should().Be(User);
    }

    [Fact]
    public void Resolve_ForAnAgent_NeverReturnsTheAgentsOwnId()
    {
        SearchPrincipal.Resolve(Authenticated(
            new Claim(ClaimTypes.NameIdentifier, Agent.ToString()),
            new Claim(SearchPrincipal.ActorTypeClaim, "agent"),
            new Claim(SearchPrincipal.OnBehalfOfClaim, User.ToString())))
            .Should().NotBe(Agent);
    }

    [Fact]
    public void Resolve_ForAnAgentWithNoUserBehindIt_ReturnsNull()
    {
        // The shape a standalone agent will have once agents can exist without a creator. Null
        // rather than a fallback to the agent's own id, because that fallback would quietly grant
        // whatever a user with that Guid happened to have.
        SearchPrincipal.Resolve(Authenticated(
            new Claim(ClaimTypes.NameIdentifier, Agent.ToString()),
            new Claim(SearchPrincipal.ActorTypeClaim, "agent")))
            .Should().BeNull();
    }

    [Fact]
    public void Resolve_ForAnAnonymousRequest_ReturnsNull()
    {
        SearchPrincipal.Resolve(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeNull();
    }

    [Fact]
    public void Resolve_ForNoPrincipalAtAll_ReturnsNull()
    {
        SearchPrincipal.Resolve(null).Should().BeNull();
    }

    [Fact]
    public void Resolve_ForAnUnparseableIdentifier_ReturnsNull()
    {
        // Null, not an exception. A malformed claim is something an attacker can arrange, and a
        // search surface that throws on one is a denial-of-service rather than a refusal.
        SearchPrincipal.Resolve(Authenticated(
            new Claim(ClaimTypes.NameIdentifier, "not-a-guid")))
            .Should().BeNull();
    }
}
