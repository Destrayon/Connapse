using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Components.Providers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

[Trait("Category", "Unit")]
public class AtlassianProviderCardTests : IDisposable
{
    private const string Secret = "super-secret-value";

    private readonly BunitContext ctx = new();
    private readonly IProviderCredentialStore credentials = Substitute.For<IProviderCredentialStore>();
    private readonly IAuditLogger audit = Substitute.For<IAuditLogger>();

    public AtlassianProviderCardTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.AddAuthorization();
        ctx.Services.AddLogging();
        ctx.Services.AddSingleton(credentials);
        ctx.Services.AddSingleton(audit);
    }

    public void Dispose() => ctx.Dispose();

    [Fact]
    public void Render_ShowsOnlyTheLinkingApp_AndSendsSitesToTheConnectionsPage()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.FindAll("section.provider-step h2").Select(h => h.TextContent)
            .Should().Equal("Linking app");
        cut.FindAll("#atlassian-site-url").Should().BeEmpty();
        cut.Markup.Should().NotContain("read:page:confluence");
        cut.Find("#atlassian-sites-elsewhere a").GetAttribute("href").Should().Be("/connections?new=atlassian");
    }

    [Fact]
    public void LinkingApp_NotSetUp_ShowsTheConsoleLinkCallbackUrlAndScope()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        var step = cut.Find("#atlassian-app");
        step.TextContent.Should().Contain("Not set up")
            .And.Contain("User identity API").And.Contain("read:me")
            .And.Contain("Distribution").And.Contain("Sharing")
            .And.Contain("Manual values");
        step.InnerHtml.Should().Contain("https://developer.atlassian.com/console/myapps/");
        cut.Find("#atlassian-callback-url").TextContent.Should().Be("http://localhost/api/v1/auth/cloud/atlassian/callback");
        cut.FindAll("#atlassian-copy-callback").Should().ContainSingle();
    }

    [Fact]
    public void LinkingApp_Save_StoresTheAppAndNeverRendersTheSecret()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>())
            .Returns(null, new AtlassianLinkAppRegistration("link-client"));
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-client-id").Input("link-client");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-save").Click();

        cut.WaitForAssertion(() => cut.FindAll("#atlassian-app-saved").Should().ContainSingle());
        credentials.Received(1).SaveAtlassianLinkAppAsync("link-client", Secret, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        cut.Find("#atlassian-app").TextContent.Should().Contain("Saved, waiting for a first sign-in");
        cut.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public void LinkingApp_Saved_ShowsTheStoredClientIdButNotTheSecret()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>())
            .Returns(new AtlassianLinkAppRegistration("stored-client"));

        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-current").TextContent.Should().Contain("stored-client").And.Contain("stored encrypted");
        cut.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public void LinkingApp_Save_WritesTheCredentialSavedAuditWithoutTheSecret()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>())
            .Returns(null, new AtlassianLinkAppRegistration("link-client"));
        object? details = null;
        audit.When(a => a.LogAsync("provider.credential.saved", "provider", "atlassian", Arg.Any<object?>(), Arg.Any<CancellationToken>()))
            .Do(call => details = call.ArgAt<object?>(3));
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-client-id").Input("link-client");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-save").Click();

        cut.WaitForAssertion(() => details.Should().NotBeNull());
        System.Text.Json.JsonSerializer.Serialize(details).Should().Contain("link-client").And.NotContain(Secret);
    }

    [Fact]
    public void LinkingApp_Verified_IsReady()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>())
            .Returns(new AtlassianLinkAppRegistration("link-client", DateTime.UtcNow));

        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app").ClassList.Should().Contain("provider-step--satisfied");
        cut.Find("#atlassian-app").TextContent.Should().Contain("Ready");
    }
}
