using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Identity.Services;
using Connapse.Integration.Tests.Authorization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// GET /api/v1/auth/links (#865): the caller's own linked accounts, so an agent can say why search hides a
/// document. Who may call it (people and their tokens, never agent keys) is the authorization matrix's.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AccountLinksEndpointTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task ListLinks_NothingLinked_ListsEveryProviderAsUnlinked()
    {
        using var viewer = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Viewer);

        var body = await GetLinksAsync(viewer);

        body.GetProperty("linkPage").GetString().Should().Be("/profile/integrations");
        var links = body.GetProperty("links").EnumerateArray().ToList();
        links.Select(l => l.GetProperty("provider").GetString()).Should().StartWith(["aws", "azure", "github", "atlassian"]);
        links.Take(4).Should().OnlyContain(l => l.GetProperty("linked").GetBoolean() == false
            && l.GetProperty("account").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task ListLinks_LinkedGitHub_ShowsTheLoginToItsOwnerOnly()
    {
        Guid admin = AdminUserId();
        string login = $"octo-{Guid.NewGuid():N}"[..20];
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<GitHubIdentityLinkStore>().SaveAsync(admin, Random.Shared.NextInt64(1, long.MaxValue), login);
        try
        {
            var mine = Provider(await GetLinksAsync(fixture.AdminClient), "github");
            mine.GetProperty("linked").GetBoolean().Should().BeTrue();
            mine.GetProperty("account").GetString().Should().Be(login);
            mine.GetProperty("linkedAt").ValueKind.Should().Be(JsonValueKind.String);

            // There is no user to name: another person reads their own links, never the admin's.
            using var viewer = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Viewer);
            var theirs = Provider(await GetLinksAsync(viewer), "github");
            theirs.GetProperty("linked").GetBoolean().Should().BeFalse();
            (await viewer.GetStringAsync("/api/v1/auth/links")).Should().NotContain(login);
        }
        finally
        {
            // Leave the shared admin as the other tests expect to find them: unlinked.
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<GitHubIdentityLinkStore>().DeleteAsync(admin);
        }
    }

    private static async Task<JsonElement> GetLinksAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/links");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Provider(JsonElement body, string provider) =>
        body.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("provider").GetString() == provider);

    /// <summary>The admin's own user id, read from the token the fixture signed in with.</summary>
    private Guid AdminUserId()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(fixture.AdminToken);
        string? sub = token.Claims.FirstOrDefault(c =>
            c.Type is "sub" or "nameid" or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        Guid.TryParse(sub, out Guid id).Should().BeTrue("the token identifies the signed-in user");
        return id;
    }
}
