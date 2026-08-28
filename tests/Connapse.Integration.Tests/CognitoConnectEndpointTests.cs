using System.Net;
using System.Net.Http.Headers;
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
/// (bad signature, wrong nonce, unverified email) and the validation parameters the endpoint builds
/// are covered separately in <c>CognitoIdTokenValidatorTests</c> against tokens forged with a
/// throwaway key, since none of that needs a live pool or an HTTP round trip.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CognitoConnectEndpointTests(SharedWebAppFixture fixture)
{
    private static CognitoSettings ConfiguredSettings() => new()
    {
        IssuerUrl = "https://cognito-idp.us-west-1.amazonaws.com/us-west-1_test",
        Domain = "https://example.auth.us-west-1.amazoncognito.com",
        ClientId = "client-id",
        ClientSecret = "secret",
        Region = "us-west-1",
    };

    // A fresh, authenticated client per test that needs one — never the shared fixture.AdminClient
    // — for two reasons: AllowAutoRedirect must be false (the shared client defaults to true, which
    // would try to actually follow the redirect out to the fake configured Cognito domain), and each
    // test needs its own cookie jar so a stashed state/PKCE/nonce cookie from one test's /connect
    // call can never leak into another test's /callback call.
    private HttpClient CreateAuthenticatedClient()
    {
        var client = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.AdminToken);
        return client;
    }

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
        // This must actually reach the `expectedState != state` branch, not the
        // `expectedState is missing` branch Callback_WithNoState_IsRejected already covers — which
        // means a real /connect call has to have set a real state cookie first. Cognito has to be
        // configured for /connect to do anything but 409.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        await store.SaveAsync("cognito", ConfiguredSettings());
        try
        {
            // One client for both calls: WebApplicationFactoryClientOptions.HandleCookies defaults
            // to true, so the state/PKCE/nonce cookies /connect sets are carried automatically into
            // the /callback request below — no live Cognito pool involved, since /connect only
            // builds a redirect URL and never dials out.
            using var client = CreateAuthenticatedClient();

            var connectResponse = await client.GetAsync("/api/cloud-identity/cognito/connect");
            connectResponse.StatusCode.Should().Be(HttpStatusCode.Redirect,
                "Cognito is configured now, so this must actually start a connection and stash a state cookie");

            // A state nobody issued, sent alongside the *real* stashed cookie from the call above.
            var response = await client.GetAsync(
                "/api/cloud-identity/cognito/callback?code=abc&state=not-a-state-we-issued");

            response.StatusCode.Should().NotBe(HttpStatusCode.NotFound, "the route must actually be registered");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            // Always restore, even if an assertion above threw — otherwise this leaves Cognito
            // configured for whichever test in this shared fixture happens to run next, turning one
            // real failure into a second, unrelated one.
            await store.SaveAsync("cognito", new CognitoSettings());
        }
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

        await store.SaveAsync("cognito", ConfiguredSettings());
        try
        {
            var monitor = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CognitoSettings>>();
            monitor.CurrentValue.IsConfigured.Should().BeTrue();
            monitor.CurrentValue.ClientId.Should().Be("client-id");
        }
        finally
        {
            // Always restore, even if an assertion above threw — otherwise Connect_WhenCognitoIsNotConfigured_Returns409
            // fails as collateral, pointing at the wrong place.
            await store.SaveAsync("cognito", new CognitoSettings());
        }
    }
}
