using System.Net;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Services;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// A Confluence sync failure reaches the Sources page as plain words that say what to do, with
/// Atlassian's answer kept apart for the Details disclosure and a guide entry to follow.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AtlassianSyncErrorsTests : IDisposable
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";
    private const string SpaceId = "4001";

    private readonly FakeAtlassianApi _api = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "confluence-errors-" + Guid.NewGuid().ToString("N"));
    private readonly ConfluenceSpaceConnector _connector;

    public AtlassianSyncErrorsTests()
    {
        var site = new AtlassianSite("https://acme.atlassian.net", CloudId, "client-1");
        var tokens = new AtlassianTokenSource(new FakeFactory(_api), TimeProvider.System);
        _connector = new ConfluenceSpaceConnector(
            new ConfluenceSpaceConfig(site, SpaceId, "ENG", _root),
            new AtlassianApiClient(_api.CreateClient(), tokens, site, "secret"));
        _api.Confluence.AddSpace(SpaceId, "ENG", "Engineering");
    }

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SpaceForbidden_SaysToGiveViewerBack_WithTheStatusAsDetail()
    {
        _api.Confluence.ForbidSpace(SpaceId);

        var thrown = (await FluentActions.Awaiting(() => _connector.GetChangesAsync(null))
            .Should().ThrowAsync<ConfluenceSpaceUnreadableException>()).Which;

        thrown.Should().BeAssignableTo<SourceAccessRevokedException>("its documents leave search");
        thrown.Message.Should().StartWith("Confluence space ENG can no longer be read: the service account lost access to it.")
            .And.Contain("Viewer role").And.NotContain("HTTP");
        thrown.Detail.Should().Be("HTTP 403");
        SyncErrorText.Of(thrown).Should().Be(thrown.Message + "\nDetails: HTTP 403");
        AtlassianSyncErrors.TroubleshootingAnchor(SyncErrorText.Of(thrown)).Should().Be(AtlassianSiteTroubleshooting.SpaceUnreadable);
    }

    [Fact]
    public async Task SpaceGone_SaysItMayHaveBeenDeleted()
    {
        _api.Confluence.RemoveSpace(SpaceId);

        var thrown = (await FluentActions.Awaiting(() => _connector.GetChangesAsync(null))
            .Should().ThrowAsync<ConfluenceSpaceUnreadableException>()).Which;

        thrown.Message.Should().Contain("it was deleted").And.Contain("delete this source");
        thrown.Detail.Should().Be("HTTP 404");
    }

    [Fact]
    public async Task CredentialRefused_SaysToReplaceIt_WithAtlassiansCodeAsDetail()
    {
        _api.FailTokenWith(HttpStatusCode.Unauthorized, """{"error":"access_denied"}""");

        var thrown = (await FluentActions.Awaiting(() => _connector.GetChangesAsync(null))
            .Should().ThrowAsync<AtlassianAuthException>()).Which;

        thrown.TokenRefused.Should().BeTrue();
        thrown.Message.Should().Be(AtlassianSyncErrors.CredentialRefused);
        thrown.Detail.Should().Be("HTTP 401, access_denied");
        AtlassianSyncErrors.TroubleshootingAnchor(SyncErrorText.Of(thrown)).Should().Be(AtlassianSiteTroubleshooting.CredentialsRejected);
    }

    [Fact]
    public async Task ServerError_IsPlainWithTheStatusAsDetail()
    {
        _api.Intercept = uri => uri.AbsolutePath.EndsWith($"/spaces/{SpaceId}", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : null;

        var thrown = (await FluentActions.Awaiting(() => _connector.GetChangesAsync(null))
            .Should().ThrowAsync<AtlassianUnavailableException>()).Which;

        thrown.Message.Should().Be(AtlassianSyncErrors.Unavailable);
        thrown.Detail.Should().Be("HTTP 502 (BadGateway)");
        AtlassianSyncErrors.TroubleshootingAnchor(SyncErrorText.Of(thrown)).Should().Be(AtlassianSiteTroubleshooting.Unreachable);
    }

    [Theory]
    [InlineData(AtlassianSyncErrors.FreshTokenRefused, AtlassianSiteTroubleshooting.CantReadConfluence)]
    [InlineData(AtlassianSyncErrors.JiraFreshTokenRefused, AtlassianSiteTroubleshooting.CantReadJira)]
    [InlineData(AtlassianSyncErrors.JiraUnavailable, AtlassianSiteTroubleshooting.Unreachable)]
    [InlineData(JiraProjectConnector.AttachmentScopeNotice, AtlassianSiteTroubleshooting.MissingScope)]
    [InlineData("Jira attachment 30001 was not downloaded. " + JiraProjectConnector.AttachmentScopeNotice, AtlassianSiteTroubleshooting.MissingScope)]
    [InlineData("2 attachments were skipped: larger than 25 MB, or a file type Connapse can't read. To include larger files, raise Max attachment size on the source.", AtlassianSiteTroubleshooting.AttachmentsSkipped)]
    [InlineData(AtlassianSyncErrors.TokenRedirected, AtlassianSiteTroubleshooting.Unreachable)]
    [InlineData(AtlassianSyncErrors.NoAccessToken, AtlassianSiteTroubleshooting.Unreachable)]
    [InlineData(ConfluenceSpaceConnector.AttachmentScopeNotice, AtlassianSiteTroubleshooting.MissingScope)]
    [InlineData("1 attachment was skipped: larger than 25 MB, or a file type Connapse can't read.", AtlassianSiteTroubleshooting.AttachmentsSkipped)]
    [InlineData("Atlassian is rate limiting this site; retry after 30 seconds.", AtlassianSiteTroubleshooting.General)]
    [InlineData("Provider requested a full resync; cursor cleared.", AtlassianSiteTroubleshooting.General)]
    public void TroubleshootingAnchor_MapsEachMessageToItsEntry(string error, string anchor) =>
        AtlassianSyncErrors.TroubleshootingAnchor(error).Should().Be(anchor);

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public void TroubleshootingAnchor_JiraProjectUnreadable_LinksProjectEntry(int status) =>
        AtlassianSyncErrors.TroubleshootingAnchor(AtlassianSyncErrors.JiraProjectUnreadable("ENG", status) + "\nDetails: HTTP " + status)
            .Should().Be(AtlassianSiteTroubleshooting.ProjectUnreadable);

    [Fact]
    public void SyncErrorText_SplitsTheMessageFromTheDetail()
    {
        SyncErrorText.Split("Plain words.\nDetails: HTTP 403").Should().Be(("Plain words.", "HTTP 403"));
        SyncErrorText.Split("Plain words only.").Should().Be(("Plain words only.", (string?)null));
        SyncErrorText.Of(new InvalidOperationException("No detail here.")).Should().Be("No detail here.");
    }

    [Theory]
    [InlineData("Atlassian is rate limiting this site.", SyncNoticeKind.Waiting)]
    [InlineData("GitHub's hourly request limit is used up until 14:00 UTC. Syncing resumes after that.", SyncNoticeKind.Waiting)]
    [InlineData(ConfluenceSpaceConnector.AttachmentScopeNotice, SyncNoticeKind.Warning)]
    [InlineData("2 attachments were skipped: larger than 25 MB, or a file type Connapse can't read.", SyncNoticeKind.Warning)]
    [InlineData("2 attachments were skipped: larger than 25 MB. Atlassian is rate limiting this site.", SyncNoticeKind.Warning)]
    public void SourceSyncNotice_OnlyARateLimitIsAWait(string notice, SyncNoticeKind kind) =>
        SourceSyncNotice.KindOf(notice).Should().Be(kind);

    [Fact]
    public void RateLimitNotice_StartsWithThePrefixTheClassifierReads()
    {
        new AtlassianRateLimitedException(TimeSpan.FromSeconds(30)).Message
            .Should().StartWith(AtlassianRateLimitedException.MessagePrefix);
    }

    private sealed class FakeFactory(FakeAtlassianApi api) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => api.CreateClient();
    }
}
