using System.Text.Json;
using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;
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
/// The New source dialog on an Atlassian connection: it shows the picker of the connection's own
/// product, with no product to choose, and one source is created per project ticked, with the
/// scope keys <c>ConnectorFactory</c> reads.
/// </summary>
[Trait("Category", "Unit")]
public class JiraProjectFormTests : IDisposable
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";

    private readonly BunitContext ctx = new();
    private readonly StubHandler stub = new();
    private readonly ISourceStore sources = Substitute.For<ISourceStore>();
    private readonly IConnectionStore connections = Substitute.For<IConnectionStore>();
    private readonly List<CreateSourceRequest> created = [];
    private readonly List<(string Action, string Details)> events = [];

    /// <summary>The setup events written so far, in order.</summary>
    private List<(string Action, string Details)> SetupEvents()
    {
        lock (events) return events.Where(e => e.Action.StartsWith("source.setup_", StringComparison.Ordinal)).ToList();
    }

    private static string Detail(string details, string name) => JsonDocument.Parse(details).RootElement.GetProperty(name).ToString();

    /// <summary>A site's connection for one product; <c>null</c> writes no product, as a connection saved before Jira did.</summary>
    private static Connection Site(string name, string? product, string host = "acme.atlassian.net") => new(
        Guid.NewGuid(), name, ConnectionProvider.Atlassian,
        product is null
            ? $$"""{"siteUrl":"https://{{host}}","cloudId":"{{CloudId}}","clientId":"client-1"}"""
            : $$"""{"siteUrl":"https://{{host}}","cloudId":"{{CloudId}}","clientId":"client-1","product":"{{product}}"}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true,
        Health: new ConnectionHealth(DateTime.UtcNow, Succeeded: true, Step: null, Message: null, Origin: "test",
            ConfirmedScopes: [AtlassianSiteScopes.JiraAttachment]));

    // One site, added twice: once as each product's connection.
    private readonly Connection confluence = Site("acme.atlassian.net", "confluence");
    private readonly Connection jira = Site("acme.atlassian.net (Jira)", "jira");

    /// <summary>A connection saved before connections named their product: a Confluence one.</summary>
    private readonly Connection legacy = Site("legacy.atlassian.net", null, host: "legacy.atlassian.net");

    public JiraProjectFormTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles("Admin");
        ctx.Services.AddLogging();
        ctx.Services.AddSourceOperations();

        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(stub, disposeHandler: false));

        connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Connection>>([confluence, jira, legacy]));
        foreach (var site in new[] { confluence, jira, legacy })
        {
            connections.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
            connections.GetSecretAsync(site.Id, Arg.Any<CancellationToken>()).Returns("the-secret");
            sources.ListByConnectionAsync(site.Id, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Source>>([]));
        }

        sources.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Source>>([]));
        sources.CreateAsync(Arg.Any<CreateSourceRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<CreateSourceRequest>();
                created.Add(request);
                return Task.FromResult(new Source(
                    Guid.NewGuid(), request.Name, null, request.ConnectionId, request.ScopeJson,
                    DateTime.UtcNow, DateTime.UtcNow));
            });

        var tokens = new AtlassianTokenSource(http, TimeProvider.System);
        ctx.Services.AddSingleton(sources);
        ctx.Services.AddSingleton(connections);
        var audit = Substitute.For<IAuditLogger>();
        audit.LogAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(c =>
            {
                lock (events) events.Add((c.ArgAt<string>(0), JsonSerializer.Serialize(c.ArgAt<object?>(3))));
            });
        ctx.Services.AddSingleton(audit);
        ctx.Services.AddSingleton(NSubstitute.Substitute.For<Connapse.Core.Interfaces.ICustomProviderStore>());
        ctx.Services.AddSingleton(new Connapse.Web.Services.CustomSourceSetup(
            NSubstitute.Substitute.For<IHttpClientFactory>(),
            NSubstitute.Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<Connapse.Core.CustomSourceSettings>>(),
            NSubstitute.Substitute.For<Connapse.Core.Interfaces.ICustomProviderStore>(),
            NSubstitute.Substitute.For<Connapse.Core.Interfaces.IConnectionStore>()));
        ctx.Services.AddSingleton(new AtlassianSiteService(
            http, new AtlassianConnectionTester(http), connections, Substitute.For<IAuditLogger>(), tokens));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Success()));
        ctx.Services.AddSingleton(new PrivateSourceVisibility(Substitute.For<ISearchScopeResolver>(), authorization, connections));
        ctx.Services.AddSingleton(Substitute.For<SourceScopePreflight>(Substitute.For<IOptionsMonitor<SourceSecuritySettings>>()));
        ctx.Services.AddSingleton(new SourceSyncService(
            Substitute.For<IServiceScopeFactory>(), Substitute.For<IConnectorFactory>(),
            Substitute.For<IIngestionQueue>(), NullLogger<SourceSyncService>.Instance));
        var app = new Connapse.Storage.Connectors.GitHub.ConnapseGitHubApp(
            Substitute.For<IServiceScopeFactory>(), http,
            NullLogger<Connapse.Storage.Connectors.GitHub.ConnapseGitHubApp>.Instance);
        ctx.Services.AddSingleton(app);
        ctx.Services.AddSingleton(new Connapse.Storage.Connectors.GitHub.GitHubRepositoryLookup(
            new Connapse.Storage.Connectors.GitHub.GitHubCredentialPool(app, Substitute.For<IServiceScopeFactory>()), http));
        ctx.Services.AddSingleton(new Connapse.Storage.Documents.DocumentCoordinateReport(
            Substitute.For<Microsoft.EntityFrameworkCore.IDbContextFactory<Connapse.Storage.Data.KnowledgeDbContext>>()));
    }

    public void Dispose() => ctx.Dispose();

    private IRenderedComponent<SourcesPage> OpenDialog(Connection site)
    {
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/sources?new={site.Id}");
        var cut = ctx.Render<SourcesPage>();
        cut.WaitForAssertion(() => cut.Find("#new-source-dialog"));
        return cut;
    }

    [Fact]
    public void NewSource_ConfluenceConnection_ShowsSpacesWithoutAProductChoice()
    {
        var cut = OpenDialog(confluence);

        cut.WaitForAssertion(() => cut.Find("#confluence-spaces"));
        cut.FindAll("#atlassian-product").Should().BeEmpty("a connection reads one product (#725)");
        cut.FindAll("input[name='atlassian-product']").Should().BeEmpty();
        cut.FindAll("#jira-projects").Should().BeEmpty();
    }

    [Fact]
    public void NewSource_JiraConnectionWhoseLastTestFailed_StillShowsTheProjectPicker()
    {
        var cut = OpenDialog(jira with
        {
            Health = new ConnectionHealth(DateTime.UtcNow, Succeeded: false, Step: "jira-admin", Message: "m", Origin: "test"),
        });

        cut.WaitForAssertion(() => cut.Find("#jira-projects"));
        cut.FindAll("#confluence-spaces").Should().BeEmpty("a Jira connection is never offered Confluence spaces");
    }

    [Fact]
    public void NewSource_JiraConnection_ShowsProjectPickerWithoutAProductChoice()
    {
        var cut = OpenDialog(jira);

        cut.WaitForAssertion(() => cut.FindAll(".jira-project-option").Should().HaveCount(2));
        cut.Markup.Should().Contain("Engineering").And.Contain("(ENG)").And.Contain("Operations");
        cut.FindAll("#atlassian-product").Should().BeEmpty("a connection reads one product (#725)");
        cut.FindAll("#confluence-spaces").Should().BeEmpty();
        cut.Find("#jira-attachments").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void NewSource_ConnectionList_NamesEachConnectionsProduct()
    {
        var cut = OpenDialog(confluence);

        var options = cut.FindAll("#source-connection option").Select(o => o.TextContent.Trim()).ToList();
        options.Should().Contain("Confluence · acme.atlassian.net")
            .And.Contain("Jira · acme.atlassian.net", "the product is named once, then the site's host")
            .And.Contain("Confluence · legacy.atlassian.net", "a connection without a product is a Confluence one");
    }

    [Fact]
    public void NewSource_SwitchingToTheSitesJiraConnection_ShowsItsProjects_AndStartsOneJiraSetup()
    {
        var cut = OpenDialog(confluence);
        cut.WaitForAssertion(() => cut.Find("#confluence-spaces"));

        cut.Find("#source-connection").Change(jira.Id.ToString());

        cut.WaitForAssertion(() => cut.FindAll(".jira-project-option").Should().HaveCount(2));
        cut.FindAll("#confluence-spaces").Should().BeEmpty();
        var started = SetupEvents().Where(e => e.Action == AtlassianSourceSetupEvents.Started).ToList();
        started.Select(e => Detail(e.Details, "Product")).Should().Equal(["confluence", "jira"],
            "each setup takes its product from the connection it opened on");
        started.Select(e => Detail(e.Details, "Attempt")).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void JiraForm_CreatesOneSourcePerProject()
    {
        var cut = OpenDialog(jira);
        cut.WaitForAssertion(() => cut.FindAll(".jira-project-option").Should().HaveCount(2));

        cut.Find("#project-10000").Change(true);
        cut.Find("#project-10001").Change(true);
        cut.Find("#jira-max-attachment-mb").Change("40");
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => created.Should().HaveCount(2));
        created.Select(r => r.Name).Should().BeEquivalentTo("acme.atlassian.net / Engineering (Jira)", "acme.atlassian.net / Operations (Jira)");
        created.Should().OnlyContain(r => r.ConnectionId == jira.Id);

        using var doc = JsonDocument.Parse(created.Single(r => r.Name.Contains("Engineering")).ScopeJson);
        doc.RootElement.GetProperty("kind").GetString().Should().Be("jira-project");
        doc.RootElement.GetProperty("projectId").GetString().Should().Be("10000");
        doc.RootElement.GetProperty("projectKey").GetString().Should().Be("ENG");
        doc.RootElement.GetProperty("includeAttachments").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("maxAttachmentMb").GetInt32().Should().Be(40);
    }

    [Fact]
    public void SourceName_LongJiraProject_KeepsTheJiraSuffix_AndConfluenceNamesAreUnchanged()
    {
        string longName = new('x', 200);

        Connapse.Web.Components.Sources.AtlassianSourceSelection.SourceName("acme.atlassian.net", longName,
                Connapse.Web.Components.Sources.JiraProjectSelection.SourceNameSuffix)
            .Should().HaveLength(128).And.EndWith("… (Jira)");
        Connapse.Web.Components.Sources.AtlassianSourceSelection.SourceName("acme.atlassian.net", longName)
            .Should().HaveLength(128).And.EndWith("x…");
        Connapse.Web.Components.Sources.AtlassianSourceSelection.SourceName("acme.atlassian.net", "Wiki")
            .Should().Be("acme.atlassian.net / Wiki");
    }

    [Fact]
    public void Create_ConnectionCantBeReadAgain_SaysSoInTheDialog_AndCreatesNothing()
    {
        var cut = OpenDialog(jira);
        cut.WaitForAssertion(() => cut.FindAll(".jira-project-option").Should().HaveCount(2));
        connections.GetAsync(jira.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Connection?>(new InvalidOperationException("database down")));

        cut.Find("#project-10000").Change(true);
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Find("#jira-projects").TextContent.Should().Contain(
            new Connapse.Web.Components.Sources.JiraProjectSelection().CouldNotCheckMessage));
        cut.Markup.Should().NotContain("database down");
        created.Should().BeEmpty();
        Detail(SetupEvents().Single(e => e.Action == AtlassianSourceSetupEvents.StepFailed).Details, "Reason").Should().Be("unavailable");
    }

    [Fact]
    public void Create_ConnectionStoredForTheOtherProduct_IsRefusedAndCreatesNothing()
    {
        var cut = OpenDialog(jira);
        cut.WaitForAssertion(() => cut.FindAll(".jira-project-option").Should().HaveCount(2));
        // The dialog opened on a Jira connection, but the stored connection now reads Confluence.
        connections.GetAsync(jira.Id, Arg.Any<CancellationToken>())
            .Returns(jira with { ConfigJson = confluence.ConfigJson });

        cut.Find("#project-10000").Change(true);
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Find("#jira-projects").TextContent.Should().Contain(
            "Connection 'acme.atlassian.net (Jira)' is a Confluence connection, so it can't hold a Jira source."));
        created.Should().BeEmpty();
        var failed = SetupEvents().Single(e => e.Action == AtlassianSourceSetupEvents.StepFailed).Details;
        Detail(failed, "Reason").Should().Be("wrong_product");
    }

    [Fact]
    public void Create_JiraReadinessLostAfterThePickerOpened_IsRefusedWithTheApiMessage()
    {
        var cut = OpenDialog(jira);
        cut.WaitForAssertion(() => cut.FindAll(".jira-project-option").Should().HaveCount(2));
        // Tested again since the dialog opened, and the Jira probe failed this time.
        connections.GetAsync(jira.Id, Arg.Any<CancellationToken>())
            .Returns(jira with { Health = new ConnectionHealth(DateTime.UtcNow, Succeeded: false, Step: "jira-admin", Message: "m", Origin: "test") });

        cut.Find("#project-10000").Change(true);
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Find("#jira-projects").TextContent.Should().Contain(JiraSourceScope.JiraNotReady));
        created.Should().BeEmpty();
        Detail(SetupEvents().Single(e => e.Action == AtlassianSourceSetupEvents.StepFailed).Details, "Reason").Should().Be("not_ready");
    }

    [Fact]
    public void NewSource_ConnectionWithoutAProduct_OffersConfluenceOnly()
    {
        var cut = OpenDialog(legacy);

        cut.WaitForAssertion(() => cut.Find("#confluence-spaces"));
        cut.FindAll("#jira-projects").Should().BeEmpty("a connection saved before #725 is a Confluence connection");
    }

    [Fact]
    public void Funnel_JiraConnection_IsOneJiraAttemptFromStartToCompletion()
    {
        var cut = OpenDialog(jira);
        cut.WaitForAssertion(() => cut.FindAll(".jira-project-option").Should().HaveCount(2));

        cut.Find("#project-10000").Change(true);
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();
        cut.WaitForAssertion(() => SetupEvents().Select(e => e.Action).Should().Contain(AtlassianSourceSetupEvents.Completed));

        var setup = SetupEvents();
        setup.Select(e => e.Action).Should().Equal(
            AtlassianSourceSetupEvents.Started, AtlassianSourceSetupEvents.ProjectsListed, AtlassianSourceSetupEvents.Completed);
        setup.Should().OnlyContain(e => Detail(e.Details, "Product") == "jira", "a Jira setup is not counted as a Confluence one");
        setup.Select(e => Detail(e.Details, "Attempt")).Distinct().Should().ContainSingle();
        Detail(setup[1].Details, "Count").Should().Be("2");
        Detail(setup[2].Details, "Created").Should().Be("1");
    }

    [Fact]
    public void Funnel_ProjectNotChosen_RecordsTheFailedCreateStep()
    {
        var cut = OpenDialog(jira);
        cut.WaitForAssertion(() => cut.FindAll(".jira-project-option").Should().HaveCount(2));

        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => SetupEvents().Select(e => e.Action).Should().Contain(AtlassianSourceSetupEvents.StepFailed));
        var failed = SetupEvents().Single(e => e.Action == AtlassianSourceSetupEvents.StepFailed).Details;
        Detail(failed, "Step").Should().Be(AtlassianSourceSetupEvents.CreateStep);
        Detail(failed, "Reason").Should().Be("invalid");
        Detail(failed, "Product").Should().Be("jira");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            string? json = null;

            if (path.EndsWith("/oauth/token", StringComparison.Ordinal))
                json = """{"access_token":"t","expires_in":3600}""";
            else if (path.EndsWith("/api/v2/spaces", StringComparison.Ordinal))
                json = """{"results":[{"id":"100","key":"WIKI","name":"Wiki","type":"global"}]}""";
            else if (path.EndsWith("/rest/api/3/project/search", StringComparison.Ordinal))
                json = """
                    {"values":[
                      {"id":"10000","key":"ENG","name":"Engineering","projectTypeKey":"software","style":"classic"},
                      {"id":"10001","key":"OPS","name":"Operations","projectTypeKey":"service_desk","style":"next-gen"}],
                     "startAt":0,"maxResults":100,"total":2,"isLast":true}
                    """;

            return Task.FromResult(json is null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
