using System.Net.Http.Headers;
using System.Text.Json;
using Connapse.Core;
using Connapse.Identity.Authentication;
using Connapse.Identity.Services;
using Connapse.Integration.Tests.Authorization;
using Connapse.Integration.Tests.CrossSurface;
using Principal = Connapse.Integration.Tests.CrossSurface.Principal;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// CON-227's proof, against the pass bar set in docs/plans/con-227-agent-memory-container.md before the feature was
/// built: through a real MCP client over HTTP with agent keys, an agent creates an agent memory container, saves five
/// general facts in one session and recalls every one of them in a new session, by subject and by a free-text
/// question. The document tools refuse the container, and memory permissions hold as in any container.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemoryContainerEndToEndTests(SharedWebAppFixture fixture)
{
    private const string Refusal = "is an agent memory container: it holds memory facts, not documents";

    /// <summary>Five general facts no document backs, each with the question an agent would later ask.</summary>
    private static readonly (string Subject, string Attribute, string Value, string Question)[] Facts =
    [
        ("team", "standup time", "9:30 AM Central, every weekday", "When is the daily standup?"),
        ("deploys", "freeze day", "No production deploys on Fridays", "Which day are deploys frozen?"),
        ("codebase", "test framework", "xUnit with FluentAssertions and NSubstitute", "What test framework do we use?"),
        ("owner", "preferred editor", "Neovim with the LazyVim setup", "Which editor does the owner prefer?"),
        ("releases", "version scheme", "Semantic versioning with alpha pre-releases", "How are release versions numbered?"),
    ];

    [Fact]
    public async Task AgentMemoryContainer_FactsSavedInOneSession_AreAllRecalledInTheNext()
    {
        string name = $"agent-memory-{Guid.NewGuid():N}"[..40];
        string id;

        // Session 1: create the container, see it listed as memory, save the five facts.
        await using (var agent = await ConnectAsync(AuthzPrincipal.AgentWrite))
        {
            string created = await CallAsync(agent, "container_create", new() { ["name"] = name, ["kind"] = "memory" });
            created.Should().StartWith($"Agent memory container '{name}' created.");
            id = created[(created.LastIndexOf("ID: ", StringComparison.Ordinal) + 4)..].Trim();

            (await CallAsync(agent, "container_list", new())).Should().Contain($"- {name} [memory]");

            foreach (var (subject, attribute, value, _) in Facts)
            {
                string saved = await CallAsync(agent, "memory_remember", new()
                {
                    ["containerId"] = name,
                    ["subject"] = subject,
                    ["attribute"] = attribute,
                    ["value"] = value,
                });
                saved.Should().StartWith("{\"remembered\"", saved);
            }
        }

        try
        {
            // Session 2: a fresh connection recalls every fact, by its subject and by a question in its top 3.
            int bySubject = 0, byQuestion = 0;
            await using (var agent = await ConnectAsync(AuthzPrincipal.AgentWrite))
            {
                foreach (var (subject, attribute, value, question) in Facts)
                {
                    if (Values(await CallAsync(agent, "memory_recall", new() { ["containerId"] = id, ["subject"] = subject }))
                        .Contains(value))
                        bySubject++;
                    if (Values(await CallAsync(agent, "memory_recall", new() { ["containerId"] = id, ["query"] = question, ["limit"] = 3 }))
                        .Contains(value))
                        byQuestion++;
                }

                bySubject.Should().Be(Facts.Length, "every fact is recalled by its subject (5/5 required)");
                byQuestion.Should().Be(Facts.Length, "every fact is in the top 3 for its question (5/5 required)");

                // The document tools refuse it, and nothing is stored.
                (await CallAsync(agent, "upload_file", new() { ["containerId"] = id, ["textContent"] = "hello", ["fileName"] = "a.md" }))
                    .Should().Contain(Refusal);
                (await CallAsync(agent, "list_files", new() { ["containerId"] = id })).Should().Contain(Refusal);
                (await CallAsync(agent, "search_knowledge", new() { ["containerId"] = id, ["query"] = "standup" })).Should().Contain(Refusal);
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
                (await db.Documents.CountAsync(d => d.ContainerId == Guid.Parse(id))).Should().Be(0);
            }

            // Permissions as in any container: a read-only key of the same owner recalls but can't write.
            await using (var reader = await ConnectAsync(AuthzPrincipal.AgentRead))
            {
                Values(await CallAsync(reader, "memory_recall", new() { ["containerId"] = id, ["subject"] = "team" }))
                    .Should().Contain(Facts[0].Value);
                string written;
                try
                {
                    written = await CallAsync(reader, "memory_remember", new()
                    {
                        ["containerId"] = id,
                        ["subject"] = "team",
                        ["attribute"] = "retro",
                        ["value"] = "Thursdays",
                    });
                }
                catch (McpException ex)
                {
                    written = ex.Message; // the tool isn't offered to a key without knowledge:write
                }
                written.Should().NotContain("remembered", "memory_remember needs knowledge:write");
                Values(await CallAsync(reader, "memory_recall", new() { ["containerId"] = id, ["subject"] = "team" }))
                    .Should().NotContain("Thursdays");
            }

            // A fact citing no documents is shown only to its owner, never to another user's agent.
            await using (var stranger = await ConnectAsync(await OtherUsersAgentKeyAsync()))
                Values(await CallAsync(stranger, "memory_recall", new() { ["containerId"] = id, ["subject"] = "team" }))
                    .Should().BeEmpty();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{id}");
        }
    }

    /// <summary>A knowledge:read key for an agent another user (the cross-surface viewer) created.</summary>
    private async Task<Action<HttpRequestHeaders>> OtherUsersAgentKeyAsync()
    {
        Guid other = await Principals.UserIdAsync(fixture, Principal.Viewer);
        using var scope = fixture.Factory.Services.CreateScope();
        var agents = scope.ServiceProvider.GetRequiredService<IAgentService>();
        var agent = await agents.CreateAsync(new CreateAgentRequest($"e2e-other-{Guid.NewGuid():N}"), other);
        var key = await agents.CreateKeyAsync(agent!.Id, new CreateAgentKeyRequest("e2e", ["knowledge:read"], RequiresApproval: false));
        return headers => headers.Add(ApiKeyAuthenticationOptions.HeaderName, key!.Token);
    }

    private async Task<McpClient> ConnectAsync(AuthzPrincipal principal) =>
        await ConnectAsync(await AuthzPrincipals.CredentialAsync(fixture, principal));

    private async Task<McpClient> ConnectAsync(Action<HttpRequestHeaders> credential)
    {
        var http = fixture.Factory.CreateClient();
        credential(http.DefaultRequestHeaders);
        return await McpTestClient.ConnectAsync(http);
    }

    private static async Task<string> CallAsync(McpClient agent, string tool, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await agent.CallToolAsync(tool, arguments);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    private static List<string> Values(string recalled)
    {
        recalled.Should().StartWith("{", recalled);
        return [.. JsonDocument.Parse(recalled).RootElement.GetProperty("facts").EnumerateArray()
            .Select(f => f.GetProperty("value").GetString()!)];
    }
}
