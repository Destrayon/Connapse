using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for CloudIdentity endpoints (/api/v1/auth/cloud/).
/// Tests basic endpoint availability and auth requirements.
/// The external AWS SAML flow requires a mocked identity provider and is not covered here.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CloudIdentityEndpointTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task DisconnectIdentity_NoIdentity_Returns404()
    {
        // Act: Try to disconnect AWS identity that doesn't exist
        var response = await fixture.AdminClient.DeleteAsync(
            "/api/v1/auth/cloud/AWS");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DisconnectAws_WithASamlLink_DeletesTheLinkAndReturns204()
    {
        // The route used to delete from the cloud-identity table, which never holds a SAML link,
        // so a user with a real link got 404 and stayed eligible for AWS-derived search scopes.
        Guid admin = AdminUserId();
        string directoryUserId = $"dir-{Guid.NewGuid():N}";

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<AwsIdentityLinkStore>();
            await store.SaveAsync(admin, directoryUserId, "admin-person", "admin@example.com");
        }

        try
        {
            var response = await fixture.AdminClient.DeleteAsync("/api/v1/auth/cloud/AWS");

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await LinkedDirectoryUserIdAsync(admin)).Should().BeNull("the link was deleted");
        }
        finally
        {
            // Leave the shared admin as the other tests expect to find them: unlinked.
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<AwsIdentityLinkStore>();
            await store.DeleteAsync(admin);
        }
    }

    /// <summary>The admin's own user id, read from the token the fixture signed in with.</summary>
    private Guid AdminUserId()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(fixture.AdminToken);
        string? sub = token.Claims.FirstOrDefault(c =>
            c.Type is "sub" or "nameid"
                   or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

        Guid.TryParse(sub, out Guid id).Should().BeTrue("the token identifies the signed-in user");
        return id;
    }

    private async Task<string?> LinkedDirectoryUserIdAsync(Guid userId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<AwsIdentityLinkStore>();
        return await store.GetDirectoryUserIdAsync(userId);
    }

    // --- Azure (Entra user identity link) ---

    private const string AzureIssuer = $"https://login.microsoftonline.com/{SharedWebAppFixture.AzureTestTenantId}/v2.0";

    /// <summary>Mirrors the production cookie name in CloudIdentityEndpoints — there is no public
    /// constant to reuse, so this is kept in sync by hand, the same way
    /// SamlLinkConfirmationTests does for the AWS cookie.</summary>
    private const string AzureConfirmCookieName = "__connapse_azure_link";

    private const string VictimEmail = "azure-victim@integration-tests.connapse.io";
    private const string VictimPassword = "AzureVictimTest1!";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private HttpClient NoRedirectClient(bool authenticated = true)
    {
        var client = fixture.Factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (authenticated)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", fixture.AdminToken);
        }
        return client;
    }

    /// <summary>
    /// A second, unprivileged Connapse account distinct from the shared admin — the "colleague" an
    /// attacker sends a captured /azure/connect URL to. Seeded once and reused; idempotent so
    /// running alongside other tests in the shared fixture is safe.
    /// </summary>
    private async Task<(Guid Id, string Token)> EnsureVictimUserAsync()
    {
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
            if (await userManager.FindByEmailAsync(VictimEmail) is null)
            {
                var user = new ConnapseUser
                {
                    UserName = VictimEmail,
                    Email = VictimEmail,
                    EmailConfirmed = true,
                    DisplayName = "Azure CSRF Victim",
                    CreatedAt = DateTime.UtcNow,
                };

                var result = await userManager.CreateAsync(user, VictimPassword);
                result.Succeeded.Should().BeTrue(
                    because: string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }

        using HttpClient anonClient = fixture.Factory.CreateClient();
        var response = await anonClient.PostAsJsonAsync(
            "/api/v1/auth/token", new LoginRequest(VictimEmail, VictimPassword));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);

        await using var idScope = fixture.Factory.Services.CreateAsyncScope();
        var lookup = idScope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        ConnapseUser victim = (await lookup.FindByEmailAsync(VictimEmail))!;

        return (victim.Id, token!.AccessToken);
    }

    /// <summary>Reads the value Set-Cookie gave a named cookie — the callback's confirm cookie is
    /// HttpOnly, so a client without a cookie jar (as these no-redirect clients are) has to be
    /// handed it explicitly to carry into the next request, exactly like
    /// SamlLinkConfirmationTests does for the AWS cookie.</summary>
    private static string ExtractCookieValue(HttpResponseMessage response, string cookieName)
    {
        foreach (string setCookie in response.Headers.GetValues("Set-Cookie"))
        {
            if (!setCookie.StartsWith($"{cookieName}=", StringComparison.Ordinal))
                continue;

            string valuePart = setCookie[(cookieName.Length + 1)..];
            int semicolon = valuePart.IndexOf(';');
            return semicolon >= 0 ? valuePart[..semicolon] : valuePart;
        }

        throw new InvalidOperationException($"Response did not set the '{cookieName}' cookie.");
    }

    private static async Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string? cookieValue)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/cloud/azure/confirm");
        if (cookieValue is not null)
            request.Headers.Add("Cookie", $"{AzureConfirmCookieName}={cookieValue}");

        return await client.SendAsync(request);
    }

    /// <summary>Builds a raw id_token JWT signed with the fake host's signing key.</summary>
    private static string BuildIdToken(string nonce, string oid, string tid, string name)
    {
        var signingCredentials = new SigningCredentials(
            FakeAzureSigningKeySource.SigningKey, SecurityAlgorithms.RsaSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = AzureIssuer,
            Audience = SharedWebAppFixture.AzureTestClientId,
            NotBefore = DateTime.UtcNow.AddMinutes(-5),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = signingCredentials,
            Claims = new Dictionary<string, object>
            {
                ["nonce"] = nonce,
                ["oid"] = oid,
                ["tid"] = tid,
                ["name"] = name,
            },
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>Naive query-string parser — avoids pulling in an extra package for the test.</summary>
    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                kv => Uri.UnescapeDataString(kv[0]),
                kv => Uri.UnescapeDataString(kv.Length > 1 ? kv[1] : string.Empty));

    private async Task<UserAzureIdentityLinkEntity?> AzureLinkAsync(Guid userId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<AzureIdentityLinkStore>();
        return await store.GetAsync(userId);
    }

    private async Task DeleteAzureLinkAsync(Guid userId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<AzureIdentityLinkStore>();
        await store.DeleteAsync(userId);
    }

    [Fact]
    public async Task AzureConnect_WhenConfigured_RedirectsToEntraWithPkceAndRecordsPending()
    {
        using var client = NoRedirectClient();

        var response = await client.GetAsync("/api/v1/auth/cloud/azure/connect");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        Uri location = response.Headers.Location!;
        location.Host.Should().Be("login.microsoftonline.com");

        var query = ParseQuery(location.Query);
        query.Should().ContainKey("state");
        query.Should().ContainKey("code_challenge");
        query["code_challenge_method"].Should().Be("S256");
        query["response_type"].Should().Be("code");

        // Consuming here doubles as the "a pending entry exists" assertion — TakeByState is the
        // only way this store exposes to check, and nothing else in this test needs the entry
        // afterwards.
        var pending = fixture.Factory.Services.GetRequiredService<AzureSignInRequests>();
        AzurePendingSignIn? p = pending.TakeByState(query["state"]);

        p.Should().NotBeNull("connect must record a pending sign-in for the state it redirects with");
        p!.UserId.Should().Be(AdminUserId());
        p.Nonce.Should().Be(query["nonce"]);
    }

    [Fact]
    public async Task AzureCallback_UnknownState_RedirectsWithGenericErrorAndStoresNothing()
    {
        using var client = NoRedirectClient(authenticated: false);

        var response = await client.GetAsync(
            "/api/v1/auth/cloud/azure/callback?code=whatever&state=a-state-this-deployment-never-issued");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("error=azure_link_failed");

        (await AzureLinkAsync(AdminUserId())).Should().BeNull("an unknown state must not store a link");
    }

    [Fact]
    public async Task AzureCallback_HappyPath_RoutesThroughConfirm_StoresLinkAndRedirectsToIntegrations()
    {
        // The ordinary flow: the same user starts the sign-in and completes the confirm step. The
        // callback itself must not store anything directly any more — it can only park the outcome
        // and hand back a cookie, exactly like AWS's /acs.
        Guid admin = AdminUserId();
        string state = $"state-{Guid.NewGuid():N}";
        string nonce = $"nonce-{Guid.NewGuid():N}";
        string code = $"code-{Guid.NewGuid():N}";
        string oid = $"oid-{Guid.NewGuid():N}";

        var pending = fixture.Factory.Services.GetRequiredService<AzureSignInRequests>();
        pending.Add(new AzurePendingSignIn(state, "unused-verifier", nonce, admin, DateTime.UtcNow.AddMinutes(10)));

        var exchanger = (FakeOidcTokenExchanger)fixture.Factory.Services.GetRequiredService<IOidcTokenExchanger>();
        exchanger.SetToken(code, BuildIdToken(nonce, oid, SharedWebAppFixture.AzureTestTenantId, "Ada Lovelace"));

        try
        {
            using var anonymous = NoRedirectClient(authenticated: false);
            var callbackResponse = await anonymous.GetAsync(
                $"/api/v1/auth/cloud/azure/callback?code={code}&state={state}");

            callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            callbackResponse.Headers.Location!.OriginalString.Should().Be("/api/v1/auth/cloud/azure/confirm");
            (await AzureLinkAsync(admin)).Should().BeNull("the callback must only park the outcome, never store it directly");

            string confirmCookie = ExtractCookieValue(callbackResponse, AzureConfirmCookieName);

            using var adminClient = NoRedirectClient();
            var confirmResponse = await ConfirmAsync(adminClient, confirmCookie);

            confirmResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            confirmResponse.Headers.Location!.OriginalString.Should().Be("/profile/integrations");

            var link = await AzureLinkAsync(admin);
            link.Should().NotBeNull();
            link!.ObjectId.Should().Be(oid);
            link.TenantId.Should().Be(SharedWebAppFixture.AzureTestTenantId);
            link.DisplayName.Should().Be("Ada Lovelace");
        }
        finally
        {
            await DeleteAzureLinkAsync(admin);
        }
    }

    [Fact]
    public async Task AzureConfirm_CompletedByADifferentUserThanStartedIt_RefusesAndStoresNothing()
    {
        // The CSRF this whole confirm hop exists to close. The admin (the "attacker") starts a
        // sign-in and — without ever following the redirect — hands the resulting authorize URL to
        // a colleague (the "victim"). The colleague signs in as themselves at Entra; PKCE binds the
        // authorization code to the verifier, not to a person, and the id_token's nonce matches
        // because it really was in the request the colleague completed. The callback parks the
        // outcome under the admin's pending entry regardless of who's browser lands on it — the
        // cookie it sets only reaches whoever's browser is completing the redirect (the victim's).
        // If the victim's own session were accepted at /confirm, the admin's Connapse account would
        // end up linked to the victim's Entra identity.
        Guid admin = AdminUserId();
        (Guid victimId, string victimToken) = await EnsureVictimUserAsync();

        string state = $"state-{Guid.NewGuid():N}";
        string nonce = $"nonce-{Guid.NewGuid():N}";
        string code = $"code-{Guid.NewGuid():N}";
        string victimOid = $"oid-{Guid.NewGuid():N}";

        var pending = fixture.Factory.Services.GetRequiredService<AzureSignInRequests>();
        // Started by the admin — this is the pending entry the admin's own /connect call would
        // have produced, captured before being followed.
        pending.Add(new AzurePendingSignIn(state, "unused-verifier", nonce, admin, DateTime.UtcNow.AddMinutes(10)));

        var exchanger = (FakeOidcTokenExchanger)fixture.Factory.Services.GetRequiredService<IOidcTokenExchanger>();
        // A genuine id_token for the colleague's own Entra identity — nothing about it is forged.
        exchanger.SetToken(code, BuildIdToken(nonce, victimOid, SharedWebAppFixture.AzureTestTenantId, "Victim Person"));

        try
        {
            using var anonymous = NoRedirectClient(authenticated: false);
            var callbackResponse = await anonymous.GetAsync(
                $"/api/v1/auth/cloud/azure/callback?code={code}&state={state}");

            callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            callbackResponse.Headers.Location!.OriginalString.Should().Be("/api/v1/auth/cloud/azure/confirm");

            string confirmCookie = ExtractCookieValue(callbackResponse, AzureConfirmCookieName);

            // The victim's own browser is what actually receives this cookie and follows the
            // redirect — so it reaches /confirm carrying the victim's session, not the admin's.
            using var victimClient = fixture.Factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            victimClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", victimToken);

            var confirmResponse = await ConfirmAsync(victimClient, confirmCookie);

            confirmResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            confirmResponse.Headers.Location!.OriginalString.Should().Contain("error=azure_link_failed");

            (await AzureLinkAsync(admin)).Should().BeNull(
                "the attacker's account must not end up linked to the victim's Entra identity");
            (await AzureLinkAsync(victimId)).Should().BeNull(
                "nothing should be stored for the victim either — they never started this sign-in");

            // Single-use: the confirmation must be burned by the refusal above, not left claimable
            // by a second attempt (e.g. the admin retrying with their own session).
            using var adminClient = NoRedirectClient();
            var secondAttempt = await ConfirmAsync(adminClient, confirmCookie);
            secondAttempt.Headers.Location!.OriginalString.Should().Contain("error=azure_link_failed");
            (await AzureLinkAsync(admin)).Should().BeNull("a burned confirmation must not become claimable by anyone afterwards");
        }
        finally
        {
            await DeleteAzureLinkAsync(admin);
            await DeleteAzureLinkAsync(victimId);
        }
    }

    [Fact]
    public async Task DisconnectAzure_WithALink_DeletesTheLinkAndReturns204()
    {
        Guid admin = AdminUserId();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<AzureIdentityLinkStore>();
            await store.SaveAsync(admin, $"oid-{Guid.NewGuid():N}", SharedWebAppFixture.AzureTestTenantId, "Ada Lovelace");
        }

        try
        {
            var response = await fixture.AdminClient.DeleteAsync("/api/v1/auth/cloud/Azure");

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await AzureLinkAsync(admin)).Should().BeNull("the link was deleted");
        }
        finally
        {
            await DeleteAzureLinkAsync(admin);
        }
    }
}
