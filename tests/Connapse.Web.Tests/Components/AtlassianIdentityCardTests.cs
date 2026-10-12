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
    public void Render_NoLink_ExplainsWhatConnapseReadsBeforeTheSignIn()
    {
        var cut = ctx.Render<AtlassianIdentityCard>(p => p.Add(c => c.SignInAvailable, true));

        cut.Find("#atlassian-consent-help").TextContent
            .Should().Contain("reads only your Atlassian account ID, to check what Confluence pages you can see");
        cut.Find("#atlassian-connect").GetAttribute("aria-describedby").Should().Be("atlassian-consent-help");
    }

    [Fact]
    public void Render_Linked_ShowsTheLinkDateAndUnlinks()
    {
        bool disconnected = false;
        var link = new UserAtlassianIdentityLinkEntity
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), AccountId = "acc-1",
            ConnectedAt = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc),
        };

        var cut = ctx.Render<AtlassianIdentityCard>(p => p
            .Add(c => c.Link, link)
            .Add(c => c.SignInAvailable, false)
            .Add(c => c.OnDisconnect, () => disconnected = true));

        cut.Find("#atlassian-linked").TextContent.Should().Contain("Linked to your Atlassian account").And.Contain("Oct 11, 2026");
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

    private static UserAtlassianIdentityLinkEntity Linked() => new()
    {
        Id = Guid.NewGuid(), UserId = Guid.NewGuid(), AccountId = "acc-1", ConnectedAt = DateTime.UtcNow,
    };

    [Fact]
    public void Disconnect_MovesFocusIntoTheConfirmation_AndCancelReturnsItToDisconnect()
    {
        var cut = ctx.Render<AtlassianIdentityCard>(p => p.Add(c => c.Link, Linked()).Add(c => c.SignInAvailable, true));

        cut.Find("#atlassian-disconnect").Click();
        ctx.LastFocused(cut)!.Id.Should().Be("atlassian-disconnect-confirm");

        cut.Find("#atlassian-disconnect-cancel").Click();
        ctx.LastFocused(cut)!.Id.Should().Be("atlassian-disconnect");
    }

    [Fact]
    public void ConfirmDisconnect_OnceTheLinkIsGone_MovesFocusToTheSignInButton()
    {
        IRenderedComponent<AtlassianIdentityCard>? cut = null;
        cut = ctx.Render<AtlassianIdentityCard>(p => p
            .Add(c => c.Link, Linked())
            .Add(c => c.SignInAvailable, true)
            .Add(c => c.OnDisconnect, () => cut!.Render(q => q.Add(c => c.Link, null))));

        cut.Find("#atlassian-disconnect").Click();
        cut.Find("#atlassian-disconnect-confirm").Click();

        cut.WaitForAssertion(() => ctx.LastFocused(cut)?.Id.Should().Be("atlassian-connect"));
    }

    [Fact]
    public void ConfirmDisconnect_WithSignInOff_MovesFocusToTheCard()
    {
        IRenderedComponent<AtlassianIdentityCard>? cut = null;
        cut = ctx.Render<AtlassianIdentityCard>(p => p
            .Add(c => c.Link, Linked())
            .Add(c => c.SignInAvailable, false)
            .Add(c => c.OnDisconnect, () => cut!.Render(q => q.Add(c => c.Link, null))));

        cut.Find("#atlassian-disconnect").Click();
        cut.Find("#atlassian-disconnect-confirm").Click();

        cut.WaitForAssertion(() => ctx.LastFocused(cut)?.Id.Should().Be("atlassian-identity-card"));
        cut.Find("#atlassian-identity-card").GetAttribute("tabindex").Should().Be("-1");
    }
}
