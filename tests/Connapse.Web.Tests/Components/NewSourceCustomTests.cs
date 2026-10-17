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
/// The New source dialog's custom source: it sits on a custom connection, which holds the middle man's address
/// and token (or names a provider that does); the source only loads and picks a scope, and holds no token.
/// </summary>
[Trait("Category", "Unit")]
public class NewSourceCustomTests : IDisposable
{
    private readonly BunitContext ctx = new();
    private readonly FakeMiddleMan remote = new();
    private readonly ISourceStore sources = Substitute.For<ISourceStore>();
    private readonly IConnectionStore connections = Substitute.For<IConnectionStore>();
    private readonly List<CreateSourceRequest> created = [];
    private readonly List<Connection> connectionList = [];
    private readonly ICustomProviderStore customProviders = Substitute.For<ICustomProviderStore>();

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
            .Returns(_ => Task.FromResult<IReadOnlyList<Connection>>(connectionList.ToList()));
        sources.ListByConnectionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Source>>([]));
        ctx.Services.AddSingleton(customProviders);
        sources.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Source>>([]));
        sources.CreateAsync(Arg.Any<CreateSourceRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<CreateSourceRequest>();
                created.Add(request);
                return Task.FromResult(new Source(
                    Guid.NewGuid(), request.Name, null, request.ConnectionId, request.ScopeJson, DateTime.UtcNow, DateTime.UtcNow,
                    Provider: request.Provider, HasSecret: request.Secret is not null));
            });

        ctx.Services.AddSingleton(sources);
        ctx.Services.AddSingleton(connections);
        ctx.Services.AddSingleton(Substitute.For<IAuditLogger>());
        var customSettings = Substitute.For<IOptionsMonitor<CustomSourceSettings>>();
        customSettings.CurrentValue.Returns(new CustomSourceSettings());
        ctx.Services.AddSingleton(new CustomSourceSetup(http, customSettings, customProviders, connections));
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

    private Connection AddContentOnlyConnection(string token = FakeMiddleMan.Token)
    {
        Connection connection = new(Guid.NewGuid(), "Acme bridge", ConnectionProvider.Custom,
            CustomConnectionConfig.ForMiddleMan(FakeMiddleMan.BaseUrl), null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);
        connectionList.Add(connection);
        connections.GetAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        connections.GetSecretAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(token);
        return connection;
    }

    private Connection AddCustomConnection(string providerUrl = FakeMiddleMan.BaseUrl)
    {
        CustomProvider provider = new(Guid.NewGuid(), "Acme permissions", providerUrl, true, 100, true, null,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);
        customProviders.GetAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(provider);
        customProviders.GetTokenAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(FakeMiddleMan.Token);
        Connection connection = new(Guid.NewGuid(), "Acme checked", ConnectionProvider.Custom,
            CustomConnectionConfig.ToJson(provider.Id), null, DateTime.UtcNow, DateTime.UtcNow);
        connectionList.Add(connection);
        connections.GetAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        return connection;
    }

    private static void LoadScopes(IRenderedComponent<SourcesPage> cut)
    {
        cut.Find("#custom-source-load").Click();
        cut.WaitForAssertion(() => cut.Find("#custom-source-scope"));
    }

    [Fact]
    public void NewSource_WithNoConnections_IsDisabled()
    {
        var cut = ctx.Render<SourcesPage>();

        cut.WaitForAssertion(() => cut.FindAll("button.btn-primary").First(b => b.TextContent.Contains("New source"))
            .HasAttribute("disabled").Should().BeTrue());
        cut.Markup.Should().Contain("custom middle man");
    }

    [Fact]
    public void NewSource_ContentOnlyConnection_ShowsItsMiddleManAndSaysItIsContentOnly()
    {
        AddContentOnlyConnection();
        var cut = OpenDialog();

        cut.WaitForAssertion(() => cut.Find("#custom-source-middle-man").TextContent.Should().Contain(FakeMiddleMan.BaseUrl));
        cut.Find("#custom-source-content-only").TextContent
            .Should().Contain("Signed-in users with access to this source see all of its content");
        cut.FindAll("#custom-source-url, #custom-source-token").Should().BeEmpty("the connection holds the address and token");
    }

    [Fact]
    public void NewSource_ContentOnlyConnection_LoadsScopesWithItsTokenAndCreatesTheSourceWithoutOne()
    {
        Connection connection = AddContentOnlyConnection();
        var cut = OpenDialog();

        LoadScopes(cut);
        cut.Find("#custom-source-scope").TextContent.Should().Contain("Engineering");
        cut.Find("#custom-source-name").GetAttribute("value").Should().Be("Engineering");
        cut.FindAll("button").First(b => b.TextContent.Contains("Create source")).Click();

        cut.WaitForAssertion(() => created.Should().ContainSingle());
        CreateSourceRequest request = created.Single();
        request.ConnectionId.Should().Be(connection.Id);
        request.Provider.Should().BeNull();
        request.Secret.Should().BeNull("a custom source reads with its connection's token");
        request.Name.Should().Be("Engineering");
        CustomSourceScope.TryParse(request.ScopeJson).Should().Be(new CustomSourceScope(FakeMiddleMan.BaseUrl, FakeMiddleMan.Scope));
    }

    [Fact]
    public void NewSource_ConnectionWhoseTokenIsRefused_SaysToReplaceItOnTheConnection()
    {
        AddContentOnlyConnection(token: "wrong");
        var cut = OpenDialog();

        cut.Find("#custom-source-load").Click();

        cut.WaitForAssertion(() => cut.Find("#custom-source-error").TextContent
            .Should().Contain("Replace the token on the connection"));
        cut.FindAll("#custom-source-scope").Should().BeEmpty();
    }

    [Fact]
    public void NewSource_IntervalThatIsNotANumber_IsRefusedBesideTheField()
    {
        AddContentOnlyConnection();
        var cut = OpenDialog();
        LoadScopes(cut);

        cut.Find("#custom-source-interval").Input("five");
        cut.FindAll("button").First(b => b.TextContent.Contains("Create source")).Click();

        cut.WaitForAssertion(() => cut.Find("#custom-source-interval-error").TextContent.Should().Contain("at least 60"));
        cut.Find("#custom-source-interval").GetAttribute("aria-invalid").Should().Be("true");
        created.Should().BeEmpty();
    }

    [Fact]
    public void NewSource_CustomSourceCreatedBeforeScopesAreLoaded_IsRefused()
    {
        AddContentOnlyConnection();
        var cut = OpenDialog();

        cut.FindAll("button").First(b => b.TextContent.Contains("Create source")).Click();

        cut.WaitForAssertion(() => cut.Find("#custom-source-error").TextContent.Should().Contain("Load scopes"));
        created.Should().BeEmpty();
    }

    [Fact]
    public void NewSource_CustomSourceBeingSaved_LocksTheConnectionChoiceUntilItFinishes()
    {
        AddContentOnlyConnection();
        var cut = OpenDialog();
        LoadScopes(cut);

        remote.Hold = new TaskCompletionSource();
        cut.FindAll("button").First(b => b.TextContent.Contains("Create source")).Click();

        cut.WaitForAssertion(() => cut.Find("#source-connection").HasAttribute("disabled").Should().BeTrue());
        remote.Hold.SetResult();
        cut.WaitForAssertion(() => created.Should().ContainSingle());
    }

    [Fact]
    public void NewSource_ProviderConnection_SaysWhoChecksAndCreatesTheSourceOnIt()
    {
        AddContentOnlyConnection();
        Connection connection = AddCustomConnection();
        var cut = OpenDialog();

        cut.Find("#source-connection").Change(connection.Id.ToString());
        cut.WaitForAssertion(() => cut.Find("#custom-source-permissions").TextContent.Should().Contain("Acme permissions"));
        cut.FindAll("#custom-source-content-only").Should().BeEmpty();

        LoadScopes(cut);
        cut.FindAll("button").First(b => b.TextContent.Contains("Create source")).Click();

        cut.WaitForAssertion(() => created.Should().ContainSingle());
        created[0].ConnectionId.Should().Be(connection.Id);
        created[0].Secret.Should().BeNull();
        CustomSourceScope.TryParse(created[0].ScopeJson).Should().Be(new CustomSourceScope(FakeMiddleMan.BaseUrl, FakeMiddleMan.Scope));
    }

    [Fact]
    public void NewSource_ConnectionWhoseProviderIsGone_SaysSoAndCannotLoadScopes()
    {
        Connection connection = new(Guid.NewGuid(), "Orphan", ConnectionProvider.Custom,
            CustomConnectionConfig.ToJson(Guid.NewGuid()), null, DateTime.UtcNow, DateTime.UtcNow);
        connectionList.Add(connection);
        var cut = OpenDialog();

        cut.WaitForAssertion(() => cut.Find("#custom-source-error").TextContent.Should().Contain("no longer exists"));
        cut.Find("#custom-source-load").HasAttribute("disabled").Should().BeTrue();
    }
}
