using Bunit;
using Connapse.Core;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using Connapse.Integration.Tests.CrossSurface;
using Connapse.Web.Components.Pages;
using Connapse.Web.Components.Pages.Admin;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// A token passes a scope check only with a scope it was issued with (#932), so a token made on either create
/// form has to carry the scopes ticked there, starting from knowledge:read.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class TokenFormScopeTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task PersonalTokenForm_LeftAsItOpens_IssuesKnowledgeRead()
    {
        string[] scopes = await CreatePersonalTokenAsync(_ => Task.CompletedTask);

        scopes.Should().BeEquivalentTo([TokenScopeChoices.Default]);
    }

    [Fact]
    public async Task PersonalTokenForm_ScopesTicked_IssuesJustThoseScopes()
    {
        string[] scopes = await CreatePersonalTokenAsync(async page =>
        {
            await page.InvokeAsync(() => page.Find("#new-pat-scope-knowledge\\:read").Change(false));
            await page.InvokeAsync(() => page.Find("#new-pat-scope-admin\\:audit").Change(true));
        });

        scopes.Should().BeEquivalentTo(["admin:audit"]);
    }

    [Fact]
    public async Task AgentKeyForm_ScopesTicked_IssuesJustThoseScopes()
    {
        using var ui = await Surfaces.UiAsync(fixture);
        var agents = ui.Context.Services.GetRequiredService<IAgentService>();
        var agent = (await agents.CreateAsync(new CreateAgentRequest($"form-scopes-{Guid.NewGuid():N}"), await AdminIdAsync(ui)))!;
        var page = ui.Context.Render<AgentManagement>();
        page.WaitForAssertion(() => page.FindAll("button").Should().Contain(b => b.TextContent.Contains("API Keys")));

        // Each find and its event on the renderer's dispatcher, so a render the page's loads finish with can't come between them.
        await page.InvokeAsync(() => page.FindAll("button").First(b => b.TextContent.Contains("API Keys") && IsAgentsRow(b, agent.Name)).Click());
        // The panel opens once the agent's keys have loaded.
        page.WaitForAssertion(() => page.Find($"#new-key-approval-{agent.Id}"));
        await page.InvokeAsync(() => page.Find($"#new-key-approval-{agent.Id}").Change(false));
        await page.InvokeAsync(() => page.Find($"#new-key-scope-{agent.Id}-agent\\:ingest").Change(true));
        await page.InvokeAsync(() => page.FindAll("input[placeholder='e.g. production']").Single().Change("form-key"));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Contains("Add Key")).Click());

        page.WaitForAssertion(() => page.Find("#new-agent-key-token"));
        var keys = await agents.ListKeysAsync(agent.Id);
        keys.Should().ContainSingle().Which.Scopes.Should().BeEquivalentTo([TokenScopeChoices.Default, "agent:ingest"]);
    }

    private async Task<string[]> CreatePersonalTokenAsync(Func<IRenderedComponent<PersonalTokens>, Task> tick)
    {
        using var ui = await Surfaces.UiAsync(fixture);
        var page = ui.Context.Render<PersonalTokens>();
        // Each find and its event on the renderer's dispatcher, so a render the page's loads finish with can't come between them.
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Contains("New Token")).Click());
        string name = $"form-scopes-{Guid.NewGuid():N}";
        await page.InvokeAsync(() => page.Find("input[placeholder='e.g. my-script']").Change(name));
        await tick(page);

        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Contains("Create Token")).Click());

        page.WaitForAssertion(() => page.Find("#new-pat-token"));
        var pats = ui.Context.Services.GetRequiredService<PatService>();
        return (await pats.ListAsync(await AdminIdAsync(ui))).Single(t => t.Name == name).Scopes;
    }

    private static async Task<Guid> AdminIdAsync(UiSurface ui) =>
        (await ui.Context.Services.GetRequiredService<ConnapseIdentityDbContext>().Users.AsNoTracking()
            .SingleAsync(u => u.Email == SharedWebAppFixture.AdminEmail)).Id;

    private static bool IsAgentsRow(AngleSharp.Dom.IElement button, string agentName) =>
        button.Closest(".card-body")?.TextContent.Contains(agentName) ?? false;
}
