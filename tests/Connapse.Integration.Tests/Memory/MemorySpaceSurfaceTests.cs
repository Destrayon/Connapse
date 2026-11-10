using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Authentication;
using Connapse.Identity.Services;
using Connapse.Integration.Tests.Authorization;
using Connapse.Integration.Tests.CrossSurface;
using Principal = Connapse.Integration.Tests.CrossSurface.Principal;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// CON-235 through the real host: a personal space exists only for its owner. Another user gets "not found" for it by
/// id and by name over REST, in the REST and MCP container lists, in search and in the memory tools, while the owner
/// sees it. A Viewer's agent recalls team memory but saves none. MCP admits administrators and agents only, so a user
/// without the Admin role reaches it through an agent they created, which acts as them.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemorySpaceSurfaceTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task PersonalSpace_IsNotFoundForEveryoneButItsOwner_OnEverySurface()
    {
        string name = $"space-surface-{Guid.NewGuid():N}"[..40];
        string personalName = $"{name}-personal";
        string team = await CreateMemoryContainerAsync(name);
        string? personal = null;
        Guid ownerId = await Principals.UserIdAsync(fixture, Principal.Viewer);
        using HttpClient ownerRest = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Viewer);
        try
        {
            // The owner's agent saves a fact citing nothing the old way: it goes to the owner's own personal space.
            await using (var owner = await ConnectAsync(await AgentKeyAsync(ownerId, "knowledge:read", "knowledge:write")))
            {
                string saved = await CallAsync(owner, "memory_remember", new()
                {
                    ["containerId"] = name,
                    ["subject"] = "owner",
                    ["attribute"] = "lunch",
                    ["value"] = "Tacos on Tuesdays",
                });
                saved.Should().StartWith("{\"remembered\"", saved);
                JsonElement savedTo = JsonDocument.Parse(saved).RootElement.GetProperty("savedTo");
                savedTo.GetProperty("name").GetString().Should().Be(personalName);
                savedTo.GetProperty("audience").GetString().Should().Be("personal");
                personal = savedTo.GetProperty("id").GetString()!;

                Values(await CallAsync(owner, "memory_recall", new() { ["detailed"] = true, ["containerId"] = personal, ["subject"] = "owner" }))
                    .Should().Contain("Tacos on Tuesdays");
                Values(await CallAsync(owner, "memory_recall", new() { ["detailed"] = true, ["containerId"] = personalName, ["subject"] = "owner" }))
                    .Should().Contain("Tacos on Tuesdays");
                // A memory space is not a file container (CON-291): the owner finds it among memory spaces instead.
                (await CallAsync(owner, "container_list", new())).Should().NotContain(personalName);
                (await CallAsync(owner, "memory_spaces", new())).Should().Contain(personalName);
            }

            // The owner sees it over REST, by id and by name.
            (await ownerRest.GetAsync($"/api/containers/{personal}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ownerRest.GetAsync($"/api/containers/by-name/{personalName}")).StatusCode.Should().Be(HttpStatusCode.OK);

            // Anyone else, an administrator included, finds nothing there.
            HttpClient other = fixture.AdminClient;
            (await other.GetAsync($"/api/containers/{personal}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.GetAsync($"/api/containers/by-name/{personalName}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.GetAsync($"/api/containers/{personal}/search?q=tacos")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.GetAsync($"/api/containers/{personal}/settings")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.DeleteAsync($"/api/containers/{personal}/settings")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ownerRest.GetAsync($"/api/containers/{personal}/settings")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ListedAsync(other)).Should().NotContain(personal).And.NotContain(personalName);
            (await ListedAsync(ownerRest)).Should().NotContain(personal, "a memory space is not a file container (CON-291)");

            await using (var stranger = await ConnectAsync(await AuthzPrincipals.CredentialAsync(fixture, AuthzPrincipal.AgentWrite)))
            {
                (await CallAsync(stranger, "container_list", new())).Should().NotContain(personalName);
                (await CallAsync(stranger, "memory_recall", new() { ["detailed"] = true, ["containerId"] = personal, ["subject"] = "owner" }))
                    .Should().ContainEquivalentOf("not found");
                (await CallAsync(stranger, "memory_recall", new() { ["detailed"] = true, ["containerId"] = personalName, ["subject"] = "owner" }))
                    .Should().ContainEquivalentOf("not found");
                (await CallAsync(stranger, "search_knowledge", new() { ["containerId"] = personal, ["query"] = "tacos" }))
                    .Should().ContainEquivalentOf("not found");
                Values(await CallAsync(stranger, "memory_recall", new() { ["detailed"] = true, ["containerId"] = team, ["subject"] = "owner" }))
                    .Should().BeEmpty("the fact went to the owner's personal space, not the team container");
            }
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
            if (personal is not null)
                await containers.DeleteAsync(Guid.Parse(personal));
            await containers.DeleteAsync(Guid.Parse(team));
        }
    }

    [Fact]
    public async Task Viewer_RecallsFromATeamSpace_ButCannotSaveToIt()
    {
        string name = $"space-viewer-{Guid.NewGuid():N}"[..40];
        string team = await CreateMemoryContainerAsync(name);
        try
        {
            Guid admin = await Principals.UserIdAsync(fixture, Principal.Admin);
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IMemoryService>().RememberAsync(Guid.Parse(team),
                    new NewMemoryFact("deploys", "freeze day", "Fridays", null, null, [], null, MemoryScope.Team),
                    new MemoryCaller(admin, null, "admin"));
            }

            // A read-only key on a Viewer's agent: the most a Viewer can hand an agent.
            await using var viewer = await ConnectAsync(
                await AgentKeyAsync(await Principals.UserIdAsync(fixture, Principal.Viewer), "knowledge:read"));
            Values(await CallAsync(viewer, "memory_recall", new() { ["detailed"] = true, ["containerId"] = name, ["subject"] = "deploys" }))
                .Should().Contain("Fridays");

            string written;
            try
            {
                written = await CallAsync(viewer, "memory_remember", new()
                {
                    ["containerId"] = name,
                    ["subject"] = "deploys",
                    ["attribute"] = "thaw day",
                    ["value"] = "Mondays",
                });
            }
            catch (McpException ex)
            {
                written = ex.Message; // the tool isn't offered to a key without knowledge:write
            }
            written.Should().NotContain("remembered", "a Viewer reads memory but saves none");
            Values(await CallAsync(viewer, "memory_recall", new() { ["detailed"] = true, ["containerId"] = name, ["subject"] = "deploys" }))
                .Should().NotContain("Mondays");
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IContainerStore>().DeleteAsync(Guid.Parse(team));
        }
    }

    private async Task<string> CreateMemoryContainerAsync(string name)
    {
        await using var admin = await ConnectAsync(await AuthzPrincipals.CredentialAsync(fixture, AuthzPrincipal.AgentWrite));
        string created = await CallAsync(admin, "container_create", new() { ["name"] = name, ["kind"] = "memory" });
        created.Should().StartWith($"Agent memory container '{name}' created.");
        return created[(created.LastIndexOf("ID: ", StringComparison.Ordinal) + 4)..].Trim();
    }

    /// <summary>Every container id the REST list shows <paramref name="http"/>'s user, page by page.</summary>
    private static async Task<List<string>> ListedAsync(HttpClient http)
    {
        List<string> ids = [];
        for (int skip = 0; ; skip += 200)
        {
            JsonElement page = JsonDocument.Parse(await http.GetStringAsync($"/api/containers?skip={skip}&take=200")).RootElement;
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetString()!));
            if (!page.GetProperty("hasMore").GetBoolean())
                return ids;
        }
    }

    /// <summary>A key for a new agent <paramref name="user"/> created, so it acts as that user.</summary>
    private async Task<Action<HttpRequestHeaders>> AgentKeyAsync(Guid user, params string[] scopes)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var agents = scope.ServiceProvider.GetRequiredService<IAgentService>();
        var agent = await agents.CreateAsync(new CreateAgentRequest($"space-{Guid.NewGuid():N}"), user);
        var key = await agents.CreateKeyAsync(agent!.Id, new CreateAgentKeyRequest("space", scopes, RequiresApproval: false));
        return headers => headers.Add(ApiKeyAuthenticationOptions.HeaderName, key!.Token);
    }

    private async Task<McpClient> ConnectAsync(Action<HttpRequestHeaders> credential)
    {
        var http = fixture.Factory.CreateClient();
        credential(http.DefaultRequestHeaders);
        return await McpTestClient.ConnectAsync(http);
    }

    private static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(tool, arguments);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    private static List<string> Values(string recalled)
    {
        recalled.Should().StartWith("{", recalled);
        return [.. JsonDocument.Parse(recalled).RootElement.GetProperty("facts").EnumerateArray()
            .Select(f => f.GetProperty("value").GetString()!)];
    }
}
