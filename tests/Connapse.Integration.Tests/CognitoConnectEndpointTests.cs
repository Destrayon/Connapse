using System.Net;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Connapse.Integration.Tests;

/// <summary>
/// Starting and finishing a Cognito connection.
/// </summary>
/// <remarks>
/// The happy-path callback cannot be tested here: completing it needs an authorization code that
/// only a real Cognito pool issues, and a real ID token signed by that pool's key. What is testable,
/// and what these cover, is everything the endpoint decides on its own — whether it redirects at
/// all, what it redirects to, what it refuses, and that a saved setting actually reaches
/// <see cref="IOptionsMonitor{TOptions}"/>. The rejection paths inside ID token validation itself
/// (bad signature, wrong nonce, unverified email) are covered separately in
/// <c>CognitoIdTokenValidatorTests</c> against tokens forged with a throwaway key, since that logic
/// needs no live pool and no HTTP round trip to exercise.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CognitoConnectEndpointTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Connect_WhenCognitoIsNotConfigured_Returns409()
    {
        // A deployment with no Cognito settings must fail in a way that says so. Redirecting to a
        // half-built URL sends the user to a 404 on a domain they have never heard of.
        var response = await fixture.AdminClient.GetAsync("/api/cloud-identity/cognito/connect");

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound, "the route must actually be registered");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "there is nowhere valid to redirect to");
    }

    [Fact]
    public async Task Connect_Unauthenticated_Returns401()
    {
        using var anonClient = fixture.Factory.CreateClient(new()
        {
            AllowAutoRedirect = false,
        });

        var response = await anonClient.GetAsync("/api/cloud-identity/cognito/connect");

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound, "the route must actually be registered");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Callback_WithNoState_IsRejected()
    {
        // An unsolicited callback is either a bug or an attempt to plant a token against someone
        // else's account. Either way it is not a connection.
        var response = await fixture.AdminClient.GetAsync("/api/cloud-identity/cognito/callback?code=abc");

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound, "the route must actually be registered");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a required query parameter is missing, matching how the Azure callback rejects the same shape of request");
    }

    [Fact]
    public async Task Callback_WithMismatchedState_IsRejected()
    {
        // Start a real connection first so a valid state exists to mismatch against — comparing
        // against a state nobody issued would test a different, easier branch.
        var connectResponse = await fixture.AdminClient.GetAsync("/api/cloud-identity/cognito/connect");
        connectResponse.StatusCode.Should().NotBe(HttpStatusCode.NotFound, "the route must actually be registered");

        var response = await fixture.AdminClient.GetAsync(
            "/api/cloud-identity/cognito/callback?code=abc&state=not-a-state-we-issued");

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound, "the route must actually be registered");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SavedSettings_ReachTheOptionsMonitor()
    {
        // The three-place registration (options binding, category-to-section map, settings
        // endpoint arms) is invisible until it is wrong, and when it is wrong the failure looks
        // like a broken endpoint rather than a missing dictionary entry. This is the only test
        // that would catch the category prefix and the section name disagreeing: reading back
        // through GET /api/settings/cognito wouldn't, because that endpoint returns whatever is
        // stored directly, regardless of whether IOptionsMonitor ever picked it up.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

        await store.SaveAsync("cognito", new CognitoSettings
        {
            IssuerUrl = "https://cognito-idp.us-west-1.amazonaws.com/us-west-1_test",
            Domain = "https://example.auth.us-west-1.amazoncognito.com",
            ClientId = "client-id",
            ClientSecret = "secret",
            Region = "us-west-1",
        });

        var monitor = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CognitoSettings>>();
        monitor.CurrentValue.IsConfigured.Should().BeTrue();
        monitor.CurrentValue.ClientId.Should().Be("client-id");

        // Restore, so a later test in the shared fixture doesn't see Cognito as configured.
        await store.SaveAsync("cognito", new CognitoSettings());
    }
}
