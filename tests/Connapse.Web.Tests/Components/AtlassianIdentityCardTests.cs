using Bunit;
using Connapse.Identity.Data.Entities;
using Connapse.Web.Components.Shared;
using FluentAssertions;

namespace Connapse.Web.Tests.Components;

/// <summary>The Atlassian row on the integrations page: Link when there is no link, the account and Unlink when there is.</summary>
[Trait("Category", "Unit")]
public class AtlassianIdentityCardTests : IDisposable
{
    private readonly BunitContext ctx = new();

    public void Dispose() => ctx.Dispose();

    [Fact]
    public void Render_NoLink_ShowsLinkAndNoDisconnect()
    {
        bool connected = false;
        var cut = ctx.Render<AtlassianIdentityCard>(p => p
            .Add(c => c.SignInAvailable, true)
            .Add(c => c.OnConnect, () => connected = true));

        cut.FindAll("#atlassian-disconnect").Should().BeEmpty();
        cut.FindAll("#atlassian-display-name").Should().BeEmpty();
        cut.Find("#atlassian-connect").Click();
        connected.Should().BeTrue();
    }

    [Fact]
    public void Render_Linked_ShowsTheDisplayNameAndUnlinks()
    {
        bool disconnected = false;
        var link = new UserAtlassianIdentityLinkEntity
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), AccountId = "acc-1",
            DisplayName = "Ada Lovelace", ConnectedAt = DateTime.UtcNow,
        };

        var cut = ctx.Render<AtlassianIdentityCard>(p => p
            .Add(c => c.Link, link)
            .Add(c => c.SignInAvailable, false)
            .Add(c => c.OnDisconnect, () => disconnected = true));

        cut.Find("#atlassian-display-name").TextContent.Should().Be("Ada Lovelace");
        cut.FindAll("#atlassian-connect").Should().BeEmpty();
        cut.Find("#atlassian-disconnect").Click();
        cut.Find("#atlassian-disconnect-confirm").Click();
        disconnected.Should().BeTrue();
    }

    [Fact]
    public void Render_NoLinkApp_SaysAnAdministratorSetsItUp()
    {
        var cut = ctx.Render<AtlassianIdentityCard>(p => p.Add(c => c.SignInAvailable, false));

        cut.FindAll("#atlassian-connect").Should().BeEmpty();
        cut.Markup.Should().Contain("Providers → Atlassian");
    }
}
