using System.Net;
using System.Security.Claims;
using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using Connapse.Web.Components.Shared;
using Connapse.Web.Endpoints;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

/// <summary>A custom provider's row on the integrations page: live status from the middle man, Link and Unlink.</summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class CustomProviderLinkCardTests : IDisposable
{
    private static readonly Guid User = Guid.NewGuid();

    private readonly BunitContext ctx = new();
    private readonly FakeMiddleMan remote = new() { SupportsLinking = true };
    private readonly IAuditLogger audit = Substitute.For<IAuditLogger>();
    private readonly CustomProvider provider = new(
        Guid.NewGuid(), "Acme bridge", FakeMiddleMan.BaseUrl, true, 100, true, FakeMiddleMan.LinkUrl,
        DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, HasLinkSecret: true);

    public CustomProviderLinkCardTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var store = Substitute.For<ICustomProviderStore>();
        store.GetAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(provider);
        store.GetTokenAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(FakeMiddleMan.Token);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(remote, disposeHandler: false));
        var settings = Substitute.For<IOptionsMonitor<CustomSourceSettings>>();
        settings.CurrentValue.Returns(new CustomSourceSettings());

        var links = new CustomLinkService(store, http, settings, TimeProvider.System);
        ctx.Services.AddSingleton(links);
        ctx.Services.AddSingleton(audit);
        AddUnlink(links);
        ctx.AddAuthorization().SetAuthorized("user").SetClaims(new Claim(ClaimTypes.NameIdentifier, User.ToString()));
    }

    public void Dispose() => ctx.Dispose();

    /// <summary>The real links.unlink, over this test's middle man; the built-in providers it never reaches are stand-ins.</summary>
    private void AddUnlink(CustomLinkService links)
    {
        var authorizer = Substitute.For<Connapse.Application.Ports.IOperationAuthorizer>();
        authorizer.AllowsAsync(default!, default!).ReturnsForAnyArgs(true);
        var identityDb = Substitute.For<IDbContextFactory<ConnapseIdentityDbContext>>();
        ctx.Services.AddSingleton(new Connapse.Application.Links.UnlinkAccount(
            Substitute.For<IAwsIdentityLinkService>(), Substitute.For<IAzureIdentityLinkService>(),
            new GitHubIdentityLinkStore(identityDb, TimeProvider.System), new GitHubLinkFlow(new MemoryCache(new MemoryCacheOptions())),
            new AtlassianIdentityLinkStore(identityDb, TimeProvider.System), new AtlassianLinkFlow(new MemoryCache(new MemoryCacheOptions())),
            links, Substitute.For<IConnectorScopeCache>(), Substitute.For<ISourceStore>(), Substitute.For<IConnectionStore>(),
            audit, authorizer));
    }

    private IRenderedComponent<CustomProviderLinkCard> Render() =>
        ctx.Render<CustomProviderLinkCard>(p => p.Add(x => x.Provider, provider).Add(x => x.UserId, User));

    [Fact]
    public void LinkedUser_SeesTheAccountNameAndDisconnect()
    {
        remote.Links[User.ToString()] = "alice@acme";

        var cut = Render();

        cut.WaitForAssertion(() => cut.Find("[data-status=linked]").TextContent.Should().Contain("alice@acme"));
        cut.FindAll("[data-action=unlink]").Should().ContainSingle();
    }

    [Fact]
    public void UnlinkedUser_IsSentToTheConnectEndpoint()
    {
        var cut = Render();

        cut.WaitForAssertion(() => cut.Find("[data-status=unlinked]"));
        cut.Find("[data-action=link]").Click();

        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri
            .Should().EndWith($"/api/v1/auth/custom/{provider.Id}/connect");
    }

    [Fact]
    public void MiddleManNotAnswering_ShowsStatusUnknownNotUnlinked()
    {
        remote.FailNextWith = HttpStatusCode.InternalServerError;

        var cut = Render();

        cut.WaitForAssertion(() => cut.Find("[data-status=unknown]"));
        cut.FindAll("[data-action=link]").Should().BeEmpty();
        cut.FindAll("[data-action=retry]").Should().ContainSingle();
    }

    [Fact]
    public void CheckAgain_KeepsTheButtonOnThePageWhileItReloads()
    {
        remote.FailNextWith = HttpStatusCode.InternalServerError;
        var cut = Render();
        cut.WaitForAssertion(() => cut.Find("[data-action=retry]"));

        remote.Hold = new TaskCompletionSource();
        cut.Find("[data-action=retry]").Click();

        cut.Find("[data-action=retry]").HasAttribute("disabled").Should().BeTrue("the focused button stays, disabled, while the status reloads");
        remote.Hold.SetResult();
        cut.WaitForAssertion(() => cut.Find("[data-action=link]"));
    }

    [Fact]
    public void Disconnect_RemovesTheLinkAndRecordsIt()
    {
        remote.Links[User.ToString()] = "alice@acme";
        var cut = Render();
        cut.WaitForAssertion(() => cut.Find("[data-action=unlink]"));
        cut.Find("[data-action=unlink]").Click();

        cut.Find("[data-action=unlink-confirm]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-status=unlinked]"));
        remote.Links.Should().BeEmpty();
        audit.Received(1).LogAsync(CustomLinkEndpoints.UnlinkedEvent, "user", User.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void DisconnectFailing_KeepsTheLinkAndSaysSo()
    {
        remote.Links[User.ToString()] = "alice@acme";
        var cut = Render();
        cut.WaitForAssertion(() => cut.Find("[data-action=unlink]"));
        cut.Find("[data-action=unlink]").Click();
        remote.FailNextWith = HttpStatusCode.ServiceUnavailable;

        cut.Find("[data-action=unlink-confirm]").Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("did not remove the link"));
        remote.Links.Should().ContainKey(User.ToString());
        audit.DidNotReceive().LogAsync(CustomLinkEndpoints.UnlinkedEvent, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }
}
