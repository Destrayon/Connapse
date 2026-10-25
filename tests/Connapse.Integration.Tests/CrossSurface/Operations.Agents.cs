using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Connapse.Core;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using Connapse.Web.Components.Pages.Admin;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>A case's own agent, named after the case's container, and the one key it holds.</summary>
public sealed record AgentCase(Guid Id, string Name, Guid KeyId);

public static partial class Operations
{
    /// <summary>MCP reaches agents only through the generic tools Phase 5 adds.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoAgentTools = new Dictionary<string, string>
    {
        ["mcp"] = "MCP has no agent tools; the generic connapse_api_* tools arrive in Phase 5 of #783",
    };

    private const string CaseKeyName = "case-key";

    /// <summary>
    /// The agent operations (CON-71), on REST and the Agents page. The page acts only on the agents and
    /// keys it lists, so it has no way to name one that doesn't exist: those invalid cases are REST's alone.
    /// </summary>
    private static readonly Operation[] AgentOperations =
    [
        new("agents_list", [Case.Valid, Case.UnderScoped], Writes: false, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.GetAsync("/api/v1/agents");
                return await WithPayloadAsync(response, body => Listed(AgentNames(body).Contains(c.Agent!.Name)));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<AgentManagement>();
                if (AgentsPageError(page) is { } error)
                    return error;
                return "ok " + Listed(AgentCard(page, c.Agent!.Name) is not null);
            },
            Project: (sp, c) => AgentStateAsync(sp, c))
        {
            AbsentReasons = NoAgentTools,
            ArrangesAgent = true,
        },

