using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public partial class LinkingTests
{
    private const string Carol = "connapse-carol";
    private const string ReturnUrl = "https://connapse.test/integrations/custom/return";

    [Fact]
    public async Task Link_FullFlow_LinksOnlyAfterConfirmation()
    {
        using ExampleServer server = new();
        HttpClient browser = server.Client(authorized: false);

        string code = await StartAndComplete(browser, LinkToken(Carol), "bob");
        (await Status(server, Carol)).GetProperty("linked").GetBoolean().Should().BeFalse();

        HttpResponseMessage confirm = await server.Client().PostAsJsonAsync("/v1/links/confirm", new { code, connapse_user_id = Carol });

        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement status = await Status(server, Carol);
        status.GetProperty("linked").GetBoolean().Should().BeTrue();
        status.GetProperty("display_name").GetString().Should().Be("bob");
        JsonElement keys = await PermissionTests.PostJson(server, "/access/v1/search/resource", PermissionTests.KeySearch(Carol, "eng"));
        keys.GetProperty("results").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Link_ConfirmedForADifferentConnapseUser_Returns409AndLinksNobody()
    {
        using ExampleServer server = new();
        // The attacker (Carol) starts a link and forwards the page; the victim finishes it in their own
        // browser, and Connapse confirms with the victim's signed-in user.
        string code = await StartAndComplete(server.Client(authorized: false), LinkToken(Carol), "bob");

        HttpResponseMessage confirm = await server.Client().PostAsJsonAsync("/v1/links/confirm", new { code, connapse_user_id = "connapse-victim" });
        HttpResponseMessage retry = await server.Client().PostAsJsonAsync("/v1/links/confirm", new { code, connapse_user_id = Carol });

        confirm.StatusCode.Should().Be(HttpStatusCode.Conflict);
        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Status(server, Carol)).GetProperty("linked").GetBoolean().Should().BeFalse();
        (await Status(server, "connapse-victim")).GetProperty("linked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Link_CodeIsSingleUse()
    {
        using ExampleServer server = new();
        string code = await StartAndComplete(server.Client(authorized: false), LinkToken(Carol), "bob");

        (await server.Client().PostAsJsonAsync("/v1/links/confirm", new { code, connapse_user_id = Carol }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await server.Client().PostAsJsonAsync("/v1/links/confirm", new { code, connapse_user_id = Carol }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Link_ReplayedToken_Returns400()
    {
        using ExampleServer server = new();
        string token = LinkToken(Carol);

        (await Start(server.Client(authorized: false), token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Start(server.Client(authorized: false), token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("wrong-secret-0123456789abcdef0123456789", ExampleServer.Issuer, ExampleServer.ProviderId)]
    [InlineData(ExampleServer.LinkSecret, "https://someone-else.test", ExampleServer.ProviderId)]
    [InlineData(ExampleServer.LinkSecret, ExampleServer.Issuer, "provider-2")]
    public async Task Link_TokenWithWrongSecretIssuerOrAudience_Returns400(string secret, string issuer, string audience)
    {
        using ExampleServer server = new();

        HttpResponseMessage response = await Start(server.Client(authorized: false), LinkToken(Carol, secret, issuer, audience));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Link_ExpiredToken_Returns400()
    {
        using ExampleServer server = new();

        HttpResponseMessage response = await Start(server.Client(authorized: false), LinkToken(Carol, expiresIn: TimeSpan.FromMinutes(-5)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Link_CompletedInADifferentBrowser_Returns400()
    {
        using ExampleServer server = new();
        HttpResponseMessage started = await Start(server.Client(authorized: false), LinkToken(Carol));
        string state = StateField().Match(await started.Content.ReadAsStringAsync()).Groups[1].Value;

        HttpResponseMessage response = await server.Client(authorized: false).PostAsync("/link/complete",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = state, ["user"] = "bob" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unlink_RemovesTheLink_AndIsIdempotent()
    {
        using ExampleServer server = new();
        HttpClient api = server.Client();

        (await api.DeleteAsync($"/v1/links/{ExampleServer.AliceId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.DeleteAsync($"/v1/links/{ExampleServer.AliceId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Status(server, ExampleServer.AliceId)).GetProperty("linked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task LinkApi_WithoutToken_Returns401()
    {
        using ExampleServer server = new();

        HttpResponseMessage response = await server.Client(authorized: false).GetAsync($"/v1/links/{ExampleServer.AliceId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static string LinkToken(
        string connapseUserId,
        string secret = ExampleServer.LinkSecret,
        string issuer = ExampleServer.Issuer,
        string audience = ExampleServer.ProviderId,
        TimeSpan? expiresIn = null)
    {
        DateTime now = DateTime.UtcNow;
        DateTime expires = now + (expiresIn ?? TimeSpan.FromMinutes(5));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = expires < now ? expires.AddMinutes(-1) : now,
            IssuedAt = expires < now ? expires.AddMinutes(-1) : now,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", connapseUserId),
                new Claim("jti", Guid.NewGuid().ToString("N")),
                new Claim("return_url", ReturnUrl),
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256),
        });
    }

    private static Task<HttpResponseMessage> Start(HttpClient browser, string token) =>
        browser.PostAsync("/link", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));

    /// <returns>The one-time code from the redirect back to Connapse.</returns>
    private static async Task<string> StartAndComplete(HttpClient browser, string token, string user)
    {
        HttpResponseMessage started = await Start(browser, token);
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        started.Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
        string state = StateField().Match(await started.Content.ReadAsStringAsync()).Groups[1].Value;

        HttpResponseMessage completed = await browser.PostAsync("/link/complete",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = state, ["user"] = user }));

        completed.StatusCode.Should().Be(HttpStatusCode.Redirect);
        Uri location = completed.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).Should().Be(ReturnUrl);
        return Uri.UnescapeDataString(location.Query.Split("code=")[1]);
    }

    private static async Task<JsonElement> Status(ExampleServer server, string connapseUserId) =>
        await DiscoveryTests.GetJson(server, $"/v1/links/{connapseUserId}");

    [GeneratedRegex("name=\"state\" value=\"([^\"]+)\"")]
    private static partial Regex StateField();
}
