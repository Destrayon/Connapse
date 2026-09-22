using System.Text;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using Connapse.Storage.Connectors.GitHub;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// The issues-and-pull-requests kind of the GitHub connector, against an in-memory GitHub API.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitHubRecordSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gh-records-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHubApi _api = new();
    private readonly ManualClock _clock = new();

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private GitHubConnectorConfig Config(bool includeComments = true) => new()
    {
        Owner = "octocat",
        Repo = "hello",
        Kind = GitHubContentKind.IssuesAndPullRequests,
        MirrorPath = _root,
        ApiBaseUrl = FakeGitHubApi.BaseUrl,
        IncludeComments = includeComments,
    };

    private GitHubRecordSource Source(bool includeComments = true) =>
        new(Config(includeComments), _api.CreateClient(), NullLogger.Instance, _clock);

    private GitHubConnector Connector() => new(Config(), _api.CreateClient());

    private static string[] Paths(SyncDelta delta) => [.. delta.Upserted.Select(f => f.Path).Order()];

    private static ConnectorFile File(SyncDelta delta, string path) => delta.Upserted.Single(f => f.Path == path);

    private static async Task<string> ReadAsync(IConnector connector, string path)
    {
        await using var stream = await connector.ReadFileAsync(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    // ── First sync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetChangesAsync_FirstSync_EmitsEveryRecordWithEdges()
    {
        _api.UpsertIssue(1, "Crash on start", "It crashes. See #7.", labels: ["bug"], milestone: "v1");
        _api.UpsertIssue(2, "Fix the crash", "Fixes #1", pullRequest: true, merged: true);
        _api.UpsertIssue(3, "Sub-task");
        _api.SetSubIssues(1, 3);

        var delta = await Source().GetChangesAsync(null, default);

        Paths(delta).Should().Equal("/issues/1.md", "/issues/3.md", "/pulls/2.md");
        delta.NextCursor.Should().NotBeNull();
        delta.RequiresFullResync.Should().BeFalse();

        var issue = File(delta, "/issues/1.md").Metadata!;
        issue["github:labels"].Should().Be("bug");
        issue["github:milestone"].Should().Be("v1");
        issue["github:children"].Should().Be("3");
        issue["github:references"].Should().Be("7");
        File(delta, "/issues/3.md").Metadata!["github:parent"].Should().Be("1");

        var pull = File(delta, "/pulls/2.md");
        pull.Metadata!["github:closes"].Should().Be("1");
        pull.Metadata["github:state"].Should().Be("merged");
        pull.ResourceUri.Should().Be("https://github.com/octocat/hello/pull/2");
        pull.ContentType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task GetChangesAsync_FirstSync_AssemblesCommentsIntoTheRecord()
    {
        _api.UpsertIssue(1, "Crash", "Body text");
        _api.AddComment(1, "alice", "Me too");
        _api.UpsertIssue(2, "Change", pullRequest: true);
        _api.AddComment(2, "bob", "Nit: rename", reviewPath: "src/app.cs");

        var connector = Connector();
        var delta = await connector.GetChangesAsync(null);

        string issue = await ReadAsync(connector, "/issues/1.md");
        issue.Should().StartWith("# 1: Crash\n");
        issue.Should().Contain("Body text");
        issue.Should().Contain("--- Comment by alice (2026-01-01) ---\nMe too");
        File(delta, "/issues/1.md").SizeBytes.Should().Be(Encoding.UTF8.GetByteCount(issue));

        (await ReadAsync(connector, "/pulls/2.md"))
            .Should().Contain("--- Review comment by bob on src/app.cs (2026-01-01) ---\nNit: rename");
    }

    [Fact]
    public async Task GetChangesAsync_CommentsExcluded_RecordHasNoComments()
    {
        _api.UpsertIssue(1, "Crash", "Body");
        _api.AddComment(1, "alice", "Me too");

        var delta = await Source(includeComments: false).GetChangesAsync(null, default);

        _api.RequestedPaths.Should().NotContain(p => p.Contains("/comments"));
        File(delta, "/issues/1.md").Metadata!["github:commentCount"].Should().Be("0");
    }

    // ── Incremental ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetChangesAsync_NothingChanged_EmitsNothingForOneRequest()
    {
        _api.UpsertIssue(1, "Crash");
        _api.AddComment(1, "alice", "Me too");
        var first = await Source().GetChangesAsync(null, default);
        int before = _api.Requests;

        var second = await Source().GetChangesAsync(first.NextCursor, default);

        second.Upserted.Should().BeEmpty();
        second.DeletedPaths.Should().BeEmpty();
        (_api.Requests - before).Should().Be(1, "an idle cycle reads one page of issues and skips the comment sweeps");
    }

    [Fact]
    public async Task GetChangesAsync_NewComment_ReemitsOnlyThatRecord()
    {
        _api.UpsertIssue(1, "Crash");
        _api.UpsertIssue(2, "Other");
        var first = await Source().GetChangesAsync(null, default);

        _api.AddComment(1, "alice", "Found the cause");
        var second = await Source().GetChangesAsync(first.NextCursor, default);

        Paths(second).Should().Equal("/issues/1.md");
        (await ReadAsync(Connector(), "/issues/1.md")).Should().Contain("Found the cause");
    }

    [Fact]
    public async Task GetChangesAsync_CommentDeleted_RemovesItFromTheRecord()
    {
        _api.UpsertIssue(1, "Crash");
        long id = _api.AddComment(1, "alice", "Spam link");
        _api.AddComment(1, "bob", "Real answer");
        var first = await Source().GetChangesAsync(null, default);

        _api.DeleteComment(id);
        var second = await Source().GetChangesAsync(first.NextCursor, default);

        Paths(second).Should().Equal("/issues/1.md");
        string text = await ReadAsync(Connector(), "/issues/1.md");
        text.Should().NotContain("Spam link").And.Contain("Real answer");
    }

    [Fact]
    public async Task GetChangesAsync_CommentMinimized_DropsItFromTheRecord()
    {
        _api.UpsertIssue(1, "Crash");
        long id = _api.AddComment(1, "alice", "Buy now");
        var first = await Source().GetChangesAsync(null, default);

        _api.MinimizeComment(id);
        _api.UpsertIssue(1, "Crash (triaged)");
        var second = await Source().GetChangesAsync(first.NextCursor, default);

        Paths(second).Should().Equal("/issues/1.md");
        (await ReadAsync(Connector(), "/issues/1.md")).Should().NotContain("Buy now");
    }

    [Fact]
    public async Task GetChangesAsync_SubIssueRemoved_ClearsTheChildsParent()
    {
        _api.UpsertIssue(1, "Epic");
        _api.UpsertIssue(2, "Task");
        _api.SetSubIssues(1, 2);
        var first = await Source().GetChangesAsync(null, default);

        _api.SetSubIssues(1);
        var second = await Source().GetChangesAsync(first.NextCursor, default);

        Paths(second).Should().Equal("/issues/1.md", "/issues/2.md");
        File(second, "/issues/2.md").Metadata!.Should().NotContainKey("github:parent");
        File(second, "/issues/1.md").Metadata!.Should().NotContainKey("github:children");
    }

    // ── Budget and recovery ────────────────────────────────────────────────

    [Fact]
    public async Task GetChangesAsync_RateLimitedMidSync_EmitsNothingThenResumes()
    {
        for (int n = 1; n <= 5; n++) _api.UpsertIssue(n, "Issue " + n);
        _api.Budget = 2; // two pages of issues, then refused

        var partial = await Source().GetChangesAsync(null, default);

        partial.Upserted.Should().BeEmpty("a cycle that stopped partway does not emit half-assembled records");
        partial.NextCursor.Should().NotBeNull();
        partial.RequiresFullResync.Should().BeFalse();

        _api.Budget = null;
        int before = _api.Requests;
        var resumed = await Source().GetChangesAsync(partial.NextCursor, default);

        Paths(resumed).Should().HaveCount(5);
        _api.RequestedPaths.Skip(before).First().Should().Contain("since=", "the resumed sweep starts where the last one stopped");
    }

    [Fact]
    public async Task GetChangesAsync_EngineDidNotStoreCursor_ReemitsTheRecords()
    {
        _api.UpsertIssue(1, "Crash");
        var first = await Source().GetChangesAsync(null, default);
        _api.AddComment(1, "alice", "More detail");
        var second = await Source().GetChangesAsync(first.NextCursor, default);
        Paths(second).Should().Equal("/issues/1.md");

        // The engine failed after that emission and still holds the older cursor.
        var retried = await Source().GetChangesAsync(first.NextCursor, default);
        Paths(retried).Should().Equal("/issues/1.md");

        var settled = await Source().GetChangesAsync(retried.NextCursor, default);
        settled.Upserted.Should().BeEmpty();
    }

    [Fact]
    public async Task GetChangesAsync_DailyRelist_DeletesIssuesGoneUpstream()
    {
        _api.UpsertIssue(1, "Keep");
        _api.UpsertIssue(2, "Transferred away");
        var first = await Source().GetChangesAsync(null, default);

        _api.DeleteIssue(2);
        var early = await Source().GetChangesAsync(first.NextCursor, default);
        early.DeletedPaths.Should().BeEmpty("the relist runs once a day, not every cycle");

        _clock.Advance(GitHubRecordSource.RelistInterval);
        var relisted = await Source().GetChangesAsync(early.NextCursor, default);

        relisted.DeletedPaths.Should().Equal("/issues/2.md");
        (await Connector().ExistsAsync("/issues/2.md")).Should().BeFalse();
    }

    [Fact]
    public async Task GetChangesAsync_RepositoryNoLongerPublic_ThrowsUnavailable()
    {
        _api.UpsertIssue(1, "Crash");
        var first = await Source().GetChangesAsync(null, default);
        _api.Gone = true;

        Func<Task> act = () => Source().GetChangesAsync(first.NextCursor, default);

        await act.Should().ThrowAsync<GitHubRepositoryUnavailableException>();
    }

    [Fact]
    public async Task GetChangesAsync_CursorButNoStore_RequiresFullResync()
    {
        _api.UpsertIssue(1, "Crash");
        var first = await Source().GetChangesAsync(null, default);
        Directory.Delete(_root, recursive: true);

        var delta = await Source().GetChangesAsync(first.NextCursor, default);

        delta.RequiresFullResync.Should().BeTrue();
        delta.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task GetChangesAsync_EmptyRepository_DoesNotRelistFromTheStartEachCycle()
    {
        var first = await Source().GetChangesAsync(null, default);
        int before = _api.Requests;

        await Source().GetChangesAsync(first.NextCursor, default);

        _api.RequestedPaths.Skip(before).Should().OnlyContain(p => p.Contains("since="));
    }

    // ── Reads ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadFileAsync_WrongKindOrUnknownNumber_IsNotFound()
    {
        _api.UpsertIssue(1, "Crash");
        var connector = Connector();
        await connector.GetChangesAsync(null);

        (await connector.ExistsAsync("/issues/1.md")).Should().BeTrue();
        (await connector.ExistsAsync("/pulls/1.md")).Should().BeFalse();
        (await connector.ExistsAsync("/issues/9.md")).Should().BeFalse();
        (await connector.ExistsAsync("/issues/../1.md")).Should().BeFalse();

        Func<Task> act = () => connector.ReadFileAsync("/issues/9.md");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ListFilesAsync_ListsSyncedRecordsUnderPrefix()
    {
        _api.UpsertIssue(1, "Crash");
        _api.UpsertIssue(2, "Fix", pullRequest: true);
        var connector = Connector();
        await connector.GetChangesAsync(null);

        (await connector.ListFilesAsync()).Select(f => f.Path).Should().Equal("/issues/1.md", "/pulls/2.md");
        (await connector.ListFilesAsync("pulls")).Select(f => f.Path).Should().Equal("/pulls/2.md");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

[Trait("Category", "Unit")]
public sealed class GitHubRecordRendererTests
{
    private static GitHubIssue Issue(int number = 5, string? body = null, bool pullRequest = false) => new(
        Id: 1, Number: number, Title: "Title", Body: body, State: "open", User: new GitHubUser("octo"),
        Labels: [], Milestone: null, HtmlUrl: "https://github.com/o/r/issues/" + number, Comments: 0,
        CreatedAt: new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero),
        UpdatedAt: new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
        ClosedAt: null, ClosedBy: null,
        PullRequest: pullRequest ? new GitHubPullRequestRef(null) : null, SubIssuesSummary: null);

    [Fact]
    public void ClosingReferences_MatchesGitHubKeywordsOnly()
    {
        GitHubRecordRenderer.ClosingReferences("Fixes #1, closes: #2, resolved #3, refs #4, prefix#5")
            .Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Mentions_IgnoresUrlsCrossRepoRefsAndEntities()
    {
        GitHubRecordRenderer.Mentions(
                ["see #10 and #11", "https://x.test/a#12 other/repo#13 &#14; (#15)"], exclude: [11])
            .Should().Equal(10, 15);
    }

    [Theory]
    [InlineData("/issues/12.md", true, 12, false)]
    [InlineData("/pulls/3.md", true, 3, true)]
    [InlineData("/issues/12", false, 0, false)]
    [InlineData("/issues/0.md", false, 0, false)]
    [InlineData("/docs/12.md", false, 0, false)]
    public void TryParsePath_AcceptsOnlyRecordPaths(string path, bool ok, int number, bool pull)
    {
        GitHubRecordRenderer.TryParsePath(path, out int n, out bool isPull).Should().Be(ok);
        if (ok)
        {
            n.Should().Be(number);
            isPull.Should().Be(pull);
        }
    }

    [Fact]
    public void Render_EdgeLineAndMetadata_OmittedWhenThereAreNoEdges()
    {
        var rendered = GitHubRecordRenderer.Render(
            new GitHubStoredRecord { Number = 5, Issue = Issue(body: "plain") }, "o", "r");

        rendered.Markdown.Should().Be("# 5: Title\nType: Issue · Author: octo · State: open · Created: 2026-03-04\n\nplain\n");
        rendered.Metadata.Keys.Should().NotContain(["github:closes", "github:parent", "github:children", "github:references"]);
        rendered.LastModified.Should().Be(new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Render_LastModified_IsTheNewestOfIssueAndComments()
    {
        var edited = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var record = new GitHubStoredRecord
        {
            Number = 5,
            Issue = Issue(),
            Comments = { ["c1"] = new GitHubStoredComment("a", "x", edited.AddDays(-9), edited, null, false) },
        };

        GitHubRecordRenderer.Render(record, "o", "r").LastModified.Should().Be(edited.UtcDateTime);
    }
}
