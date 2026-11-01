using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Approvals;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Authentication;
using Connapse.Integration.Tests.CrossSurface;
using Connapse.Web.Mcp;
using Connapse.Web.Services.GenericApi;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Security.Claims;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// CON-20: the generic connapse_api_* tools reach every operation marked for MCP through its REST
/// route, and only those; a call needs the operation's scope on the token and passes the route's own
/// authorization; a destructive call from a token that requires approval waits for a person.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class GenericApiToolsTests(SharedWebAppFixture fixture)
{
    [Fact]
    public void Catalog_EveryOperationMarkedForMcp_IsReachable()
    {
        var generic = typeof(OperationAttribute).Assembly.GetTypes()
            .Select(t => t.GetCustomAttribute<OperationAttribute>())
            .OfType<OperationAttribute>()
            .Where(o => o.Mcp == McpExposure.Generic)
            .Select(o => o.Id);

        fixture.Factory.Services.GetRequiredService<ApiCatalog>().ById.Keys
            .Should().BeEquivalentTo(generic, "every operation marked for MCP is served by the route the spec names it on, and nothing else is");
    }

    [Fact]
    public async Task ApiRead_OperationMarkedForMcp_RunsItsRoute()
    {
        await using var mcp = await Surfaces.McpAsync(fixture);
        string container = await CreateContainerAsync("generic-read");
        try
        {
            string read = Text(await mcp.CallToolAsync(McpApiTools.ReadName, Call("containers.settings.get", new() { ["containerId"] = container })));

            read.Should().StartWith("200 OK");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task ApiCalls_OperationNotMarkedForMcp_AreRefused()
    {
        await using var mcp = await Surfaces.McpAsync(fixture);
        string container = await CreateContainerAsync("generic-off");
        try
        {
            Text(await mcp.CallToolAsync(McpApiTools.WriteName, Call("containers.delete", new() { ["containerId"] = container })))
                .Should().Contain("isn't an operation MCP may call");
            Text(await mcp.CallToolAsync(McpApiTools.ReadName, Call("folders.delete", new() { ["containerId"] = container, ["path"] = "/x/" })))
                .Should().Contain("connapse_api_write", "a write isn't run by the read tool");
            (await fixture.AdminClient.GetAsync($"/api/containers/{container}")).IsSuccessStatusCode.Should().BeTrue();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task ApiTools_ReadOnlyAgentKey_SeesAndCallsOnlyItsScopes()
    {
        await using var agent = await ConnectAsync(await AuthzPrincipals.CredentialAsync(fixture, AuthzPrincipal.AgentRead));

        var tools = (await agent.ListToolsAsync()).Select(t => t.Name).ToList();
        tools.Should().Contain([McpApiTools.SearchName, McpApiTools.ReadName]);
        tools.Should().NotContain(McpApiTools.WriteName, "a knowledge:read key may call no operation that writes");

        string search = Text(await agent.CallToolAsync(McpApiTools.SearchName, new Dictionary<string, object?> { ["query"] = "", ["limit"] = 50 }));
        search.Should().Contain("containers.settings.get").And.NotContain("agents.list", "the key lacks admin:users");

        Text(await agent.CallToolAsync(McpApiTools.ReadName, Call("agents.list", [])))
            .Should().Contain("needs the admin:users scope");
    }

    [Fact]
    public async Task ApiRead_AgentKeyHoldingAdminScope_IsRefusedByTheRoute()
    {
        // An agent key issued before #955 can hold admin:users, but the route admits only administrators.
        var principal = AuthzPrincipal.AgentAdmins.Single(p => p.Scopes!.Contains("admin:users"));
        await using var agent = await ConnectAsync(await AuthzPrincipals.CredentialAsync(fixture, principal));

        Text(await agent.CallToolAsync(McpApiTools.ReadName, Call("agents.list", []))).Should().StartWith("403");
    }

    [Fact]
    public async Task ApiWrite_DestructiveOperation_WaitsForApprovalThenRunsOnce()
    {
        await using var agent = await ApprovalTokenAsync();
        string container = await CreateContainerAsync("generic-approval");
        try
        {
            Text(await agent.CallToolAsync(McpApiTools.WriteName, Call("folders.create", new() { ["containerId"] = container }, new { path = "/kept/" })))
                .Should().StartWith("201", "a write that isn't destructive runs without approval");

            var delete = Call("folders.delete", new() { ["containerId"] = container, ["path"] = "/kept/", ["cascade"] = true });
            Text(await agent.CallToolAsync(McpApiTools.WriteName, delete)).Should().Contain("pending approval").And.Contain("folders.delete");

            McpApprovalItem pending;
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var listed = await scope.ServiceProvider.GetRequiredService<ListMcpApprovals>().ExecuteAsync(Admin());
                pending = listed.Result!.Should().ContainSingle(a => a.ToolName == McpApiTools.WriteName && a.ArgumentsSummary.Contains(container)).Subject;
                (await scope.ServiceProvider.GetRequiredService<DecideMcpApproval>().ExecuteAsync(Admin(), pending.Id, approve: true))
                    .Succeeded.Should().BeTrue();
            }

            string ran = Text(await agent.CallToolAsync(McpApiTools.WriteName, delete));
            ran.Should().StartWith("2", "the approved call ran");
            Text(await agent.CallToolAsync(McpApiTools.WriteName, delete)).Should().Contain("pending approval", "an approval runs one call");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task ApiWrite_CredentialOperation_WaitsForApproval()
    {
        await using var agent = await AdminTokenAsync(["admin:settings"], requiresApproval: true);
        var original = (await fixture.AdminClient.GetFromJsonAsync<ChunkingSettings>("/api/settings/chunking"))!;
        try
        {
            Text(await agent.CallToolAsync(McpApiTools.ReadName, Call("settings.get", new() { ["category"] = "chunking" })))
                .Should().StartWith("200", "a read runs without approval");

            Text(await agent.CallToolAsync(McpApiTools.WriteName,
                    Call("settings.update", new() { ["category"] = "chunking" }, original with { Overlap = original.Overlap + 5 })))
                .Should().Contain("pending approval").And.Contain("settings.update", "settings.update saves API keys, so it is a credential change");

            (await fixture.AdminClient.GetFromJsonAsync<ChunkingSettings>("/api/settings/chunking"))!.Overlap
                .Should().Be(original.Overlap, "nothing ran before a person approved it");
        }
        finally
        {
            await fixture.AdminClient.PutAsJsonAsync("/api/settings/chunking", original);
        }
    }

    [Fact]
    public async Task ApiWrite_ConnectionWithAKey_ApproverNeverSeesTheKeyAndTheApprovedCallStoresIt()
    {
        await using var agent = await AdminTokenAsync(["admin:sources"], requiresApproval: true);
        string name = $"sealed-{Guid.NewGuid():N}";
        string key = $"private-key-{Guid.NewGuid():N}";
        object Body(string privateKey) => new
        {
            name,
            provider = "Sftp",
            config = new { host = "sftp.example.com", port = 2222, username = "reader", allowedRoot = "/data" },
            privateKey,
        };
        var create = Call("connections.create", [], Body(key));
        Guid? created = null;
        try
        {
            Text(await agent.CallToolAsync(McpApiTools.WriteName, create)).Should().Contain("pending approval").And.NotContain(key);

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var listed = await scope.ServiceProvider.GetRequiredService<ListMcpApprovals>().ExecuteAsync(Admin());
                var pending = listed.Result!.Should().ContainSingle(a => a.ArgumentsSummary.Contains(name)).Subject;
                pending.ArgumentsSummary.Should().NotContain(key, "the approvals page shows secret fields hidden")
                    .And.Contain(McpCallArguments.Hidden).And.Contain("sftp.example.com", "everything else stays readable");
                (await scope.ServiceProvider.GetRequiredService<DecideMcpApproval>().ExecuteAsync(Admin(), pending.Id, approve: true))
                    .Succeeded.Should().BeTrue();
            }

            Text(await agent.CallToolAsync(McpApiTools.WriteName, Call("connections.create", [], Body("a-different-key"))))
                .Should().Contain("pending approval", "the approval is for the key that was asked for, though the page hid it");

            string ran = Text(await agent.CallToolAsync(McpApiTools.WriteName, create));
            ran.Should().StartWith("201", "the approved call ran").And.NotContain(key);
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
                created = (await connections.ListAsync(take: 1000)).Single(c => c.Name == name).Id;
                (await connections.GetSecretAsync(created.Value)).Should().Contain(key, "the call ran with the real key");
            }
        }
        finally
        {
            if (created is { } id)
                await fixture.AdminClient.DeleteAsync($"/api/v1/connections/{id}");
        }
    }

    [Fact]
    public async Task ApiWrite_SettingsSaveWithAStaleETag_Returns412AndKeepsTheOtherChange()
    {
        await using var mcp = await AdminTokenAsync(["admin:settings"], requiresApproval: false);
        var original = (await fixture.AdminClient.GetFromJsonAsync<ChunkingSettings>("/api/settings/chunking"))!;
        try
        {
            string read = Text(await mcp.CallToolAsync(McpApiTools.ReadName, Call("settings.get", new() { ["category"] = "chunking" })));
            read.Should().StartWith("200 OK").And.Contain("\nETag: ", "a save needs the read's ETag to be conditional");
            string etag = read.Split('\n').Single(l => l.StartsWith("ETag: ", StringComparison.Ordinal))["ETag: ".Length..];

            // Another administrator saves in between.
            (await fixture.AdminClient.PutAsJsonAsync("/api/settings/chunking", original with { MaxChunkSize = original.MaxChunkSize + 11 }))
                .EnsureSuccessStatusCode();

            Text(await mcp.CallToolAsync(McpApiTools.WriteName,
                    Call("settings.update", new() { ["category"] = "chunking", ["If-Match"] = etag }, original with { Overlap = original.Overlap + 3 })))
                .Should().StartWith("412", "the ETag the read returned goes back as If-Match");

            var after = (await fixture.AdminClient.GetFromJsonAsync<ChunkingSettings>("/api/settings/chunking"))!;
            after.MaxChunkSize.Should().Be(original.MaxChunkSize + 11);
            after.Overlap.Should().Be(original.Overlap);
        }
        finally
        {
            await fixture.AdminClient.PutAsJsonAsync("/api/settings/chunking", original);
        }
    }

    private static Dictionary<string, object?> Call(string operation, Dictionary<string, object?> parameters, object? body = null)
    {
        var arguments = new Dictionary<string, object?> { ["operation"] = operation, ["parameters"] = parameters };
        if (body is not null)
            arguments["body"] = body;
        return arguments;
    }

    private static OperationContext Admin() => new(
        new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin")], "Identity.Application")),
        OperationSurface.Ui);

    /// <summary>The administrator's own token, issued requiring approval: the folder routes admit editors, which an agent isn't.</summary>
    private Task<McpClient> ApprovalTokenAsync() => AdminTokenAsync(["knowledge:read", "knowledge:write"], requiresApproval: true);

    private async Task<McpClient> AdminTokenAsync(string[] scopes, bool requiresApproval)
    {
        var issued = await fixture.AdminClient.PostAsJsonAsync("/api/v1/auth/pats",
            new PatCreateRequest($"generic-{Guid.NewGuid():N}", scopes, RequiresApproval: requiresApproval));
        var pat = (await issued.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<PatCreateResponse>())!;
        return await ConnectAsync(headers => headers.Add(ApiKeyAuthenticationOptions.HeaderName, pat.Token));
    }

    private async Task<McpClient> ConnectAsync(Action<HttpRequestHeaders> credential)
    {
        var http = fixture.Factory.CreateClient();
        credential(http.DefaultRequestHeaders);
        return await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true));
    }

    private async Task<string> CreateContainerAsync(string prefix)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { name = $"{prefix}-{Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}
