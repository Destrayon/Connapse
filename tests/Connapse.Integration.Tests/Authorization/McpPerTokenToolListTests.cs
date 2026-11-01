using System.ComponentModel;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// #846: the 2026-07-28 spec has no sessions and lets clients cache tools/list, yet each token must
/// still see only the tools it may call. Every tools/list is its own authenticated request, so the
/// authorization filters trim it for that request's caller; and the answer is marked
/// cacheScope "private", so no shared cache hands one token's list to another.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class McpPerTokenToolListTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task ToolsList_Stateless_IsTrimmedPerTokenAndCachedOnlyForThatCaller()
    {
        // No Connapse tool restricts by scope yet (#782), so a tool only administrators may see stands in.
        await using var host = fixture.Factory.WithWebHostBuilder(builder =>
        {
            // A second Hangfire server in one process corrupts the parent's (see McpDiscoveryTests).
            builder.UseSetting("Hangfire:DisableServer", "true");
            builder.ConfigureServices(services => services.AddMcpServer().WithTools<AdminOnlyProbeTool>());
        });

        await using var admin = await ConnectAsync(host, headers =>
            headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.AdminToken));
        await using var agent = await ConnectAsync(host, await AuthzPrincipals.CredentialAsync(fixture, AuthzPrincipal.AgentRead));

        var adminList = await admin.ListToolsAsync(new ListToolsRequestParams());
        var agentList = await agent.ListToolsAsync(new ListToolsRequestParams());

        admin.NegotiatedProtocolVersion.Should().Be("2026-07-28", "the stateless spec is the one under test");
        adminList.Tools.Select(t => t.Name).Should().Contain(AdminOnlyProbeTool.Name);
        agentList.Tools.Select(t => t.Name).Should().NotContain(AdminOnlyProbeTool.Name, "a knowledge:read agent key may not call it")
            .And.Contain("container_list");
        adminList.CacheScope.Should().Be(CacheScope.Private, "a list trimmed for one token must never be served from a shared cache");
        agentList.CacheScope.Should().Be(CacheScope.Private);
    }

    private static async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> host, Action<HttpRequestHeaders> credential)
    {
        var http = host.CreateClient();
        credential(http.DefaultRequestHeaders);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }
}

/// <summary>A tool only administrators may see, registered only in this test's host.</summary>
[McpServerToolType]
public sealed class AdminOnlyProbeTool
{
    public const string Name = "authz_probe_admin_only";

    [McpServerTool(Name = Name), Description("Test-only: visible to administrators."), Authorize(Policy = "RequireAdmin")]
    public static string Probe() => "probe";
}
