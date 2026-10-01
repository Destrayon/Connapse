using System.Text.Json;
using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Components.Sources;
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
/// The New source dialog on an Atlassian site connection: the site's spaces are listed (personal
/// ones only when asked), and one source is created per space ticked, with the scope keys
/// <c>ConnectorFactory</c> reads.
/// </summary>
[Trait("Category", "Unit")]
public class NewSourceConfluenceTests : IDisposable
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";
    private const string OtherCloudId = "99999999-8888-7777-6666-555555555555";

    private readonly BunitContext ctx = new();
    private readonly StubSpacesHandler stub = new();
    private readonly ISourceStore sources = Substitute.For<ISourceStore>();
    private readonly IConnectionStore connections = Substitute.For<IConnectionStore>();
    private readonly List<CreateSourceRequest> created = [];
    private readonly Connection connection = new(
        Guid.NewGuid(), "acme.atlassian.net", ConnectionProvider.Atlassian,
        $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"client-1"}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);
    private readonly Connection otherConnection = new(
        Guid.NewGuid(), "globex.atlassian.net", ConnectionProvider.Atlassian,
        $$"""{"siteUrl":"https://globex.atlassian.net","cloudId":"{{OtherCloudId}}","clientId":"client-2"}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);

    public NewSourceConfluenceTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles("Admin");
        ctx.Services.AddLogging();

        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(stub, disposeHandler: false));

        connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Connection>>([connection, otherConnection]));
        connections.GetAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        connections.GetSecretAsync(connection.Id, Arg.Any<CancellationToken>()).Returns("the-secret");
        connections.GetAsync(otherConnection.Id, Arg.Any<CancellationToken>()).Returns(otherConnection);
        connections.GetSecretAsync(otherConnection.Id, Arg.Any<CancellationToken>()).Returns("the-other-secret");
        sources.ListByConnectionAsync(otherConnection.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Source>>([]));

        sources.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Source>>([]));
        sources.ListByConnectionAsync(connection.Id, Arg.Any<CancellationToken>())
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
        ctx.Services.AddSingleton(Substitute.For<IAuditLogger>());
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

    private IRenderedComponent<SourcesPage> OpenDialog()
    {
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/sources?new={connection.Id}");
        var cut = ctx.Render<SourcesPage>();
        cut.WaitForAssertion(() => cut.Find("#confluence-spaces"));
        return cut;
    }

    [Fact]
    public void Dialog_ListsTheSitesSpacesAndHidesPersonalOnesByDefault()
    {
        var cut = OpenDialog();

        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));
        cut.Markup.Should().Contain("Engineering").And.Contain("Handbook").And.NotContain("Pat's notes");
        cut.Find("#confluence-attachments").HasAttribute("checked").Should().BeTrue();
        cut.Find("#confluence-max-attachment-mb").GetAttribute("value").Should().Be("25");
    }

    [Fact]
    public void Dialog_IncludePersonalSpaces_ListsPersonalSpacesToo()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));

        cut.Find("#confluence-personal").Change(true);

        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(3));
        cut.Markup.Should().Contain("(~pat)");
    }

    [Fact]
    public void Create_TwoSpacesSelected_CreatesTwoSourcesWithTheScopeTheFactoryReads()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));

        cut.Find("#space-100").Change(true);
        cut.Find("#space-200").Change(true);
        cut.Find("#confluence-max-attachment-mb").Change("40");
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => created.Should().HaveCount(2));
        created.Select(r => r.Name).Should().BeEquivalentTo("acme.atlassian.net / Engineering", "acme.atlassian.net / Handbook");
        created.Should().OnlyContain(r => r.ConnectionId == connection.Id);

        using var doc = JsonDocument.Parse(created.Single(r => r.Name.EndsWith("Engineering")).ScopeJson);
        doc.RootElement.GetProperty("kind").GetString().Should().Be("confluence-space");
        doc.RootElement.GetProperty("spaceId").GetString().Should().Be("100");
        doc.RootElement.GetProperty("spaceKey").GetString().Should().Be("ENG");
        doc.RootElement.GetProperty("includeAttachments").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("maxAttachmentMb").GetInt32().Should().Be(40);
    }

    [Fact]
    public void Create_NothingSelected_CreatesNothingAndSaysSo()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));

        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Choose at least one space."));
        created.Should().BeEmpty();
    }

    [Fact]
    public void Dialog_SpaceThatAlreadyHasASource_IsNotOfferedAgain()
    {
        sources.ListByConnectionAsync(connection.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Source>>([new Source(
                Guid.NewGuid(), "existing", null, connection.Id,
                """{"kind":"confluence-space","spaceId":"100","spaceKey":"ENG"}""", DateTime.UtcNow, DateTime.UtcNow)]));

        var cut = OpenDialog();

        cut.WaitForAssertion(() => cut.Find("#space-100").HasAttribute("disabled").Should().BeTrue());
        cut.Find("#space-200").HasAttribute("disabled").Should().BeFalse();
        cut.Markup.Should().Contain("already added");
    }

    [Fact]
    public void Dialog_SwitchingSitesWhileTheFirstListIsLoading_NeverOffersTheFirstSitesSpaces()
    {
        var holdA = new TaskCompletionSource();
        stub.HoldSiteA = holdA;
        var cut = OpenDialog();

        cut.Find("#source-connection").Change(otherConnection.Id.ToString());
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(1));
        cut.Markup.Should().Contain("Globex Wiki");

        holdA.SetResult();
        Thread.Sleep(300);

        cut.FindAll(".confluence-space-option").Should().HaveCount(1);
        cut.Markup.Should().Contain("Globex Wiki").And.NotContain("Engineering").And.NotContain("Handbook");

        cut.Find("#space-700").Change(true);
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => created.Should().ContainSingle());
        created.Single().ConnectionId.Should().Be(otherConnection.Id);
        created.Single().Name.Should().Be("globex.atlassian.net / Globex Wiki");
    }

    [Fact]
    public void Create_SelectedSpaceIsNotOnTheConnection_IsRefusedAndTheRestAreCreated()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));
        cut.Find("#space-100").Change(true);
        cut.Find("#space-200").Change(true);

        // The site no longer has Engineering by the time the admin presses add.
        stub.SiteASpaces = """{"results":[{"id":"200","key":"HB","name":"Handbook","type":"global"}]}""";
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => created.Should().ContainSingle());
        created.Single().Name.Should().Be("acme.atlassian.net / Handbook");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("not spaces on this site: Engineering"));
    }

    [Fact]
    public void Create_OnlySelectedSpaceIsNotOnTheConnection_CreatesNothingAndSaysSo()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));
        cut.Find("#space-100").Change(true);

        stub.SiteASpaces = """{"results":[{"id":"200","key":"HB","name":"Handbook","type":"global"}]}""";
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Not spaces on this site: Engineering"));
        created.Should().BeEmpty();
    }

    [Fact]
    public void ToRequests_SpaceAlreadyASource_IsSkippedByIdAndReported()
    {
        var selection = new ConfluenceSpaceSelection
        {
            Available = [new ConfluenceSpaceInfo("100", "ENG", "Engineering", "global"), new ConfluenceSpaceInfo("200", "HB", "Handbook", "global")],
        };
        selection.Selected.UnionWith(["100", "200"]);

        var requests = selection.ToRequests(connection.Id, "acme.atlassian.net", new HashSet<string> { "100" }, out var skipped);

        requests.Should().ContainSingle().Which.Name.Should().Be("acme.atlassian.net / Handbook");
        skipped.Should().Equal("Engineering");
    }

    [Fact]
    public void ListSpaces_AtlassianRefusesTheAccount_IsUnavailableWithoutTheSecret()
    {
        stub.RejectToken = true;
        var sites = ctx.Services.GetRequiredService<AtlassianSiteService>();

        var result = sites.ListSpacesAsync(connection.Id, false).GetAwaiter().GetResult();

        result.Outcome.Should().Be(AtlassianSpacesOutcome.Unavailable);
        result.Error.Should().NotBeNullOrEmpty().And.NotContain("the-secret");
    }

    private sealed class StubSpacesHandler : HttpMessageHandler
    {
        public bool RejectToken { get; set; }

        /// <summary>Spaces for the first site; a test can change what the site has between calls.</summary>
        public string SiteASpaces { get; set; } = """
            {"results":[
              {"id":"100","key":"ENG","name":"Engineering","type":"global"},
              {"id":"200","key":"HB","name":"Handbook","type":"global"},
              {"id":"300","key":"~pat","name":"Pat's notes","type":"personal"}]}
            """;

        /// <summary>When set, the first site's space list is not answered until this completes.</summary>
        public TaskCompletionSource? HoldSiteA { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            string? json = null;

            if (path.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                if (RejectToken)
                    return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
                json = """{"access_token":"t","expires_in":3600}""";
            }
            else if (path.EndsWith("/api/v2/spaces", StringComparison.Ordinal))
            {
                if (path.Contains(OtherCloudId, StringComparison.Ordinal))
                {
                    json = """{"results":[{"id":"700","key":"GBL","name":"Globex Wiki","type":"global"}]}""";
                }
                else
                {
                    if (HoldSiteA is { } hold)
                        await hold.Task;
                    json = SiteASpaces;
                }
            }

            return json is null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
