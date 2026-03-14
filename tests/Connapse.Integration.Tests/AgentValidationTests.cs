using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AgentValidationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ── Agent name validation ───────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    public async Task CreateAgent_NameTooShort_Returns400(string name)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest(name));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        body.GetProperty("error").GetString().Should().Be("agent_name_invalid");
    }

    [Fact]
    public async Task CreateAgent_NameTooLong_Returns400()
    {
        var name = new string('a', 65);
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest(name));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        body.GetProperty("error").GetString().Should().Be("agent_name_invalid");
    }

    [Theory]
    [InlineData("my agent")]
    [InlineData("agent!@#")]
    [InlineData("agent name")]
    [InlineData("agent.name")]
    public async Task CreateAgent_NameInvalidChars_Returns400(string name)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest(name));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        body.GetProperty("error").GetString().Should().Be("agent_name_invalid");
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("my-agent_01")]
    public async Task CreateAgent_ValidName_Returns201(string name)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest(name));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var agent = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var agentId = agent.GetProperty("id").GetString();
        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agentId}");
    }

    [Fact]
    public async Task CreateAgent_NameAtMaxLength_Returns201()
    {
        var name = new string('a', 64);
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest(name));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var agent = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var agentId = agent.GetProperty("id").GetString();
        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agentId}");
    }

    // ── Agent description validation ────────────────────────────────

    [Fact]
    public async Task CreateAgent_DescriptionTooLong_Returns400()
    {
        var description = new string('x', 501);
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest("valid-agent", description));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        body.GetProperty("error").GetString().Should().Be("agent_description_too_long");
    }

    [Fact]
    public async Task CreateAgent_DescriptionAtMaxLength_Returns201()
    {
        var description = new string('x', 500);
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest("valid-desc-agent", description));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var agent = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var agentId = agent.GetProperty("id").GetString();
        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agentId}");
    }

    // ── Agent key name validation ───────────────────────────────────

    [Fact]
    public async Task CreateAgentKey_EmptyName_Returns400()
    {
        var agentResp = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest("key-test-agent"));
        agentResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var agent = await agentResp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var agentId = agent.GetProperty("id").GetString();

        try
        {
            var response = await fixture.AdminClient.PostAsJsonAsync(
                $"/api/v1/agents/{agentId}/keys",
                new CreateAgentKeyRequest(""));
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            body.GetProperty("error").GetString().Should().Be("agent_key_name_invalid");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agentId}");
        }
    }

    [Fact]
    public async Task CreateAgentKey_NameTooLong_Returns400()
    {
        var agentResp = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest("key-long-agent"));
        agentResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var agent = await agentResp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var agentId = agent.GetProperty("id").GetString();

        try
        {
            var keyName = new string('k', 65);
            var response = await fixture.AdminClient.PostAsJsonAsync(
                $"/api/v1/agents/{agentId}/keys",
                new CreateAgentKeyRequest(keyName));
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            body.GetProperty("error").GetString().Should().Be("agent_key_name_invalid");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agentId}");
        }
    }

    [Fact]
    public async Task CreateAgentKey_ValidName_Returns201()
    {
        var agentResp = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new CreateAgentRequest("key-valid-agent"));
        agentResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var agent = await agentResp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var agentId = agent.GetProperty("id").GetString();

        try
        {
            var response = await fixture.AdminClient.PostAsJsonAsync(
                $"/api/v1/agents/{agentId}/keys",
                new CreateAgentKeyRequest("my-valid-key"));
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agentId}");
        }
    }
}
