using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SourcesPage = Connapse.Web.Components.Pages.Sources;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The New source dialog's content-only custom source: no connection, a middle man's address and token,
/// scopes loaded from the middle man, and a source created with its token.
/// </summary>
[Trait("Category", "Unit")]
public class NewSourceCustomTests : IDisposable
{
    private readonly BunitContext ctx = new();
    private readonly FakeMiddleMan remote = new();
    private readonly ISourceStore sources = Substitute.For<ISourceStore>();
    private readonly IConnectionStore connections = Substitute.For<IConnectionStore>();
    private readonly List<CreateSourceRequest> created = [];

    public NewSourceCustomTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles("Admin");
        ctx.Services.AddLogging();

        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(remote, disposeHandler: false));

        connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Connection>>([]));
        sources.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Source>>([]));
        sources.CreateAsync(Arg.Any<CreateSourceRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<CreateSourceRequest>();
                created.Add(request);
                return Task.FromResult(new Source(
                    Guid.NewGuid(), request.Name, null, null, request.ScopeJson, DateTime.UtcNow, DateTime.UtcNow,
                    Provider: request.Provider, HasSecret: request.Secret is not null));
            });

        ctx.Services.AddSingleton(sources);
        ctx.Services.AddSingleton(connections);
        ctx.Services.AddSingleton(Substitute.For<IAuditLogger>());
        var customSettings = Substitute.For<IOptionsMonitor<CustomSourceSettings>>();
        customSettings.CurrentValue.Returns(new CustomSourceSettings());
        ctx.Services.AddSingleton(new CustomSourceSetup(http, customSettings));
        ctx.Services.AddSingleton(new AtlassianSiteService(
            http, new Connapse.Storage.ConnectionTesters.AtlassianConnectionTester(http), connections,
            Substitute.For<IAuditLogger>(), new Connapse.Storage.Connectors.Atlassian.AtlassianTokenSource(http, TimeProvider.System)));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Success()));
        ctx.Services.AddSingleton(new PrivateSourceVisibility(Substitute.For<ISearchScopeResolver>(), authorization, connections));
        ctx.Services.AddSingleton(Substitute.For<SourceScopePreflight>(Substitute.For<IOptionsMonitor<SourceSecuritySettings>>()));
        ctx.Services.AddSingleton(new SourceSyncService(
            Substitute.For<IServiceScopeFactory>(), Substitute.For<IConnectorFactory>(),
            Substitute.For<IIngestionQueue>(), NullLogger<SourceSyncService>.Instance));
        var app = new Connapse.Storage.Connectors.GitHub.ConnapseGitHubApp(
            Substitute.For<IServiceScopeFactory>(), http, NullLogger<Connapse.Storage.Connectors.GitHub.ConnapseGitHubApp>.Instance);
        ctx.Services.AddSingleton(app);
        ctx.Services.AddSingleton(new Connapse.Storage.Connectors.GitHub.GitHubRepositoryLookup(
            new Connapse.Storage.Connectors.GitHub.GitHubCredentialPool(app, Substitute.For<IServiceScopeFactory>()), http));
        ctx.Services.AddSingleton(new Connapse.Storage.Documents.DocumentCoordinateReport(
            Substitute.For<Microsoft.EntityFrameworkCore.IDbContextFactory<Connapse.Storage.Data.KnowledgeDbContext>>()));
    }

    public void Dispose() => ctx.Dispose();

    private IRenderedComponent<SourcesPage> OpenDialog()
    {
        var cut = ctx.Render<SourcesPage>();
        cut.WaitForAssertion(() => cut.Find("button.btn-primary").TextContent.Should().Contain("New source"));
        cut.FindAll("button.btn-primary").First(b => b.TextContent.Contains("New source")).Click();
        cut.WaitForAssertion(() => cut.Find("#new-source-dialog"));
        return cut;
    }

    [Fact]
    public void NewSource_WithNoConnections_OpensOnACustomSourceAndSaysItIsContentOnly()
    {
        var cut = OpenDialog();

        cut.Find("#source-connection").GetAttribute("value").Should().Be("custom");
        cut.Find("#custom-source-content-only").TextContent
            .Should().Contain("Signed-in users with access to this source see all of its content");
    }

    [Fact]
    public void NewSource_CustomSource_LoadsScopesAndCreatesTheSourceWithItsToken()
    {
        var cut = OpenDialog();

        cut.Find("#custom-source-url").Change(FakeMiddleMan.BaseUrl + "/");
        cut.Find("#custom-source-token").Change(FakeMiddleMan.Token);
        cut.Find("#custom-source-load").Click();
        cut.WaitForAssertion(() => cut.Find("#custom-source-scope").TextContent.Should().Contain("Engineering"));
        cut.Find("#custom-source-name").GetAttribute("value").Should().Be("Engineering");

        cut.FindAll("button").First(b => b.TextContent.Contains("Create source")).Click();

        cut.WaitForAssertion(() => created.Should().ContainSingle());
        CreateSourceRequest request = created.Single();
        request.Provider.Should().Be(ConnectionProvider.Custom);
        request.ConnectionId.Should().BeNull();
        request.Secret.Should().Be(FakeMiddleMan.Token);
        request.Name.Should().Be("Engineering");
        CustomSourceScope.TryParse(request.ScopeJson).Should().Be(new CustomSourceScope(FakeMiddleMan.BaseUrl, FakeMiddleMan.Scope));
    }

    [Fact]
    public void NewSource_CustomSourceWithAWrongToken_SaysSoAtTheTokenField()
    {
        var cut = OpenDialog();

        cut.Find("#custom-source-url").Change(FakeMiddleMan.BaseUrl);
        cut.Find("#custom-source-token").Change("wrong");
        cut.Find("#custom-source-load").Click();

        cut.WaitForAssertion(() => cut.Find("#custom-source-token-error").TextContent.Should().Contain("refused the token"));
        cut.FindAll("#custom-source-scope").Should().BeEmpty();
    }

    [Fact]
    public void NewSource_CustomSourceCreatedBeforeScopesAreLoaded_IsRefused()
    {
        var cut = OpenDialog();

        cut.FindAll("button").First(b => b.TextContent.Contains("Create source")).Click();

        cut.WaitForAssertion(() => cut.Find("#new-source-dialog .alert-danger").TextContent.Should().Contain("Load scopes"));
        created.Should().BeEmpty();
    }

    [Fact]
    public void NewSource_CustomSourceBeingSaved_LocksTheConnectionChoiceUntilItFinishes()
    {
        var cut = OpenDialog();
        cut.Find("#custom-source-url").Change(FakeMiddleMan.BaseUrl);
        cut.Find("#custom-source-token").Change(FakeMiddleMan.Token);
        cut.Find("#custom-source-load").Click();
        cut.WaitForAssertion(() => cut.Find("#custom-source-scope"));

        remote.Hold = new TaskCompletionSource();
        cut.FindAll("button").First(b => b.TextContent.Contains("Create source")).Click();

        cut.WaitForAssertion(() => cut.Find("#source-connection").HasAttribute("disabled").Should().BeTrue());
        remote.Hold.SetResult();
        cut.WaitForAssertion(() => created.Should().ContainSingle());
    }
}
