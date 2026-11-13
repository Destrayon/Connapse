using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Identity.Authentication;
using Connapse.Integration.Tests.CrossSurface;
using Connapse.Web.Mcp;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// CON-346: the default endpoint lists the core tools plus find_tools and the three call tools; an agent
/// finds every other tool by searching and runs it through the call tool for its risk, under the same
/// scopes, authorization and approval it would meet calling the tool directly.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class McpToolSearchTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task CompactList_ShowsCoreAndSearchTools_WithInstructionsNamingTheRest()
    {
        await using var mcp = await AdminTokenAsync(["knowledge:read", "knowledge:write"], requiresApproval: false, McpTestClient.Compact);

        var names = (await mcp.ListToolsAsync()).Select(t => t.Name).ToList();

        names.Should().BeEquivalentTo(McpToolList.Core.Concat(McpToolSearch.Names));
        mcp.ServerInstructions.Should().Be(McpToolList.CompactInstructions);
    }

    [Fact]
    public async Task FullList_ShowsEveryToolButTheSearchTools()
    {
        await using var mcp = await AdminTokenAsync(["knowledge:read", "knowledge:write"], requiresApproval: false, McpTestClient.Full);

        var names = (await mcp.ListToolsAsync()).Select(t => t.Name).ToList();

        names.Should().Contain(McpToolList.Core).And.Contain("container_delete").And.Contain(McpApiTools.ReadName);
        names.Should().NotContain(McpToolSearch.Names);
        mcp.ServerInstructions.Should().NotBe(McpToolList.CompactInstructions);
    }

    [Fact]
    public async Task CompactEndpoint_PatCreatedWithFullList_GetsEveryToolAndListsTheFlag()
    {
        var issued = await fixture.AdminClient.PostAsJsonAsync("/api/v1/auth/pats",
            new PatCreateRequest($"full-{Guid.NewGuid():N}", ["knowledge:read", "knowledge:write"], RequiresApproval: false, FullToolList: true));
        var pat = (await issued.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<PatCreateResponse>())!;
        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, pat.Token);
        await using var mcp = await McpTestClient.ConnectAsync(http, path: McpTestClient.Compact);

        var names = (await mcp.ListToolsAsync()).Select(t => t.Name).ToList();

        names.Should().Contain(McpToolList.Core).And.Contain("container_delete");
        names.Should().NotContain(McpToolSearch.Names);
        mcp.ServerInstructions.Should().NotBe(McpToolList.CompactInstructions);
        var listed = await fixture.AdminClient.GetFromJsonAsync<List<PatListItem>>("/api/v1/auth/pats");
        listed!.Single(p => p.Id == pat.Id).FullToolList.Should().BeTrue();
    }

    [Fact]
    public async Task CompactEndpoint_AgentKeys_OnlyTheKeyCreatedWithFullListGetsEveryTool()
    {
        var created = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents", new CreateAgentRequest($"tool-list-{Guid.NewGuid():N}"));
        var agent = (await created.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<AgentDto>())!;
        try
        {
            await using var full = await AgentKeyClientAsync(agent.Id, fullToolList: true);
            await using var compact = await AgentKeyClientAsync(agent.Id, fullToolList: false);

            var fullNames = (await full.ListToolsAsync()).Select(t => t.Name).ToList();
            var compactNames = (await compact.ListToolsAsync()).Select(t => t.Name).ToList();

            fullNames.Should().Contain("search_knowledge").And.NotContain(McpToolSearch.Names);
            compactNames.Should().Contain(McpToolSearch.FindName).And.NotContain("container_delete");
            var keys = await fixture.AdminClient.GetFromJsonAsync<List<AgentKeyListItem>>($"/api/v1/agents/{agent.Id}/keys");
            keys!.Select(k => k.FullToolList).Should().BeEquivalentTo([true, false]);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent.Id}");
        }
    }

    [Fact]
    public async Task CompactEndpoint_ServerSettingSavedAsFull_ShowsEveryToolWithoutARestart()
    {
        try
        {
            (await fixture.AdminClient.PutAsJsonAsync($"/api/settings/{McpSettings.Category}",
                new McpSettings { ToolList = McpSettings.Full })).EnsureSuccessStatusCode();
            await using var full = await AdminTokenAsync(["knowledge:read"], requiresApproval: false, McpTestClient.Compact);

            (await full.ListToolsAsync()).Select(t => t.Name).Should().NotContain(McpToolSearch.Names);
        }
        finally
        {
            (await fixture.AdminClient.DeleteAsync($"/api/v1/settings/{McpSettings.Category}")).EnsureSuccessStatusCode();
        }

        await using var compact = await AdminTokenAsync(["knowledge:read"], requiresApproval: false, McpTestClient.Compact);
        (await compact.ListToolsAsync()).Select(t => t.Name).Should().Contain(McpToolSearch.Names);
    }

    [Fact]
    public async Task FullList_EverySchemaUsesOnlyKeywordsTheCallToolsCheck()
    {
        await using var mcp = await Surfaces.McpAsync(fixture);

        foreach (var tool in await mcp.ListToolsAsync())
            Keywords(tool.JsonSchema).Should().BeSubsetOf(McpToolArguments.Keywords,
                $"call tools check {tool.Name}'s arguments against its schema and must understand every keyword in it");
    }

    [Fact]
    public async Task FindTools_EveryToolOnTheFullList_IsFoundByItsName()
    {
        await using var full = await Surfaces.McpAsync(fixture);
        var http = fixture.Factory.CreateClient();
        await using var compact = await McpTestClient.ConnectAsync(await WithAdminPatAsync(http), path: McpTestClient.Compact);

        var hidden = (await full.ListToolsAsync()).Select(t => t.Name)
            .Where(n => !McpToolList.Core.Contains(n) && !n.StartsWith("connapse_api_", StringComparison.Ordinal))
            .ToList();
        hidden.Should().Contain("container_delete");

        foreach (string name in hidden)
            Text(await compact.CallToolAsync(McpToolSearch.FindName, new Dictionary<string, object?> { ["query"] = name }))
                .Should().Contain($"## {name} — ", "an agent that knows a tool's name must be able to find it");
    }

    [Fact]
    public async Task CallRead_RestOperation_RunsItsRoute()
    {
        await using var mcp = await AdminTokenAsync(["knowledge:read", "knowledge:write"], requiresApproval: false, McpTestClient.Compact);
        string container = await CreateContainerAsync("search-rest");
        try
        {
            Text(await mcp.CallToolAsync(McpToolSearch.CallReadName, Call("containers.settings.get",
                    new { parameters = new { containerId = container } })))
                .Should().StartWith("200 OK");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task CallRead_DestructiveTool_IsRefusedAndNothingIsDeleted()
    {
        await using var mcp = await AdminTokenAsync(["knowledge:read", "knowledge:write"], requiresApproval: false, McpTestClient.Compact);
        string container = await CreateContainerAsync("search-wrong-risk");
        try
        {
            Text(await mcp.CallToolAsync(McpToolSearch.CallReadName, Call("container_delete", new { containerId = container })))
                .Should().Contain($"run it with {McpToolSearch.CallDestructiveName}");
            Text(await mcp.CallToolAsync(McpToolSearch.CallWriteName, Call("container_delete", new { containerId = container })))
                .Should().Contain($"run it with {McpToolSearch.CallDestructiveName}");

            (await fixture.AdminClient.GetAsync($"/api/containers/{container}")).IsSuccessStatusCode.Should().BeTrue();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task CallDestructive_TokenRequiringApproval_WaitsForAPerson()
    {
        await using var mcp = await AdminTokenAsync(["knowledge:read", "knowledge:write"], requiresApproval: true, McpTestClient.Compact);
        string container = await CreateContainerAsync("search-approval");
        try
        {
            Text(await mcp.CallToolAsync(McpToolSearch.CallDestructiveName, Call("container_delete", new { containerId = container })))
                .Should().Contain("pending approval");

            (await fixture.AdminClient.GetAsync($"/api/containers/{container}")).IsSuccessStatusCode.Should().BeTrue("nobody approved it");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task CallWrite_ReadOnlyAgentKey_IsRefusedAndCannotFindTheTool()
    {
        var http = fixture.Factory.CreateClient();
        (await AuthzPrincipals.CredentialAsync(fixture, AuthzPrincipal.AgentRead))(http.DefaultRequestHeaders);
        await using var mcp = await McpTestClient.ConnectAsync(http, path: McpTestClient.Compact);
        string container = await CreateContainerAsync("search-read-key");
        try
        {
            Text(await mcp.CallToolAsync(McpToolSearch.CallWriteName,
                    Call("upload_file", new { containerId = container, textContent = "x", fileName = "x.md" })))
                .Should().Contain("may not use 'upload_file'");
            Text(await mcp.CallToolAsync(McpToolSearch.FindName, new Dictionary<string, object?> { ["query"] = "upload_file" }))
                .Should().NotContain("## upload_file", "a token only finds tools it may run");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    /// <summary>Every keyword in a JSON schema, walking into properties and items.</summary>
    private static IEnumerable<string> Keywords(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            yield break;
        foreach (var keyword in schema.EnumerateObject())
        {
            yield return keyword.Name;
            if (keyword.Name == "properties" && keyword.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in keyword.Value.EnumerateObject())
                    foreach (string nested in Keywords(property.Value))
                        yield return nested;
            }
            else if (keyword.Name == "items")
            {
                foreach (string nested in Keywords(keyword.Value))
                    yield return nested;
            }
        }
    }

    private static Dictionary<string, object?> Call(string tool, object arguments) =>
        new() { ["tool"] = tool, ["arguments"] = arguments };

    private async Task<McpClient> AdminTokenAsync(string[] scopes, bool requiresApproval, string path)
    {
        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, await PatAsync(scopes, requiresApproval));
        return await McpTestClient.ConnectAsync(http, path: path);
    }

    private async Task<McpClient> AgentKeyClientAsync(Guid agentId, bool fullToolList)
    {
        var issued = await fixture.AdminClient.PostAsJsonAsync($"/api/v1/agents/{agentId}/keys",
            new CreateAgentKeyRequest($"key-{Guid.NewGuid():N}", ["knowledge:read"], RequiresApproval: false, FullToolList: fullToolList));
        var key = (await issued.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CreateAgentKeyResponse>())!;
        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, key.Token);
        return await McpTestClient.ConnectAsync(http, path: McpTestClient.Compact);
    }

    private async Task<HttpClient> WithAdminPatAsync(HttpClient http)
    {
        http.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName,
            await PatAsync(["knowledge:read", "knowledge:write", "admin:users"], requiresApproval: false));
        return http;
    }

    private async Task<string> PatAsync(string[] scopes, bool requiresApproval)
    {
        var issued = await fixture.AdminClient.PostAsJsonAsync("/api/v1/auth/pats",
            new PatCreateRequest($"search-{Guid.NewGuid():N}", scopes, RequiresApproval: requiresApproval));
        return (await issued.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<PatCreateResponse>())!.Token;
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
