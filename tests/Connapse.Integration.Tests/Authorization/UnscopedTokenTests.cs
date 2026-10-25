using System.Net;
using System.Net.Http.Json;
using Connapse.Core;
using Connapse.Identity.Authentication;
using FluentAssertions;
using ModelContextProtocol.Client;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// A token issued with no scopes carries no scope claim, which every scope check used to read as a sign-in
/// (#932). Its owner is the administrator, so only the scope check stands between it and each route.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class UnscopedTokenTests(SharedWebAppFixture fixture)
{
    private async Task<HttpClient> UnscopedPatClientAsync()
    {
        using var created = await fixture.AdminClient.PostAsJsonAsync("/api/v1/auth/pats",
            new PatCreateRequest($"unscoped-{Guid.NewGuid():N}", [], RequiresApproval: false));
        created.EnsureSuccessStatusCode();
        var pat = (await created.Content.ReadFromJsonAsync<PatCreateResponse>())!;
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, pat.Token);
        return client;
    }

    [Theory]
    [InlineData("/api/v1/audit/?take=1")]
    [InlineData("/api/v1/oauth-clients/")]
    [InlineData("/api/v1/auth/links")]
    public async Task ScopeCheckedRoute_AdministratorsTokenWithNoScopes_IsForbidden(string route)
    {
        using var client = await UnscopedPatClientAsync();

        using var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task McpTools_AdministratorsTokenWithNoScopes_ListsNoScopedTool()
    {
        using var http = await UnscopedPatClientAsync();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http);
        await using var mcp = await McpClient.CreateAsync(transport);

        var tools = await mcp.ListToolsAsync();

        tools.Select(t => t.Name).Should().NotContain(["search_knowledge", "container_list", "container_delete"],
            "each needs a knowledge scope the token wasn't issued with");
    }
}
