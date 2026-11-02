using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using Connapse.Application.Extensions;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using Connapse.Web.Components.Pages;
using Connapse.Web.Components.Pages.Admin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The API Tokens and Agents pages when the database fails: a message instead of a dead page, and a
/// token or key just created stays on screen when only the list refresh after it fails.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class TokenPageErrorTests : IDisposable
{
    private const string DatabaseError = "connection to server at db:5432 refused";

    private readonly BunitContext ctx = new();
    private readonly IPatService pats = Substitute.For<IPatService>();
    private readonly IAgentService agents = Substitute.For<IAgentService>();
    private readonly AgentDto agent = new(Guid.NewGuid(), "my-agent", null, true, Guid.NewGuid(), DateTime.UtcNow, []);

    public TokenPageErrorTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles("Admin");
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
        ctx.Services.AddLogging();

        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        ctx.Services.AddSingleton(authorizer);
        ctx.Services.AddSingleton(pats);
        ctx.Services.AddSingleton(agents);
        ctx.Services.AddSingleton(Substitute.For<IAuditLogger>());
        ctx.Services.AddSingleton(TimeProvider.System);
        ctx.Services.AddApplication();

        pats.ListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        agents.ListAsync(Arg.Any<CancellationToken>()).Returns([agent]);
        agents.GetAsync(agent.Id, Arg.Any<CancellationToken>()).Returns(agent);
    }

    public void Dispose() => ctx.Dispose();

    private static IElement Button(IRenderedComponent<PersonalTokens> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == text);

    private static IElement Button(IRenderedComponent<AgentManagement> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == text);

    private static void ShowsError<T>(IRenderedComponent<T> cut, string message) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.WaitForAssertion(() =>
        {
            string alert = cut.Find(".alert-danger").TextContent;
            alert.Should().Contain(message);
            // The database's own text can name hosts and tables: it goes to the log, not the page.
            alert.Should().NotContain(DatabaseError);
        });

    private IRenderedComponent<PersonalTokens> CreateToken()
    {
        var cut = ctx.Render<PersonalTokens>();
        Button(cut, "New Token").Click();
        cut.Find("input[placeholder='e.g. my-script']").Change("my-script");
        Button(cut, "Create Token").Click();
        return cut;
    }

    private static PatCreateResponse NewToken(string token) =>
        new(Guid.NewGuid(), "my-script", token, ["knowledge:read"], DateTime.UtcNow, null);

    [Fact]
    public void PersonalTokens_CreateThrows_ShowsErrorAndPageKeepsWorking()
    {
        pats.CreateAsync(Arg.Any<Guid>(), Arg.Any<PatCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PatCreateResponse>(new InvalidOperationException(DatabaseError)), Task.FromResult(NewToken("cnp_second")));

        var cut = CreateToken();

        ShowsError(cut, "The token couldn't be created.");
        Button(cut, "Create Token").Click();
        cut.WaitForAssertion(() => cut.Find("#new-pat-token").GetAttribute("value").Should().Be("cnp_second"));
    }

    [Fact]
    public void PersonalTokens_OnlyRefreshFailsAfterCreate_TokenStaysVisible()
    {
        pats.CreateAsync(Arg.Any<Guid>(), Arg.Any<PatCreateRequest>(), Arg.Any<CancellationToken>()).Returns(NewToken("cnp_once"));
        pats.ListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PatListItem>>([]), Task.FromException<IReadOnlyList<PatListItem>>(new InvalidOperationException(DatabaseError)));

        var cut = CreateToken();

        ShowsError(cut, "Your tokens couldn't be loaded.");
        cut.Find("#new-pat-token").GetAttribute("value").Should().Be("cnp_once");
    }

    [Fact]
    public void PersonalTokens_RevokeThrows_ShowsErrorAndKeepsTheToken()
    {
        var token = new PatListItem(Guid.NewGuid(), "my-script", "cnp_ab", ["knowledge:read"], DateTime.UtcNow, null, null, false);
        pats.ListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([token]);
        pats.RevokeAsync(Arg.Any<Guid>(), token.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(DatabaseError));
        var cut = ctx.Render<PersonalTokens>();

        Button(cut, "Revoke").Click();
        Button(cut, "Confirm Revoke").Click();

        ShowsError(cut, "The token couldn't be revoked.");
        Button(cut, "Revoke").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void PersonalTokens_FirstLoadThrows_ShowsErrorInsteadOfSpinner()
    {
        pats.ListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(DatabaseError));

        var cut = ctx.Render<PersonalTokens>();

        ShowsError(cut, "Your tokens couldn't be loaded.");
        cut.FindAll(".spinner-border").Should().BeEmpty();
    }

    [Fact]
    public void AgentManagement_CreateThrows_ShowsErrorAndPageKeepsWorking()
    {
        agents.CreateAsync(Arg.Any<CreateAgentRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AgentDto?>(new InvalidOperationException(DatabaseError)), Task.FromResult<AgentDto?>(agent with { Name = "second" }));
        var cut = ctx.Render<AgentManagement>();
        cut.Find("input[placeholder='my-agent']").Change("second");

        Button(cut, "Create Agent").Click();

        ShowsError(cut, "The agent couldn't be created.");
        Button(cut, "Create Agent").Click();
        cut.WaitForAssertion(() => cut.Find(".alert-success").TextContent.Should().Contain("Agent 'second' created."));
    }

    [Fact]
    public void AgentManagement_OnlyRefreshFailsAfterCreateKey_KeyStaysVisible()
    {
        agents.CreateKeyAsync(agent.Id, Arg.Any<CreateAgentKeyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CreateAgentKeyResponse(Guid.NewGuid(), agent.Id.ToString(), "cnp_agent_once", ["knowledge:read"], DateTime.UtcNow, null));
        var cut = ctx.Render<AgentManagement>();
        Button(cut, "API Keys").Click();
        cut.Find("input[placeholder='e.g. production']").Change("production");
        agents.ListAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(DatabaseError));
        agents.GetAsync(agent.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(DatabaseError));

        Button(cut, "Add Key").Click();

        ShowsError(cut, "couldn't be loaded.");
        cut.Find("#new-agent-key-token").GetAttribute("value").Should().Be("cnp_agent_once");
    }

    [Fact]
    public void AgentManagement_RevokeKeyThrows_ShowsError()
    {
        var key = new AgentKeyListItem(Guid.NewGuid(), "production", "cnp_ab", ["knowledge:read"], DateTime.UtcNow, null, null, false);
        agents.GetAsync(agent.Id, Arg.Any<CancellationToken>()).Returns(agent with { Keys = [key] });
        agents.RevokeKeyAsync(agent.Id, key.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(DatabaseError));
        var cut = ctx.Render<AgentManagement>();
        Button(cut, "API Keys").Click();

        Button(cut, "Revoke").Click();

        ShowsError(cut, "The key couldn't be revoked.");
        Button(cut, "Revoke").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void AgentManagement_KeysOfAnotherAgentFailToLoad_ShowsNoneOfTheFirstAgentsKeys()
    {
        var key = new AgentKeyListItem(Guid.NewGuid(), "production", "cnp_ab", ["knowledge:read"], DateTime.UtcNow, null, null, false);
        var other = agent with { Id = Guid.NewGuid(), Name = "other-agent" };
        agents.ListAsync(Arg.Any<CancellationToken>()).Returns([agent with { Keys = [key] }, other]);
        agents.GetAsync(agent.Id, Arg.Any<CancellationToken>()).Returns(agent with { Keys = [key] });
        agents.GetAsync(other.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(DatabaseError));
        var cut = ctx.Render<AgentManagement>();
        cut.FindAll("button").Where(b => b.TextContent.Trim() == "API Keys").ElementAt(0).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("cnp_ab"));

        cut.FindAll("button").Where(b => b.TextContent.Trim() == "API Keys").ElementAt(1).Click();

        ShowsError(cut, "The agent's keys couldn't be loaded.");
        cut.Markup.Should().NotContain("cnp_ab");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Revoke");
    }

    [Theory]
    [InlineData("Disable", "The agent couldn't be updated.")]
    [InlineData("Confirm Delete", "The agent couldn't be deleted.")]
    public void AgentManagement_AgentChangeThrows_ShowsError(string button, string message)
    {
        agents.SetActiveAsync(agent.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(DatabaseError));
        agents.DeleteAsync(agent.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(DatabaseError));
        var cut = ctx.Render<AgentManagement>();
        if (button == "Confirm Delete")
            Button(cut, "Delete").Click();

        Button(cut, button).Click();

        ShowsError(cut, message);
    }

    [Fact]
    public void AgentManagement_FirstLoadThrows_ShowsError()
    {
        agents.ListAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException(DatabaseError));

        var cut = ctx.Render<AgentManagement>();

        ShowsError(cut, "The agents couldn't be loaded.");
    }
}