        // Reads the case's agent. The invalid case names one that doesn't exist.
        new("agents_get", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: false, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.GetAsync($"/api/v1/agents/{AgentTarget(c, k)}");
                return await WithPayloadAsync(response, body => Found(body.Contains(c.Agent!.Name, StringComparison.Ordinal)));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                if (k == Case.Invalid)
                    return null;
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<AgentManagement>();
                if (AgentsPageError(page) is { } error)
                    return error;
                return "ok " + Found(AgentCard(page, c.Agent!.Name) is not null);
            },
            Project: (sp, c) => AgentStateAsync(sp, c))
        {
            AbsentReasons = NoAgentTools,
            ArrangesAgent = true,
        },

        // Creates an agent named after the case. The invalid case's name has spaces; preflight reuses the
        // name of the case's own agent, which is taken.
        new("agents_create", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.PostAsJsonAsync("/api/v1/agents", new { name = NewAgentName(c, k), description = "made here" });
                // The created agent is in the state; its id is this surface's alone.
                return response.IsSuccessStatusCode ? "ok" : await Surfaces.ResultAsync(response);
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<AgentManagement>();
                if (AgentsPageError(page) is { } error)
                    return error;
                await page.InvokeAsync(() => page.Find("input[placeholder='my-agent']").Change(NewAgentName(c, k)));
                await page.InvokeAsync(() => page.Find("input[placeholder='What does this agent do?']").Change("made here"));
                await ClickAsync(page, () => page.FindAll("button").First(b => b.TextContent.Trim() == "Create Agent"));
                return AgentsPageOutcome(page);
            },
            Project: (sp, c) => AgentStateAsync(sp, c))
        {
            AbsentReasons = NoAgentTools,
            ArrangesAgent = true,
        },

        // Turns the case's agent off. The invalid case names one that doesn't exist.
        new("agents_setActive", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                return await Surfaces.ResultAsync(await rest.PutAsJsonAsync($"/api/v1/agents/{AgentTarget(c, k)}/active", new { isActive = false }));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                if (k == Case.Invalid)
                    return null;
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<AgentManagement>();
                if (AgentsPageError(page) is { } error)
                    return error;
                await ClickAsync(page, () => AgentButton(page, c, "Disable"));
                return AgentsPageOutcome(page);
            },
            Project: (sp, c) => AgentStateAsync(sp, c))
        {
            AbsentReasons = NoAgentTools,
            ArrangesAgent = true,
        },

        // Deletes the case's agent, which revokes its key. The invalid case names one that doesn't exist.
        new("agents_delete", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                return await Surfaces.ResultAsync(await rest.DeleteAsync($"/api/v1/agents/{AgentTarget(c, k)}"));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                if (k == Case.Invalid)
                    return null;
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<AgentManagement>();
                if (AgentsPageError(page) is { } error)
                    return error;
                await ClickAsync(page, () => AgentButton(page, c, "Delete"));
                await ClickAsync(page, () => AgentButton(page, c, "Confirm Delete"));
                return AgentsPageOutcome(page);
            },
            Project: (sp, c) => AgentStateAsync(sp, c))
        {
            AbsentReasons = NoAgentTools,
            ArrangesAgent = true,
        },

        // Lists the case's agent's keys. The invalid case names an agent that doesn't exist.
        new("agents_keys_list", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: false, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.GetAsync($"/api/v1/agents/{AgentTarget(c, k)}/keys");
                return await WithPayloadAsync(response, body => Listed(KeyNames(body).Contains(CaseKeyName)));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                if (k == Case.Invalid)
                    return null;
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<AgentManagement>();
                if (AgentsPageError(page) is { } error)
                    return error;
                await OpenKeysAsync(page, c);
                return "ok " + Listed(KeyRow(page, c) is not null);
            },
            Project: (sp, c) => AgentStateAsync(sp, c))
        {
            AbsentReasons = NoAgentTools,
            ArrangesAgent = true,
        },

        // Gives the case's agent a second key that reads knowledge. The invalid case's key name is one character.
        new("agents_keys_create", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.PostAsJsonAsync($"/api/v1/agents/{c.Agent!.Id}/keys",
                    new { name = NewKeyName(k), scopes = new[] { "knowledge:read" }, requiresApproval = true });
                // The key is in the state; its id and one-time token are this surface's alone.
                return response.IsSuccessStatusCode ? "ok" : await Surfaces.ResultAsync(response);
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<AgentManagement>();
                if (AgentsPageError(page) is { } error)
                    return error;
                await OpenKeysAsync(page, c);
                await page.InvokeAsync(() => page.Find("input[placeholder='e.g. production']").Change(NewKeyName(k)));
                await ClickAsync(page, () => page.FindAll("button").First(b => b.TextContent.Trim() == "Add Key"));
                return AgentsPageOutcome(page);
            },
            Project: (sp, c) => AgentStateAsync(sp, c))
        {
            AbsentReasons = NoAgentTools,
            ArrangesAgent = true,
        },

        // Revokes the case's agent's key. The invalid case names a key that doesn't exist.
        new("agents_keys_revoke", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                string key = k == Case.Invalid ? c.Unknown : c.Agent!.KeyId.ToString();
                return await Surfaces.ResultAsync(await rest.DeleteAsync($"/api/v1/agents/{c.Agent!.Id}/keys/{key}"));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                if (k == Case.Invalid)
                    return null;
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<AgentManagement>();
                if (AgentsPageError(page) is { } error)
                    return error;
                await OpenKeysAsync(page, c);
                await ClickAsync(page, () => KeyRow(page, c)!.QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Revoke"));
                return AgentsPageOutcome(page);
            },
            Project: (sp, c) => AgentStateAsync(sp, c))
        {
            AbsentReasons = NoAgentTools,
            ArrangesAgent = true,
        },
    ];

    private static string AgentName(string containerName) => $"agent-{containerName}";

    /// <summary>The agent a case acts on: one that doesn't exist for the invalid case, the case's own otherwise.</summary>
    private static string AgentTarget(CaseContext c, string @case) => @case == Case.Invalid ? c.Unknown : c.Agent!.Id.ToString();

    /// <summary>A name for a create: one of the case's own, one with spaces, or the case's agent's, which is taken.</summary>
    private static string NewAgentName(CaseContext c, string @case) => @case switch
    {
        Case.Invalid => "not a valid name",
        Case.Preflight => c.Agent!.Name,
        _ => $"{c.Agent!.Name}-{@case[..2]}",
    };

    private static string NewKeyName(string @case) => @case == Case.Invalid ? "x" : "second-key";

    private static List<string?> AgentNames(string body) =>
        JsonDocument.Parse(body).RootElement.EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();

    private static List<string?> KeyNames(string body) =>
        JsonDocument.Parse(body).RootElement.EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();

    private static IElement? AgentCard(IRenderedComponent<AgentManagement> page, string name) =>
        page.FindAll("div.card").FirstOrDefault(card => card.QuerySelector("span.fw-semibold")?.TextContent.Trim() == name);

    private static IElement AgentButton(IRenderedComponent<AgentManagement> page, CaseContext c, string text) =>
        AgentCard(page, c.Agent!.Name)!.QuerySelectorAll("button").First(b => b.TextContent.Trim() == text);

    private static IElement? KeyRow(IRenderedComponent<AgentManagement> page, CaseContext c) =>
        AgentCard(page, c.Agent!.Name)!.QuerySelectorAll("tbody tr").FirstOrDefault(r => r.QuerySelector("td")?.TextContent.Trim() == CaseKeyName);

    /// <summary>Opens the case's agent's API Keys panel and waits for its keys.</summary>
    private static async Task OpenKeysAsync(IRenderedComponent<AgentManagement> page, CaseContext c)
    {
        await ClickAsync(page, () => AgentButton(page, c, "API Keys"));
        page.WaitForAssertion(() => AgentCard(page, c.Agent!.Name)!.QuerySelectorAll("tbody tr, p.text-muted.small.mb-3, div.alert-danger")
            .Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
    }

    /// <summary>Waits for the page to load its agents; the error it showed instead, or null.</summary>
    private static string? AgentsPageError(IRenderedComponent<AgentManagement> page)
    {
        page.WaitForAssertion(() => page.FindAll("span.fw-semibold, div.alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
        return page.FindAll("div.alert-danger").FirstOrDefault() is { } alert ? Surfaces.ErrorKind(alert.TextContent.Trim()) : null;
    }

    /// <summary>What the page said after an action: its success alert, or its error in the shared words.</summary>
    private static string AgentsPageOutcome(IRenderedComponent<AgentManagement> page)
    {
        page.WaitForAssertion(() => page.FindAll("div.alert-success, div.alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
        return page.FindAll("div.alert-danger").FirstOrDefault() is { } alert ? Surfaces.ErrorKind(alert.TextContent.Trim()) : "ok";
    }

    /// <summary>Makes the case's own agent, as the admin, with one key that reads knowledge.</summary>
    private static async Task<AgentCase> ArrangeAgentAsync(SharedWebAppFixture fixture, string containerName)
    {
        Guid admin = await Principals.UserIdAsync(fixture, Principal.Admin);
        using var scope = fixture.Factory.Services.CreateScope();
        var agents = scope.ServiceProvider.GetRequiredService<IAgentService>();
        var agent = (await agents.CreateAsync(new CreateAgentRequest(AgentName(containerName), "the case's own"), admin))!;
        var key = (await agents.CreateKeyAsync(agent.Id, new CreateAgentKeyRequest(CaseKeyName, ["knowledge:read"])))!;
        return new AgentCase(agent.Id, agent.Name, key.KeyId);
    }

    /// <summary>
    /// Every agent a case may have made, deleted ones included, with their keys: any name with its
    /// container's name in it. Ids are listed in name order, so each surface's are given the same tokens.
    /// </summary>
    private static async Task<StateProjection> AgentStateAsync(IServiceProvider services, CaseContext c)
    {
        var db = services.GetRequiredService<ConnapseIdentityDbContext>();
        var agents = await db.Agents.AsNoTracking().Include(a => a.ApiKeys)
            .Where(a => a.Name.Contains(c.ContainerName))
            .OrderBy(a => a.Name)
            .ToListAsync();
        var state = new List<string>();
        var ids = new List<string>();
        foreach (var agent in agents)
        {
            state.Add($"agent {agent.Name} description={agent.Description} active={agent.IsActive} {(agent.DeletedAt is null ? "live" : "deleted")}");
            ids.Add(agent.Id.ToString());
            foreach (var key in agent.ApiKeys.OrderBy(k => k.Name, StringComparer.Ordinal))
            {
                state.Add($"  key {key.Name} scopes={key.Scopes} approval={key.RequiresApproval} {(key.RevokedAt is null ? "active" : "revoked")}");
                ids.Add(key.Id.ToString());
            }
        }
        if (c.Agent is { } own)
            ids.AddRange([own.Id.ToString(), own.KeyId.ToString()]);
        return new StateProjection(state.Count == 0 ? ["no agent"] : state, ids.Distinct().ToList());
    }

    private static async Task CleanupAgentsAsync(SharedWebAppFixture fixture, CaseContext c)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
        await db.AgentApiKeys.Where(k => k.Agent.Name.Contains(c.ContainerName)).ExecuteDeleteAsync();
        await db.Agents.Where(a => a.Name.Contains(c.ContainerName)).ExecuteDeleteAsync();
    }
}
