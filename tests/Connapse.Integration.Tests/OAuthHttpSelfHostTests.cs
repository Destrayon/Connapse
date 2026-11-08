using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// A server reached over plain http on a home network (CON-280): apps signing in with a loopback callback
/// register, and the server's own CLI signs in with its client_id under the server's http address.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class OAuthHttpSelfHostTests(SharedWebAppFixture fixture)
{
    private const string HomeServer = "http://divielserver.lan:8080";
    private const string CliRedirect = "http://127.0.0.1:49152/callback";

    [Theory]
    [InlineData("http://localhost:53682/callback")]
    [InlineData("http://127.0.0.1:8765/callback")]
    [InlineData("http://[::1]:8765/callback")]
    public async Task Register_NoApplicationTypeLoopbackRedirect_RegistersNativeClient(string redirect)
    {
        using var client = HomeServerClient();

        var response = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = $"home-{Guid.NewGuid():N}",
            redirect_uris = new[] { redirect },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("application_type").GetString().Should().Be("native");
    }

    [Fact]
    public async Task Register_NoApplicationTypeLanHttpRedirect_Returns400InvalidClientMetadata()
    {
        using var client = HomeServerClient();

        var response = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "lan",
            redirect_uris = new[] { "http://divielserver.lan:9000/callback" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Be("invalid_client_metadata");
    }

    [Fact]
    public async Task Token_ThisServersCliClientIdOverHttp_IssuesAndRefreshes()
    {
        using var client = HomeServerClient();
        string cliClientId = (await client.GetFromJsonAsync<JsonElement>("/oauth/clients/cli.json"))
            .GetProperty("client_id").GetString()!;
        cliClientId.Should().Be(HomeServer + OAuthClientService.FirstPartyCliPath);

        using var granted = await ExchangeCodeAsync(client, cliClientId);
        granted.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await granted.Content.ReadFromJsonAsync<JsonElement>();
        new JwtSecurityTokenHandler().ReadJwtToken(tokens.GetProperty("access_token").GetString())
            .Claims.Should().Contain(c => c.Type == "client_id" && c.Value == cliClientId);

        using var refreshed = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["client_id"] = cliClientId,
        }));
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Token_AnotherServersHttpCliClientId_IsRefused()
    {
        using var client = HomeServerClient();

        using var response = await ExchangeCodeAsync(client, "http://evil.lan" + OAuthClientService.FirstPartyCliPath);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    private HttpClient HomeServerClient() =>
        fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(HomeServer) });

    private async Task<HttpResponseMessage> ExchangeCodeAsync(HttpClient client, string clientId)
    {
        string verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string code;
        await using (var services = fixture.Factory.Services.CreateAsyncScope())
            code = await services.ServiceProvider.GetRequiredService<OAuthAuthCodeService>()
                .CreateAsync(AdminUserId(), clientId, challenge, CliRedirect, "knowledge:read");
        return await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = CliRedirect,
            ["client_id"] = clientId,
        }));
    }

    private Guid AdminUserId()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(fixture.AdminToken);
        string? sub = token.Claims.FirstOrDefault(c =>
            c.Type is "sub" or "nameid" or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        Guid.TryParse(sub, out Guid id).Should().BeTrue();
        return id;
    }
}
