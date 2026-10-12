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
    private readonly IAuditLogger audit = Substitute.For<IAuditLogger>();
    private readonly List<(string Action, string? ResourceId, string Details)> events = [];

    /// <summary>The setup events written so far, in order.</summary>
    private List<(string Action, string? ResourceId, string Details)> SetupEvents()
    {
        lock (events) return events.Where(e => e.Action.StartsWith("source.setup_", StringComparison.Ordinal)).ToList();
    }

    private static JsonElement Detail(string details, string name) => JsonDocument.Parse(details).RootElement.GetProperty(name).Clone();
    private readonly Connection connection = new(
        Guid.NewGuid(), "acme.atlassian.net", ConnectionProvider.Atlassian,
        $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"client-1"}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true,
        Health: new ConnectionHealth(DateTime.UtcNow, Succeeded: true, Step: null, Message: null, Origin: "test",
            ConfirmedScopes: ["read:space:confluence", "read:page:confluence", ConfluenceSpaceSelection.AttachmentScope]));

    /// <summary>A site whose last test confirmed no attachment scope (it has never been tested).</summary>
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
        audit.LogAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(c =>
            {
                lock (events) events.Add((c.ArgAt<string>(0), c.ArgAt<string?>(2), JsonSerializer.Serialize(c.ArgAt<object?>(3))));
            });
        ctx.Services.AddSingleton(audit);
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
    public void Funnel_OpenListCreate_IsOneAttemptWithCountsAndDurations()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));

        cut.Find("#space-100").Change(true);
        cut.Find("#space-200").Change(true);
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();
        cut.WaitForAssertion(() => SetupEvents().Select(e => e.Action).Should().Contain(ConfluenceSourceSetupEvents.Completed));

        var setup = SetupEvents();
        setup.Select(e => e.Action).Should().StartWith([ConfluenceSourceSetupEvents.Started, ConfluenceSourceSetupEvents.SpacesListed])
            .And.EndWith(ConfluenceSourceSetupEvents.Completed);
        setup.Should().OnlyContain(e => e.ResourceId == connection.Id.ToString());
        setup.Select(e => Detail(e.Details, "Attempt").GetString()).Distinct().Should().ContainSingle(
            "every event of one dialog shares its attempt, so drop-off can be counted");

        var listed = setup.First(e => e.Action == ConfluenceSourceSetupEvents.SpacesListed).Details;
        Detail(listed, "Count").GetInt32().Should().Be(2);
        Detail(listed, "DurationMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        var completed = setup.Last().Details;
        Detail(completed, "Created").GetInt32().Should().Be(2);
        Detail(completed, "TotalMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        // Counts and ids only: no space names, keys or secrets.
        setup.Should().OnlyContain(e => !e.Details.Contains("Engineering") && !e.Details.Contains("ENG") && !e.Details.Contains("the-secret"));
    }

    [Fact]
    public void Funnel_CreateWithNothingChosen_RecordsTheFailedStep()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));

        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => SetupEvents().Select(e => e.Action).Should().Contain(ConfluenceSourceSetupEvents.StepFailed));
        var failed = SetupEvents().Single(e => e.Action == ConfluenceSourceSetupEvents.StepFailed).Details;
        Detail(failed, "Step").GetString().Should().Be(ConfluenceSourceSetupEvents.CreateStep);
        Detail(failed, "Reason").GetString().Should().Be("invalid");
        SetupEvents().Select(e => e.Action).Should().NotContain(ConfluenceSourceSetupEvents.Completed);
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
    public void Create_NothingSelected_ShowsTheErrorInsideTheSpacesGroupNotAtTheTop()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));

        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Find("#confluence-spaces #confluence-spaces-error").TextContent
            .Should().Contain(ConfluenceSpaceSelection.NoSpaceChosen));
        cut.Find("#confluence-spaces").GetAttribute("aria-describedby").Should().Contain("confluence-spaces-error");
        cut.FindAll(".confluence-space-option").Should().OnlyContain(e => e.GetAttribute("aria-invalid") == "true");
        cut.FindAll(".modal-body > .alert-danger").Should().BeEmpty();

        cut.Find("#space-100").Change(true);

        cut.FindAll("#confluence-spaces-error").Should().BeEmpty();
        cut.Find("#space-100").HasAttribute("aria-invalid").Should().BeFalse();
    }

    [Fact]
    public void Dialog_PutsTheSetupGuideLinkInItsTitleRow_AsEveryProviderSurfaceDoes()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));

        var guide = cut.Find("#confluence-setup-guide");
        guide.GetAttribute("href").Should().Be(
            "https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md#4-confluence-sources");
        guide.ParentElement!.ClassList.Should().Contain("modal-header", "the link sits in the dialog's title row");
        guide.PreviousElementSibling!.Id.Should().Be("new-source-title");
    }

    [Fact]
    public void Dialog_GroupsAreFieldsetsWithLegendsAndHelpIsLinkedToItsControl()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));

        var spaces = cut.Find("fieldset#confluence-spaces");
        spaces.QuerySelector("legend")!.TextContent.Trim().Should().Be("Spaces");
        spaces.GetAttribute("aria-describedby").Should().Be("confluence-spaces-help");
        cut.Find("#confluence-spaces-help").TextContent.Trim().Should().Be(
            "Each space you tick becomes its own source. If a space is missing, give the service account the Viewer role in it.");
        cut.FindAll("#confluence-spaces-help a").Should().BeEmpty("the guide link sits at the top of the Confluence part");

        cut.Find("fieldset#confluence-attachments-group legend").TextContent.Trim().Should().Be("Attachments");
        cut.Find("#confluence-personal").GetAttribute("aria-describedby").Should().Be("confluence-personal-help");
        cut.Find("#confluence-attachments").GetAttribute("aria-describedby").Should().Be("confluence-attachments-help");

        var size = cut.Find("#confluence-max-attachment-mb");
        size.GetAttribute("min").Should().Be("1");
        size.GetAttribute("max").Should().Be("100");
        size.GetAttribute("aria-describedby").Should().Be("confluence-max-attachment-mb-help");
        cut.Find("#confluence-max-attachment-mb-help").TextContent.Should().Contain("1 to 100 MB");
    }

    [Fact]
    public void MaxAttachmentSize_OutOfRange_IsFlaggedBesideTheFieldBeforeSubmitAndBlocksCreate()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));
        cut.Find("#space-100").Change(true);

        cut.Find("#confluence-max-attachment-mb").Change("500");

        var size = cut.Find("#confluence-max-attachment-mb");
        size.GetAttribute("aria-invalid").Should().Be("true");
        size.ClassList.Should().Contain("is-invalid");
        size.GetAttribute("aria-describedby").Should().Contain("confluence-max-attachment-mb-error");
        cut.Find("#confluence-max-attachment-mb-error").TextContent.Should().Contain("between 1 and 100 MB");

        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();
        created.Should().BeEmpty();

        // Turning attachments off makes the size irrelevant, so the error goes.
        cut.Find("#confluence-attachments").Change(false);
        cut.FindAll("#confluence-max-attachment-mb-error").Should().BeEmpty();
    }

    [Fact]
    public void Create_OnlySelectedSpaceIsNotOnTheConnection_ShowsTheErrorBesideTheSpaces()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));
        cut.Find("#space-100").Change(true);

        stub.SiteASpaces = """{"results":[{"id":"200","key":"HB","name":"Handbook","type":"global"}]}""";
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Find("#confluence-spaces-error").TextContent.Should().Contain("Not spaces on this site: Engineering"));
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
    public void Dialog_SiteThatConfirmedTheAttachmentScope_StartsWithAttachmentsOnAndNoScopeNote()
    {
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));

        cut.Find("#confluence-attachments").HasAttribute("checked").Should().BeTrue();
        cut.FindAll("#confluence-attachments-scope").Should().BeEmpty();
    }

    [Fact]
    public void Dialog_SiteWithoutAConfirmedAttachmentScope_StartsWithAttachmentsOffAndSaysWhy()
    {
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/sources?new={otherConnection.Id}");
        var cut = ctx.Render<SourcesPage>();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(1));

        cut.Find("#confluence-attachments").HasAttribute("checked").Should().BeFalse();
        cut.Find("#confluence-max-attachment-mb").HasAttribute("disabled").Should().BeTrue();
        var note = cut.Find("#confluence-attachments-scope");
        note.TextContent.Should().Contain("read:attachment:confluence");
        note.QuerySelector("a")!.GetAttribute("href").Should().Be(
            "https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md#missing-scope");
        cut.Find("#confluence-attachments").GetAttribute("aria-describedby")
            .Should().Be("confluence-attachments-help confluence-attachments-scope");

        cut.Find("#space-700").Change(true);
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => created.Should().ContainSingle());
        using var doc = JsonDocument.Parse(created[0].ScopeJson);
        doc.RootElement.GetProperty("includeAttachments").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Dialog_TurningAttachmentsOnWithoutTheScope_ShowsWhatToAddAndWhereToAddIt()
    {
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/sources?new={otherConnection.Id}");
        var cut = ctx.Render<SourcesPage>();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(1));
        cut.FindAll("#confluence-attachments-scope-route").Should().BeEmpty("nothing is asked for until attachments are turned on");

        cut.Find("#confluence-attachments").Change(true);

        cut.FindAll("#confluence-attachments-scope").Should().BeEmpty();
        var route = cut.Find("#confluence-attachments-scope-route");
        route.TextContent.Should().Contain("read:attachment:confluence").And.Contain("globex.atlassian.net");
        cut.Find("#confluence-attachments-scope-route-replace").GetAttribute("href")
            .Should().Be($"/connections?edit={otherConnection.Id}&replace=1");
        cut.Find("#confluence-attachments-scope-route-replace").TextContent.Should().Contain("Replace credentials on globex.atlassian.net");
        cut.Find("#confluence-attachments-scope-route-guide").GetAttribute("href")
            .Should().Be("https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md#missing-scope");
        cut.Find("#confluence-attachments").GetAttribute("aria-describedby")
            .Should().Be("confluence-attachments-help confluence-attachments-scope-route");
    }

    [Fact]
    public void Create_UnexpectedFailure_ShowsAPlainMessageAndNotTheExceptionText()
    {
        sources.CreateAsync(Arg.Any<CreateSourceRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<Source>>(_ => throw new TimeoutException("Npgsql: connection to 10.0.0.5:5432 timed out"));
        var cut = OpenDialog();
        cut.WaitForAssertion(() => cut.FindAll(".confluence-space-option").Should().HaveCount(2));

        cut.Find("#space-100").Change(true);
        cut.Find("#new-source-dialog .modal-footer button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Find("#confluence-spaces-error").TextContent
            .Should().Contain("The spaces could not be added. Try again; if it keeps failing, check the server log."));
        cut.Markup.Should().NotContain("Npgsql").And.NotContain("10.0.0.5");
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

    [Fact]
    public void Dialog_CredentialRefused_SaysWhatToDoAndLinksTheMatchingEntry()
    {
        stub.RejectToken = true;
        var cut = OpenDialog();

        cut.WaitForAssertion(() => cut.Find("#confluence-spaces-load-error").TextContent
            .Should().Contain("Replace credentials"));
        cut.Find("#confluence-spaces-load-error a").GetAttribute("href")
            .Should().EndWith("atlassian-setup.md#client-id-or-secret-rejected");
        cut.Markup.Should().NotContain("the-secret");
    }

    [Fact]
    public void Dialog_SpacesUnreadable_LinksTheNoSpacesEntry()
    {
        stub.SiteASpaces = null;
        var cut = OpenDialog();

        cut.WaitForAssertion(() => cut.Find("#confluence-spaces-load-error a").GetAttribute("href")
            .Should().EndWith("atlassian-setup.md#no-confluence-access-or-no-spaces"));
    }

    [Fact]
    public void Dialog_NoSpaces_LinksTheNoSpacesEntry()
    {
        stub.SiteASpaces = """{"results":[]}""";
        var cut = OpenDialog();

        cut.WaitForAssertion(() => cut.Find("#confluence-spaces-none a").GetAttribute("href")
            .Should().EndWith("atlassian-setup.md#no-confluence-access-or-no-spaces"));
    }

    private sealed class StubSpacesHandler : HttpMessageHandler
    {
        public bool RejectToken { get; set; }

        /// <summary>Spaces for the first site; a test can change what the site has between calls.</summary>
        public string? SiteASpaces { get; set; } = """
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
