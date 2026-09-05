using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Tests;

/// <summary>
/// The two short-lived stores a SAML sign-in passes through: who started it, and what came back.
/// </summary>
/// <remarks>
/// They exist as separate steps because neither can do the other's job. The first survives a
/// cross-site POST that carries no session; the second waits for a session before anything is
/// written. Splitting them is what stops an assertion completed in one browser being saved against
/// an account signed in somewhere else.
/// </remarks>
[Trait("Category", "Unit")]
public class SamlSignInHandoffTests
{
    private static SamlSignInRequests Requests() => new(new MemoryCache(new MemoryCacheOptions()));

    private static SamlLinkConfirmations Confirmations() =>
        new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void Start_ThenConsume_ReturnsWhoStartedItAndWhatTheyAsked()
    {
        var requests = Requests();
        Guid userId = Guid.NewGuid();

        string nonce = requests.Start(userId, "_authn-1");

        var started = requests.Consume(nonce);
        started.Should().NotBeNull();
        started!.Value.UserId.Should().Be(userId);
        started.Value.AuthnRequestId.Should().Be("_authn-1");
    }

    [Fact]
    public void Consume_Twice_ResolvesToNobodyTheSecondTime()
    {
        // A signed assertion is a bearer credential until it expires. Single use here means a
        // replayed RelayState is refused before the assertion is even parsed.
        var requests = Requests();
        string nonce = requests.Start(Guid.NewGuid(), "_authn-1");

        requests.Consume(nonce).Should().NotBeNull();
        requests.Consume(nonce).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a-nonce-nobody-issued")]
    public void Consume_WithNothingThisDeploymentIssued_ResolvesToNobody(string? nonce) =>
        Requests().Consume(nonce).Should().BeNull();

    [Fact]
    public void Start_WithoutAnAuthnRequestId_Throws()
    {
        // The id is what the assertion has to name back. Recording a sign-in without one would
        // leave nothing to check InResponseTo against, silently.
        var act = () => Requests().Start(Guid.NewGuid(), "  ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Nonces_AreNotReused()
    {
        var requests = Requests();
        var nonces = Enumerable.Range(0, 50)
            .Select(_ => requests.Start(Guid.NewGuid(), "_authn"))
            .ToList();

        nonces.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Confirmation_RoundTripsTheValidatedOutcome()
    {
        var confirmations = Confirmations();
        Guid starter = Guid.NewGuid();

        string code = confirmations.Start(
            new PendingIdentityLink(starter, "dir-1", "person", "person@example.com"));

        var link = confirmations.Consume(code);
        link.Should().NotBeNull();
        link!.StartedByUserId.Should().Be(starter);
        link.DirectoryUserId.Should().Be("dir-1");
        link.DirectoryUserName.Should().Be("person");
        link.Email.Should().Be("person@example.com");
    }

    [Fact]
    public void Confirmation_IsSingleUse()
    {
        // So an interrupted confirmation cannot be replayed out of browser history.
        var confirmations = Confirmations();
        string code = confirmations.Start(
            new PendingIdentityLink(Guid.NewGuid(), "dir-1", "person", null));

        confirmations.Consume(code).Should().NotBeNull();
        confirmations.Consume(code).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a-code-nobody-issued")]
    public void Confirmation_WithNothingThisDeploymentIssued_ResolvesToNothing(string? code) =>
        Confirmations().Consume(code).Should().BeNull();

    [Fact]
    public void Confirmation_RecordsWhoStartedTheSignIn_NotWhoTheAssertionNames()
    {
        // The distinction the whole handoff rests on. The assertion says which directory user
        // signed it; only the started-by id says which Connapse account asked for it. Keeping both
        // is what lets the confirmation step notice they disagree.
        var confirmations = Confirmations();
        Guid attacker = Guid.NewGuid();

        string code = confirmations.Start(
            new PendingIdentityLink(attacker, "victim-directory-id", "victim", null));

        var link = confirmations.Consume(code)!;
        link.StartedByUserId.Should().Be(attacker);
        link.DirectoryUserId.Should().Be("victim-directory-id");
    }
}
