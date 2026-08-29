using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
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
    private const string OtherUserEmail = "other-cognito-user@integration-tests.connapse.io";
    private const string OtherUserPassword = "OtherCognitoUserTest1!";

    // Must match the private cookie-name consts in CloudIdentityEndpoints — there is no public
    // surface to read them from, and the user-mismatch test needs the raw stashed state value.
    private const string CognitoStateCookieName = "__connapse_cog_state";
    private const string CognitoPkceCookieName = "__connapse_cog_pkce";
    private const string CognitoNonceCookieName = "__connapse_cog_nonce";

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

    /// <summary>Creates a second Connapse user distinct from the admin, and returns its id.</summary>
    private static async Task<Guid> SeedOtherUserAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ConnapseUser>>();

        var existing = await userManager.FindByEmailAsync(OtherUserEmail);
        if (existing is not null)
            return existing.Id;

        var user = new ConnapseUser
        {
            UserName = OtherUserEmail,
            Email = OtherUserEmail,
            EmailConfirmed = true,
            DisplayName = OtherUserEmail,
            CreatedAt = DateTime.UtcNow,
        };

        var createResult = await userManager.CreateAsync(user, OtherUserPassword);
        createResult.Succeeded.Should().BeTrue(
            because: string.Join(", ", createResult.Errors.Select(e => e.Description)));

        await userManager.AddToRoleAsync(user, "Viewer");
        return user.Id;
    }

    private async Task<string> LoginAsAsync(string email, string password)
    {
        using var anonClient = fixture.Factory.CreateClient();
        var response = await anonClient.PostAsJsonAsync("/api/v1/auth/token", new LoginRequest(email, password));
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"login as {email} should succeed");

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        token.Should().NotBeNull();
        return token!.AccessToken;
    }

    /// <summary>Pulls the three stashed Cognito cookies' raw name=value pairs off a /connect response.</summary>
    private static Dictionary<string, string> ExtractCognitoCookies(HttpResponseMessage response)
    {
        var names = new[] { CognitoStateCookieName, CognitoPkceCookieName, CognitoNonceCookieName };
        var result = new Dictionary<string, string>();

        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
            return result;

        foreach (var header in setCookieHeaders)
        {
            var nameValue = header.Split(';', 2)[0];
            var parts = nameValue.Split('=', 2);
            if (parts.Length == 2 && names.Contains(parts[0]))
                result[parts[0]] = parts[1];
        }

        return result;
    }

    private static string BuildCookieHeader(Dictionary<string, string> cookies) =>
        string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));

    [Fact]
    public async Task Connect_WhenCognitoIsNotConfigured_Returns409()
    {
        // A deployment with no Cognito settings must fail in a way that says so. Redirecting to a
        // half-built URL sends the user to a 404 on a domain they have never heard of.
        var response = await fixture.AdminClient.GetAsync("/api/v1/auth/cloud/cognito/connect");

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

        var response = await anonClient.GetAsync("/api/v1/auth/cloud/cognito/connect");

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound, "the route must actually be registered");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Callback_WithNoState_IsRejected()
    {
        // An unsolicited callback is either a bug or an attempt to plant a token against someone
        // else's account. Either way it is not a connection.
        var response = await fixture.AdminClient.GetAsync("/api/v1/auth/cloud/cognito/callback?code=abc");

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound, "the route must actually be registered");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a required query parameter is missing, matching how the Azure callback rejects the same shape of request");
    }

    [Fact]
    public async Task Connect_RequestsOnlyScopesCognitoAndTheAppClientBothAllow()
    {
        // `offline_access` shipped here and broke every connection attempt: Cognito has no such
        // scope, so it rejected the authorize request outright with error=invalid_request and
        // error_description=invalid_scope, and the user reached the hosted login page. The refresh
        // token this flow stores arrives with the code grant on its own.
        //
        // The list is also bounded by the app client the setup script creates, whose
        // AllowedOAuthScopes are openid, email and profile. Asking for anything outside that set
        // fails the same way, so this asserts the whole scope parameter rather than just the
        // absence of the one value that caused the outage.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        await store.SaveAsync("cognito", ConfiguredSettings());
        try
        {
            using var client = CreateAuthenticatedClient();

            var response = await client.GetAsync("/api/v1/auth/cloud/cognito/connect");

            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var query = System.Web.HttpUtility.ParseQueryString(
                new Uri(response.Headers.Location!.ToString()).Query);
            var scopes = (query["scope"] ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

            scopes.Should().NotContain("offline_access", "Cognito rejects the whole request for it");
            scopes.Should().BeSubsetOf(["openid", "email", "profile"],
                "the app client the setup script creates allows only these three");
            scopes.Should().Contain("openid", "the flow needs an ID token");
            scopes.Should().Contain("email",
                "the trusted token issuer matches the email claim to an Identity Center user");
        }
        finally
        {
            await store.SaveAsync("cognito", new CognitoSettings());
        }
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

            var connectResponse = await client.GetAsync("/api/v1/auth/cloud/cognito/connect");
            connectResponse.StatusCode.Should().Be(HttpStatusCode.Redirect,
                "Cognito is configured now, so this must actually start a connection and stash a state cookie");

            // A state nobody issued, sent alongside the *real* stashed cookie from the call above.
            var response = await client.GetAsync(
                "/api/v1/auth/cloud/cognito/callback?code=abc&state=not-a-state-we-issued");

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
    public async Task Callback_WhenSignedInUserDiffersFromWhoStartedTheFlow_IsRejectedAndStoresNothing()
    {
        // The failure this guards against needs no attacker: user A clicks Connect, goes to the
        // Cognito hosted UI, and while they are there their Connapse session ends and user B signs
        // in on the same browser. The redirect lands inside the cookie window. State, PKCE verifier
        // and nonce are all still valid — they are browser-scoped cookies, not session-scoped — so
        // without the fix the callback would decide whose account to link by reading whichever
        // principal happens to be signed in right now (B), storing A's verified AWS identity
        // against B's account.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        await store.SaveAsync("cognito", ConfiguredSettings());
        try
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
            var initiatingUser = await userManager.FindByEmailAsync(SharedWebAppFixture.AdminEmail);
            initiatingUser.Should().NotBeNull("the shared admin account is the one used to start the flow below");

            var switchedInUserId = await SeedOtherUserAsync(scope.ServiceProvider);
            var switchedInUserToken = await LoginAsAsync(OtherUserEmail, OtherUserPassword);

            // Start the flow as the admin (user A) — this is the request whose cookies carry A's id.
            using var initiatingClient = CreateAuthenticatedClient();
            var connectResponse = await initiatingClient.GetAsync("/api/v1/auth/cloud/cognito/connect");
            connectResponse.StatusCode.Should().Be(HttpStatusCode.Redirect,
                "Cognito is configured, so /connect must stash state and redirect");

            var cognitoCookies = ExtractCognitoCookies(connectResponse);
            cognitoCookies.Should().ContainKey(CognitoStateCookieName,
                "the state cookie has to exist for the mismatch check downstream to have anything to parse");

            // The callback arrives with the same browser's cookies, but a different signed-in user
            // (B) — simulated here by sending them explicitly on a client authenticated as B rather
            // than A, since the two are otherwise indistinguishable to the server. HandleCookies
            // must be off: WebApplicationFactory's cookie handler manages the Cookie header from
            // its own (empty, for this brand-new client) container and discards a manually set one
            // otherwise, which would defeat the whole point of forwarding A's cookies here.
            using var switchedInClient = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
            switchedInClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", switchedInUserToken);

            // Set-Cookie values are percent-encoded by ASP.NET Core (Response.Cookies.Append
            // escapes them; Request.Cookies[name] decodes them back on the way in). The raw
            // extracted cookie value is already in that encoded form, so it goes on the Cookie
            // header unchanged — but the query string's `state` needs the *decoded* value
            // re-escaped exactly once, or the endpoint reads two different strings for
            // "the state the cookie carries" vs. "the state the query string carries" and
            // rejects the callback as a state mismatch before ever reaching the check this test
            // is for.
            var decodedState = Uri.UnescapeDataString(cognitoCookies[CognitoStateCookieName]);
            var callbackUrl =
                $"/api/v1/auth/cloud/cognito/callback?code=abc&state={Uri.EscapeDataString(decodedState)}";
            using var callbackRequest = new HttpRequestMessage(HttpMethod.Get, callbackUrl);
            callbackRequest.Headers.TryAddWithoutValidation("Cookie", BuildCookieHeader(cognitoCookies));

            var callbackResponse = await switchedInClient.SendAsync(callbackRequest);

            callbackResponse.StatusCode.Should().NotBe(HttpStatusCode.NotFound, "the route must actually be registered");
            callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect,
                "a user mismatch is reported the same way the other rejection reasons are: a redirect back with ?error=");
            callbackResponse.Headers.Location.Should().NotBeNull();
            callbackResponse.Headers.Location!.ToString().Should().Contain("error=cognito_user_mismatch");

            var linkStore = scope.ServiceProvider.GetRequiredService<AwsIdentityLinkStore>();
            (await linkStore.GetAsync(initiatingUser!.Id)).Should().BeNull(
                "the user who started the flow must not gain a link from a callback that ran under someone else's session");
            (await linkStore.GetAsync(switchedInUserId)).Should().BeNull(
                "the user signed in at callback time must not gain a link either — the callback stores nothing on a mismatch");
        }
        finally
        {
            await store.SaveAsync("cognito", new CognitoSettings());
        }
    }

    [Fact]
    public async Task SavedSettings_ReachTheOptionsMonitor()
    {
        // The two-place registration (options binding, category-to-section map) is invisible
        // until it is wrong, and when it is wrong the failure looks like a broken endpoint rather
        // than a missing dictionary entry. This is the only test that would catch the category
        // prefix and the section name disagreeing: reading the row back through the store wouldn't,
        // because that returns whatever was stored directly, regardless of whether IOptionsMonitor
        // ever picked it up.
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

    // ── The settings API is not a way in ──────────────────────────────

    [Fact]
    public async Task GetSettings_Cognito_IsNotServed()
    {
        // Not merely unsupported: the pool's client secret is a deployment-wide credential, and a
        // read arm would hand it to anyone holding an admin token. Configuration is the admin UI
        // only, which writes through ISettingsStore rather than over HTTP.
        var response = await fixture.AdminClient.GetAsync("/api/settings/cognito");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateSettings_Cognito_IsNotServed()
    {
        var response = await fixture.AdminClient.PutAsJsonAsync(
            "/api/settings/cognito", ConfiguredSettings());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateSettings_Cognito_LeavesTheStoredPoolAlone()
    {
        // A 404 that had already written would be worse than no endpoint at all. This asserts the
        // refusal happens before the save, not after it.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

        await store.SaveAsync("cognito", new CognitoSettings());

        await fixture.AdminClient.PutAsJsonAsync("/api/settings/cognito", ConfiguredSettings());

        var stored = await store.GetAsync<CognitoSettings>("cognito");
        stored?.ClientId.Should().BeNullOrEmpty();
    }
}
