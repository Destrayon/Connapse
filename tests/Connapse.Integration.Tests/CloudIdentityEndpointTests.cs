using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
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
    public async Task AzureCallback_HappyPath_StoresLinkAndRedirectsToIntegrations()
    {
        Guid admin = AdminUserId();
        string state = $"state-{Guid.NewGuid():N}";
        string nonce = $"nonce-{Guid.NewGuid():N}";
        string code = $"code-{Guid.NewGuid():N}";
        string oid = $"oid-{Guid.NewGuid():N}";

        var pending = fixture.Factory.Services.GetRequiredService<AzureSignInRequests>();
        pending.Add(new AzurePendingSignIn(state, "unused-verifier", nonce, admin, DateTime.UtcNow.AddMinutes(10)));

        var exchanger = (FakeOidcTokenExchanger)fixture.Factory.Services.GetRequiredService<IOidcTokenExchanger>();
        exchanger.SetToken(code, BuildIdToken(nonce, oid, SharedWebAppFixture.AzureTestTenantId, "Ada Lovelace"));

        using var client = NoRedirectClient(authenticated: false);
        try
        {
            var response = await client.GetAsync(
                $"/api/v1/auth/cloud/azure/callback?code={code}&state={state}");

            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            response.Headers.Location!.OriginalString.Should().Be("/profile/integrations");

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
