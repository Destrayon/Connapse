using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for the agent management API and agent API key authentication.
/// Routes under: /api/v1/agents/
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AgentIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// MCP protocol initialize request — used to verify auth on the MCP endpoint.
    /// The SDK speaks MCP Streamable HTTP, so we send a standard initialize message.
    /// </summary>
    private static readonly object McpInitializeRequest = new
    {
        jsonrpc = "2.0",
        method = "initialize",
        @params = new
        {
            protocolVersion = "2025-11-05",
            capabilities = new { },
            clientInfo = new { name = "integration-test", version = "1.0.0" }
        },
        id = "1"
    };

    // ── POST /api/v1/agents ──────────────────────────────────────────────

    [Fact]
    public async Task CreateAgent_ValidRequest_Returns201WithAgentDto()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest("test-agent-create", "A test agent"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var agent = await response.Content.ReadFromJsonAsync<AgentDto>(JsonOptions);
        agent.Should().NotBeNull();
        agent!.Name.Should().Be("test-agent-create");
        agent.Description.Should().Be("A test agent");
        agent.IsActive.Should().BeTrue();
        agent.Keys.Should().BeEmpty();

        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent.Id}");
    }

    [Fact]
    public async Task CreateAgent_DuplicateName_Returns409Conflict()
    {
        var first = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest("duplicate-name-agent"));
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var agent = await first.Content.ReadFromJsonAsync<AgentDto>(JsonOptions);

        var second = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest("duplicate-name-agent"));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent!.Id}");
    }

    [Fact]
    public async Task CreateAgent_RequiresAdmin_UnauthenticatedReturns401()
    {
        using var anonClient = fixture.Factory.CreateClient();
        var response = await anonClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest("anon-agent"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /api/v1/agents ───────────────────────────────────────────────

    [Fact]
    public async Task ListAgents_AdminUser_ReturnsAllActiveAgents()
    {
        var created = await CreateAgentAsync("list-test-agent");

        var response = await fixture.AdminClient.GetAsync("/api/v1/agents");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var agents = await response.Content.ReadFromJsonAsync<List<AgentDto>>(JsonOptions);
        agents.Should().NotBeNull();
        agents!.Should().Contain(a => a.Name == "list-test-agent");

        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{created.Id}");
    }

    // ── GET /api/v1/agents/{id} ──────────────────────────────────────────

    [Fact]
    public async Task GetAgent_ExistingId_ReturnsAgentWithKeys()
    {
        var created = await CreateAgentAsync("get-test-agent");
        await fixture.AdminClient.PostAsJsonAsync($"/api/v1/agents/{created.Id}/keys",
            new CreateAgentKeyRequest("key1", ["knowledge:read"]));

        var response = await fixture.AdminClient.GetAsync($"/api/v1/agents/{created.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var agent = await response.Content.ReadFromJsonAsync<AgentDto>(JsonOptions);
        agent.Should().NotBeNull();
        agent!.Keys.Should().HaveCount(1);
        agent.Keys[0].Name.Should().Be("key1");

        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{created.Id}");
    }

    [Fact]
    public async Task GetAgent_NonExistentId_Returns404()
    {
        var response = await fixture.AdminClient.GetAsync($"/api/v1/agents/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /api/v1/agents/{id}/keys ───────────────────────────────────

    [Fact]
    public async Task CreateAgentKey_ValidRequest_ReturnsRawTokenOnce()
    {
        var agent = await CreateAgentAsync("key-creation-agent");

        var keyResponse = await fixture.AdminClient.PostAsJsonAsync(
            $"/api/v1/agents/{agent.Id}/keys",
            new CreateAgentKeyRequest("my-key", ["knowledge:read", "agent:ingest"]));

        keyResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var key = await keyResponse.Content.ReadFromJsonAsync<CreateAgentKeyResponse>(JsonOptions);
        key.Should().NotBeNull();
        key!.Token.Should().StartWith("cnp_");
        key.Scopes.Should().Contain("knowledge:read");
        key.Scopes.Should().Contain("agent:ingest");

        // Token is not returned again in subsequent GET
        var agent2 = await fixture.AdminClient.GetAsync($"/api/v1/agents/{agent.Id}");
        var agentDto = await agent2.Content.ReadFromJsonAsync<AgentDto>(JsonOptions);
        agentDto!.Keys[0].TokenPrefix.Should().Be(key.Token[..12]);

        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent.Id}");
    }

    [Fact]
    public async Task CreateAgentKey_NonExistentAgent_Returns404()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync(
            $"/api/v1/agents/{Guid.NewGuid()}/keys",
            new CreateAgentKeyRequest("orphan-key"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Agent API key authentication ──────────────────────────────────────

    [Fact]
    public async Task AuthenticateWithAgentKey_ValidKey_AccessesMcpEndpoint_Returns200()
    {
        var agent = await CreateAgentAsync("mcp-auth-agent");
        var key = await CreateAgentKeyAsync(agent.Id, "mcp-key", ["knowledge:read", "agent:ingest"]);

        using var agentClient = fixture.Factory.CreateClient();
        agentClient.DefaultRequestHeaders.Add("X-Api-Key", key.Token);

        var response = await PostMcpAsync(agentClient, McpInitializeRequest);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent.Id}");
    }

    [Fact]
    public async Task AuthenticateWithAgentKey_RevokedKey_Returns401()
    {
        var agent = await CreateAgentAsync("revoke-auth-agent");
        var key = await CreateAgentKeyAsync(agent.Id, "to-be-revoked");

        // Revoke it
        var agentDto = await fixture.AdminClient.GetAsync($"/api/v1/agents/{agent.Id}");
        var agentData = await agentDto.Content.ReadFromJsonAsync<AgentDto>(JsonOptions);
        var keyId = agentData!.Keys[0].Id;
        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent.Id}/keys/{keyId}");

        // Try to use it
        using var agentClient = fixture.Factory.CreateClient();
        agentClient.DefaultRequestHeaders.Add("X-Api-Key", key.Token);

        var response = await PostMcpAsync(agentClient, McpInitializeRequest);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent.Id}");
    }

    [Fact]
    public async Task AuthenticateWithAgentKey_DisabledAgent_Returns401()
    {
        var agent = await CreateAgentAsync("disabled-auth-agent");
        var key = await CreateAgentKeyAsync(agent.Id, "disabled-key");

        // Disable the agent
        await fixture.AdminClient.PutAsJsonAsync(
            $"/api/v1/agents/{agent.Id}/active",
            new SetAgentActiveRequest(false));

        using var agentClient = fixture.Factory.CreateClient();
        agentClient.DefaultRequestHeaders.Add("X-Api-Key", key.Token);

        var response = await PostMcpAsync(agentClient, McpInitializeRequest);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent.Id}");
    }

    [Fact]
    public async Task AuthenticateWithAgentKey_DeletedAgent_Returns401()
    {
        var agent = await CreateAgentAsync("deleted-auth-agent");
        var key = await CreateAgentKeyAsync(agent.Id, "deleted-key");

        // Delete the agent
        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent.Id}");

        using var agentClient = fixture.Factory.CreateClient();
        agentClient.DefaultRequestHeaders.Add("X-Api-Key", key.Token);

        var response = await PostMcpAsync(agentClient, McpInitializeRequest);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AuthenticateWithAgentKey_AccessesAdminEndpoint_Returns403()
    {
        var agent = await CreateAgentAsync("admin-access-agent");
        var key = await CreateAgentKeyAsync(agent.Id, "admin-key", ["knowledge:read"]);

        using var agentClient = fixture.Factory.CreateClient();
        agentClient.DefaultRequestHeaders.Add("X-Api-Key", key.Token);

        // Agents should not be able to access admin-only endpoints
        var response = await agentClient.GetAsync("/api/v1/agents");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent.Id}");
    }

    // ── DELETE /api/v1/agents/{id} ───────────────────────────────────────

    [Fact]
    public async Task DeleteAgent_WithKeys_RevokesAllKeysAndSoftDeletes()
    {
        var agent = await CreateAgentAsync("delete-keys-agent");
        var key = await CreateAgentKeyAsync(agent.Id, "key-to-revoke");

        var deleteResponse = await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Agent no longer accessible
        var getResponse = await fixture.AdminClient.GetAsync($"/api/v1/agents/{agent.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Key no longer works
        using var agentClient = fixture.Factory.CreateClient();
        agentClient.DefaultRequestHeaders.Add("X-Api-Key", key.Token);
        var mcpResponse = await PostMcpAsync(agentClient, McpInitializeRequest);
        mcpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteAgent_NonExistentId_Returns404()
    {
        var response = await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PUT /api/v1/agents/{id}/active ───────────────────────────────────

    [Fact]
    public async Task SetAgentActive_DisableThenReenableAgent_RestoresAuth()
    {
        var agent = await CreateAgentAsync("toggle-agent");
        var key = await CreateAgentKeyAsync(agent.Id, "toggle-key");

        // Disable
        await fixture.AdminClient.PutAsJsonAsync(
            $"/api/v1/agents/{agent.Id}/active",
            new SetAgentActiveRequest(false));

        using var agentClient = fixture.Factory.CreateClient();
        agentClient.DefaultRequestHeaders.Add("X-Api-Key", key.Token);

        var disabled = await PostMcpAsync(agentClient, McpInitializeRequest);
        disabled.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Re-enable
        await fixture.AdminClient.PutAsJsonAsync(
            $"/api/v1/agents/{agent.Id}/active",
            new SetAgentActiveRequest(true));

        var enabled = await PostMcpAsync(agentClient, McpInitializeRequest);
        enabled.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent.Id}");
    }

    // ── DELETE /api/v1/agents/{id}/keys/{keyId} ──────────────────────────

    [Fact]
    public async Task RevokeAgentKey_WrongAgent_Returns404()
    {
        var agent1 = await CreateAgentAsync("revoke-wrong-agent-1");
        var agent2 = await CreateAgentAsync("revoke-wrong-agent-2");
        await CreateAgentKeyAsync(agent1.Id, "agent1-key");

        var agent1Data = await (await fixture.AdminClient.GetAsync($"/api/v1/agents/{agent1.Id}"))
            .Content.ReadFromJsonAsync<AgentDto>(JsonOptions);
        var keyId = agent1Data!.Keys[0].Id;

        // Try to revoke agent1's key via agent2's URL
        var response = await fixture.AdminClient.DeleteAsync(
            $"/api/v1/agents/{agent2.Id}/keys/{keyId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent1.Id}");
        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agent2.Id}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<AgentDto> CreateAgentAsync(string name, string? description = null)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest(name, description));
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            because: $"creating agent '{name}' should succeed");
        return (await response.Content.ReadFromJsonAsync<AgentDto>(JsonOptions))!;
    }

    private async Task<CreateAgentKeyResponse> CreateAgentKeyAsync(
        Guid agentId,
        string name,
        string[]? scopes = null)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync(
            $"/api/v1/agents/{agentId}/keys",
            new CreateAgentKeyRequest(name, scopes));
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            because: $"creating key '{name}' for agent {agentId} should succeed");
        return (await response.Content.ReadFromJsonAsync<CreateAgentKeyResponse>(JsonOptions))!;
    }

    /// <summary>
    /// Sends a POST to /mcp with the Accept header required by Streamable HTTP transport.
    /// </summary>
    private static async Task<HttpResponseMessage> PostMcpAsync(HttpClient client, object body)
    {
        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await client.SendAsync(request);
    }
}
