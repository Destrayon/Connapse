using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Storage.Connectors.GitHub;
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
/// How a source's last sync reads on the Sources page: a failure says what to do, links the guide
/// entry for it and keeps the provider's answer behind Details; a warning is not a wait or an error.
/// </summary>
[Trait("Category", "Unit")]
public class SourcesSyncStatusTests : IDisposable
{
    private readonly BunitContext ctx = new();
    private readonly ISourceStore sources = Substitute.For<ISourceStore>();
    private readonly IConnectionStore connections = Substitute.For<IConnectionStore>();
    private readonly Connection site = new(
        Guid.NewGuid(), "acme.atlassian.net", ConnectionProvider.Atlassian,
        """{"siteUrl":"https://acme.atlassian.net","cloudId":"11111111-2222-3333-4444-555555555555","clientId":"client-1"}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);
    private readonly Connection repoConnection = new(
        Guid.NewGuid(), "github", ConnectionProvider.GitHub, "{}", null, DateTime.UtcNow, DateTime.UtcNow);

    public SourcesSyncStatusTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles("Admin");
        ctx.Services.AddLogging();
        ctx.Services.AddSourceOperations();

        var http = Substitute.For<IHttpClientFactory>();
        connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Connection>>([site, repoConnection]));

        ctx.Services.AddSingleton(sources);
        ctx.Services.AddSingleton(connections);
        ctx.Services.AddSingleton(Substitute.For<IAuditLogger>());
        ctx.Services.AddSingleton(NSubstitute.Substitute.For<Connapse.Core.Interfaces.ICustomProviderStore>());
        ctx.Services.AddSingleton(new Connapse.Web.Services.CustomSourceSetup(
            NSubstitute.Substitute.For<IHttpClientFactory>(),
            NSubstitute.Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<Connapse.Core.CustomSourceSettings>>(),
            NSubstitute.Substitute.For<Connapse.Core.Interfaces.ICustomProviderStore>(),
            NSubstitute.Substitute.For<Connapse.Core.Interfaces.IConnectionStore>()));
        ctx.Services.AddSingleton(new AtlassianSiteService(
            http, new AtlassianConnectionTester(http), connections, Substitute.For<IAuditLogger>(),
            new AtlassianTokenSource(http, TimeProvider.System),
            new Connapse.Application.Connections.CreateConnection(connections, Substitute.For<Connapse.Application.Ports.IConnectionSettingsRules>(),
                Substitute.For<IAuditLogger>(), Substitute.For<Connapse.Application.Ports.IOperationAuthorizer>()),
            new Connapse.Application.Connections.ReplaceConnectionSecret(connections, Substitute.For<ISourceStore>(),
                Substitute.For<Connapse.Application.Ports.IConnectionSettingsRules>(), Substitute.For<IAuditLogger>(),
                Substitute.For<Connapse.Application.Ports.IOperationAuthorizer>())));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Success()));
        ctx.Services.AddSingleton(new PrivateSourceVisibility(Substitute.For<ISearchScopeResolver>(), authorization, connections));
        ctx.Services.AddSingleton(Substitute.For<SourceScopePreflight>(Substitute.For<IOptionsMonitor<SourceSecuritySettings>>()));
        ctx.Services.AddSingleton(new SourceSyncService(
            Substitute.For<IServiceScopeFactory>(), Substitute.For<IConnectorFactory>(),
            Substitute.For<IIngestionQueue>(), NullLogger<SourceSyncService>.Instance));
        var app = new ConnapseGitHubApp(Substitute.For<IServiceScopeFactory>(), http, NullLogger<ConnapseGitHubApp>.Instance);
        ctx.Services.AddSingleton(app);
        ctx.Services.AddSingleton(new GitHubRepositoryLookup(new GitHubCredentialPool(app, Substitute.For<IServiceScopeFactory>()), http));
        ctx.Services.AddSingleton(new Connapse.Storage.Documents.DocumentCoordinateReport(
            Substitute.For<Microsoft.EntityFrameworkCore.IDbContextFactory<Connapse.Storage.Data.KnowledgeDbContext>>()));
    }

    public void Dispose() => ctx.Dispose();

    private IRenderedComponent<SourcesPage> Render(Source source)
    {
        sources.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Source>>([source]));
        var cut = ctx.Render<SourcesPage>();
        cut.WaitForAssertion(() => cut.Find("table"));
        return cut;
    }

    private Source Confluence(SyncStatus status, string? error, Connection? connection = null) => new(
        Guid.NewGuid(), "acme.atlassian.net / Engineering", null, (connection ?? site).Id,
        """{"spaceId":"100","spaceKey":"ENG"}""", DateTime.UtcNow, DateTime.UtcNow,
        LastSyncedAt: DateTime.UtcNow, LastSyncStatus: status, LastSyncError: error);

    /// <summary>
    /// A paused source says so with a badge, not by fading its row: halving the opacity took its
    /// failure and warning text below 4.5:1, on the row most likely to need attention.
    /// </summary>
    [Theory]
    [InlineData(SyncStatus.Failed, ".sync-error")]
    [InlineData(SyncStatus.Succeeded, ".sync-notice-warning")]
    public void PausedSource_KeepsItsAlertsAtFullContrast_AndSaysPausedWithABadge(SyncStatus status, string alert)
    {
        string error = status == SyncStatus.Failed
            ? AtlassianSyncErrors.SpaceUnreadable("ENG", 403)
            : ConfluenceSpaceConnector.AttachmentScopeNotice;
        var cut = Render(Confluence(status, error) with { Enabled = false });

        cut.Markup.Should().NotContain("opacity-50");
        cut.Find(alert).ClassList.Should().NotContain("opacity-50");
        foreach (var row in cut.FindAll("tbody tr"))
            row.ClassList.Should().Contain("source-paused").And.NotContain("opacity-50");
        var badge = cut.FindAll("tbody td .badge").Single(b => b.TextContent.Contains("Paused"));
        badge.QuerySelector(".bi-pause-circle").Should().NotBeNull("the state is an icon and text, not colour alone");
        cut.FindAll("tbody button").Should().Contain(b => b.TextContent.Trim() == "Resume syncing");
    }

    [Fact]
    public void DeleteSource_SaysHowManyDocumentsGo_AndNeedsTheNameTyped()
    {
        var source = Confluence(SyncStatus.Succeeded, null) with { DocumentCount = 42 };
        sources.DeleteAsync(source.Id, Arg.Any<CancellationToken>()).Returns(true);
        // The delete operation reads the source to check the typed name against it, as REST does.
        sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);
        var cut = Render(source);

        cut.FindAll("tbody button").Single(b => b.TextContent.Trim() == "Delete").Click();

        cut.Find("#delete-source-count").TextContent.Should().Be("42 indexed documents");
        cut.Find("#delete-source-consequence").TextContent.Should().Contain("cannot be undone");
        cut.Find("#delete-source-submit").HasAttribute("disabled").Should().BeTrue("nothing is typed yet");
        cut.Find("#delete-source-confirm").Input("acme.atlassian.net");
        cut.Find("#delete-source-submit").HasAttribute("disabled").Should().BeTrue("only part of the name is typed");
        cut.Find("#delete-source-submit").Click();
        sources.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);

        cut.Find("#delete-source-confirm").Input(source.Name);
        cut.Find("#delete-source-submit").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#delete-source-submit").Click();

        cut.WaitForAssertion(() => sources.Received(1).DeleteAsync(source.Id, Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void SpaceUnreadable_ShowsThePlainMessage_LinksItsEntry_AndKeepsTheStatusUnderDetails()
    {
        string error = AtlassianSyncErrors.SpaceUnreadable("ENG", 403) + SyncErrorText.DetailMarker + "HTTP 403";

        var cut = Render(Confluence(SyncStatus.Failed, error));

        var row = cut.Find(".sync-error");
        row.TextContent.Should().Contain("Give the service account the Viewer role in the space");
        row.QuerySelector("a")!.GetAttribute("href").Should().EndWith("atlassian-setup.md#space-can-no-longer-be-read");
        var details = row.QuerySelector("details")!;
        details.QuerySelector("summary")!.TextContent.Should().Be("Details");
        details.TextContent.Should().Contain("HTTP 403");
        row.TextContent.IndexOf("HTTP 403", StringComparison.Ordinal)
            .Should().BeGreaterThan(row.TextContent.IndexOf("sync again", StringComparison.Ordinal), "the raw answer comes after the plain words");
    }

    [Theory]
    [InlineData(AtlassianSyncErrors.CredentialRefused, "#client-id-or-secret-rejected")]
    [InlineData(AtlassianSyncErrors.FreshTokenRefused, "#cant-read-confluence")]
    [InlineData(AtlassianSyncErrors.Unavailable, "#couldnt-reach-atlassian")]
    [InlineData(AtlassianSyncErrors.TokenRedirected, "#couldnt-reach-atlassian")]
    [InlineData(AtlassianSyncErrors.NoAccessToken, "#couldnt-reach-atlassian")]
    [InlineData("Something nobody planned for.", "#troubleshooting")]
    public void EachConfluenceFailure_LinksItsTroubleshootingEntry(string error, string anchor)
    {
        var cut = Render(Confluence(SyncStatus.Failed, error));

        cut.Find(".sync-error a").GetAttribute("href").Should().Be(AtlassianSiteTroubleshooting.GuideUrl + anchor);
    }

    [Theory]
    [InlineData(AtlassianSyncErrors.JiraFreshTokenRefused, "#cant-read-jira")]
    [InlineData(AtlassianSyncErrors.JiraUnavailable, "#couldnt-reach-atlassian")]
    [InlineData(JiraProjectConnector.AttachmentScopeNotice, "#missing-scope")]
    public void EachJiraFailure_LinksItsTroubleshootingEntry(string error, string anchor)
    {
        var cut = Render(Jira(SyncStatus.Failed, error));

        cut.Find(".sync-error a").GetAttribute("href").Should().Be(AtlassianSiteTroubleshooting.GuideUrl + anchor);
    }

    [Fact]
    public void JiraProjectUnreadable_LinksTheProjectEntry()
    {
        var cut = Render(Jira(SyncStatus.Failed, AtlassianSyncErrors.JiraProjectUnreadable("ENG", 403) + "\nDetails: HTTP 403"));

        var row = cut.Find(".sync-error");
        row.TextContent.Should().Contain("Give the service account the Browse projects permission");
        row.QuerySelector("a")!.GetAttribute("href").Should().EndWith("atlassian-setup.md#project-can-no-longer-be-read");
    }

    private Source Jira(SyncStatus status, string? error) => new(
        Guid.NewGuid(), "acme.atlassian.net / ENG", null, site.Id,
        """{"kind":"jira-project","projectId":"10000","projectKey":"ENG"}""", DateTime.UtcNow, DateTime.UtcNow,
        LastSyncedAt: DateTime.UtcNow, LastSyncStatus: status, LastSyncError: error);

    [Fact]
    public void GitHubFailure_StillLinksTheGitHubGuide()
    {
        var cut = Render(Confluence(SyncStatus.Failed, "GitHub repository acme/x can no longer be read", repoConnection));

        cut.Find(".sync-error a").GetAttribute("href").Should().Be(GitHubMessages.TroubleshootingUrl);
        cut.FindAll(".sync-error details").Should().BeEmpty("there is no provider answer to show");
    }

    [Fact]
    public void SkippedAttachments_IsAWarning_NotAWaitOrAnError()
    {
        string notice = "2 attachments were " + ConfluenceSpaceConnector.SkippedAttachmentsPhrase
            + " 25 MB, or a file type Connapse can't read. To include larger files, raise Max attachment size on the source.";

        var cut = Render(Confluence(SyncStatus.Succeeded, notice));

        var badge = cut.Find("td .badge.text-bg-warning");
        badge.TextContent.Trim().Should().Be("Warning");
        badge.QuerySelector(".bi-exclamation-triangle-fill").Should().NotBeNull("colour is never the only signal");
        var row = cut.Find(".sync-notice-warning");
        row.ClassList.Should().Contain("alert-warning").And.NotContain("alert-danger");
        row.QuerySelector(".bi-hourglass-split").Should().BeNull();
        row.TextContent.Should().Contain("Warning:");
        row.QuerySelector("a")!.GetAttribute("href").Should().EndWith("#attachments-skipped");
        cut.FindAll(".sync-notice-waiting").Should().BeEmpty();
    }

    [Fact]
    public void AttachmentScopeNotice_IsAWarningLinkingMissingScope()
    {
        var cut = Render(Confluence(SyncStatus.Succeeded, ConfluenceSpaceConnector.AttachmentScopeNotice));

        cut.Find(".sync-notice-warning a").GetAttribute("href").Should().EndWith("#missing-scope");
    }

    [Theory]
    [InlineData("Atlassian is rate limiting this site; retry after 30 seconds.")]
    [InlineData("GitHub's hourly request limit is used up until 14:00 UTC. Syncing resumes after that.")]
    public void RateLimit_IsAWait_NotAWarning(string notice)
    {
        var cut = Render(Confluence(SyncStatus.Succeeded, notice));

        cut.Find("td .badge").TextContent.Trim().Should().Be("Waiting");
        cut.Find("td .badge .bi-hourglass-split").Should().NotBeNull();
        cut.Find(".sync-notice-waiting").ClassList.Should().Contain("alert-secondary");
        cut.FindAll(".sync-notice-warning").Should().BeEmpty();
    }
}
