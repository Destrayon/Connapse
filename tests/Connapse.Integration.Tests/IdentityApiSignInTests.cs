using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-166: signing in through ASP.NET Identity's API (/api/v1/identity) without a browser cookie. Login used to
/// answer 500 because the Identity.Bearer scheme it signs in with wasn't registered.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class IdentityApiSignInTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    // A user of its own: login locks an account out after repeated wrong passwords, and the tests here send some.
    private const string Email = "signin@identity-api-test.connapse.io";
    private const string Password = "IdentityApiTest1!";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private string _userId = null!;

    private sealed record TokenPair(string TokenType, string AccessToken, long ExpiresIn, string RefreshToken);

    public async Task InitializeAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        var user = await users.FindByEmailAsync(Email);
        if (user is null)
        {
            user = new ConnapseUser { UserName = Email, Email = Email, EmailConfirmed = true, DisplayName = Email, CreatedAt = DateTime.UtcNow };
            var created = await users.CreateAsync(user, Password);
            created.Succeeded.Should().BeTrue(string.Join(", ", created.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(user, "Viewer");
        }
        await users.ResetAccessFailedCountAsync(user);
        _userId = user.Id.ToString();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<TokenPair> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/identity/login", new { email = Email, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TokenPair>(JsonOptions))!;
    }

    private static async Task ShouldBeUnauthorizedProblemAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().Should().Be(401);
    }

    [Fact]
    public async Task Login_WithoutCookies_ReturnsABearerTokenPairAndNoCookie()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/identity/login", new { email = Email, password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.Contains("Set-Cookie").Should().BeFalse("a token sign-in starts no browser session");
        var token = await response.Content.ReadFromJsonAsync<TokenPair>(JsonOptions);
        token!.TokenType.Should().Be("Bearer");
        token.AccessToken.Should().NotBeNullOrEmpty();
        token.RefreshToken.Should().NotBeNullOrEmpty();
        token.ExpiresIn.Should().BePositive();
    }

    [Fact]
    public async Task LoginToken_AuthenticatesAnApiRequest()
    {
        using var client = fixture.Factory.CreateClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var response = await client.GetAsync("/api/v1/identity/manage/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("email").GetString().Should().Be(Email);
    }

    [Fact]
    public async Task JwtAndLoginToken_BothAuthenticateTheSameRequest()
    {
        using var client = fixture.Factory.CreateClient();
        var login = await LoginAsync(client);
        var jwtResponse = await client.PostAsJsonAsync("/api/v1/auth/token", new { email = Email, password = Password });
        jwtResponse.StatusCode.Should().Be(HttpStatusCode.OK, await jwtResponse.Content.ReadAsStringAsync());
        using var jwtBody = JsonDocument.Parse(await jwtResponse.Content.ReadAsStringAsync());
        string jwt = jwtBody.RootElement.GetProperty("accessToken").GetString()!;
        jwt.Should().Contain(".", "a JWT has dots between its parts");
        login.AccessToken.Should().NotContain(".", "the login token is a data-protected ticket");

        foreach (string token in new[] { jwt, login.AccessToken })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity/manage/info");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401Problem()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/identity/login", new { email = Email, password = "WrongPassword1!" });

        await ShouldBeUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task Refresh_WithTheLoginRefreshToken_ReturnsANewUsableTokenPair()
    {
        using var client = fixture.Factory.CreateClient();
        var login = await LoginAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/identity/refresh", new { refreshToken = login.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var refreshed = await response.Content.ReadFromJsonAsync<TokenPair>(JsonOptions);
        refreshed!.AccessToken.Should().NotBeNullOrEmpty();
        refreshed.RefreshToken.Should().NotBeNullOrEmpty();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
        (await client.GetAsync("/api/v1/identity/manage/info")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_WithAnInvalidToken_Returns401Problem()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/identity/refresh", new { refreshToken = "not-a-refresh-token" });

        await ShouldBeUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task Refresh_WithAnAccessToken_Returns401Problem()
    {
        using var client = fixture.Factory.CreateClient();
        var login = await LoginAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/identity/refresh", new { refreshToken = login.AccessToken });

        await ShouldBeUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task UnknownDotlessBearerToken_Returns401Problem()
    {
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "CfDJ8NotARealTicket");

        var response = await client.GetAsync("/api/v1/identity/manage/info");

        await ShouldBeUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task ConfirmEmail_WithABadCode_Returns401Problem()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/identity/confirmEmail?userId={_userId}&code=bad");

        await ShouldBeUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task ConfirmEmail_ForAnUnknownUser_Returns401Problem()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/identity/confirmEmail?userId={Guid.NewGuid()}&code=bad");

        await ShouldBeUnauthorizedProblemAsync(response);
    }
}
