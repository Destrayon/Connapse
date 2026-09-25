using System.Text;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using FluentAssertions;
using LibGit2Sharp;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// The docs kind of the GitHub connector, against a local repository standing in for
/// github.com. Git transport is the same code path for a local remote as for HTTPS, so what
/// these cover — mirror creation, fetch, tree diff, reads — is what runs in production; only
/// the URL differs.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitHubConnectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gh-connector-" + Guid.NewGuid().ToString("N"));
    private readonly string _upstream;
    private readonly string _mirror;

    public GitHubConnectorTests()
    {
        _upstream = Path.Combine(_root, "upstream");
        _mirror = Path.Combine(_root, "mirror");
        Repository.Init(_upstream);
    }

    public void Dispose()
    {
        // Git marks pack and object files read-only, which Directory.Delete refuses on Windows.
        if (!Directory.Exists(_root)) return;
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private GitHubConnector Connector(
        IReadOnlyList<string>? include = null, IReadOnlyList<string>? exclude = null,
        GitHubContentKind kind = GitHubContentKind.Docs) =>
        new(new GitHubConnectorConfig
        {
            Owner = "octocat",
            Repo = "docs",
            Kind = kind,
            IncludePatterns = include ?? GitHubConnectorConfig.DefaultDocPatterns,
            ExcludePatterns = exclude ?? [],
            MirrorPath = _mirror,
            RemoteUrl = _upstream,
        });

    /// <summary>Writes, deletes, and commits in the upstream repository. Returns the new SHA.</summary>
    private string Commit(IDictionary<string, string>? write = null, params string[] delete)
    {
        using var repo = new Repository(_upstream);

        foreach (var (path, content) in write ?? new Dictionary<string, string>())
        {
            string full = Path.Combine(_upstream, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            Commands.Stage(repo, path);
        }

        foreach (string path in delete)
        {
            File.Delete(Path.Combine(_upstream, path));
            Commands.Stage(repo, path);
        }

        var who = new Signature("t", "t@example.com", DateTimeOffset.UtcNow);
        return repo.Commit("change", who, who).Sha;
    }

    private static async Task<string> ReadAllAsync(IConnector connector, string path)
    {
        await using var stream = await connector.ReadFileAsync(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task GetChangesAsync_NullCursor_ReturnsEveryMarkdownFileAndTheHeadSha()
    {
        string head = Commit(new Dictionary<string, string>
        {
            ["README.md"] = "# Readme",
            ["docs/guide.markdown"] = "guide",
            ["docs/deep/nested.MD"] = "nested",
            ["src/Program.cs"] = "class P {}",
            ["docs/diagram.png"] = "not really a png",
        });

        var delta = await Connector().GetChangesAsync(cursor: null);

        delta.RequiresFullResync.Should().BeFalse();
        delta.NextCursor.Should().Be(head);
        delta.DeletedPaths.Should().BeEmpty();
        delta.Upserted.Select(f => f.Path).Should().BeEquivalentTo(
            "/README.md", "/docs/guide.markdown", "/docs/deep/nested.MD");
    }

    [Fact]
    public async Task GetChangesAsync_PrivateRepository_AddressesEachDocumentForThePermissionFilter()
    {
        Commit(new Dictionary<string, string> { ["docs/a b.md"] = "12345" });
        var connector = new GitHubConnector(new GitHubConnectorConfig
        {
            Owner = "octocat", Repo = "docs", RepoId = 99, IsPrivate = true, RequirePublic = false,
            MirrorPath = _mirror, RemoteUrl = _upstream,
        });

        var delta = await connector.GetChangesAsync(cursor: null);

        delta.Upserted.Should().ContainSingle().Which.ResourceUri.Should().Be("github://99/docs/a b.md");
    }

    [Fact]
    public async Task GetChangesAsync_NullCursor_ReportsSizeAndCitationLink()
    {
        Commit(new Dictionary<string, string> { ["docs/a b.md"] = "12345" });

        var delta = await Connector().GetChangesAsync(cursor: null);

        var file = delta.Upserted.Should().ContainSingle().Subject;
        file.SizeBytes.Should().Be(5);
        file.Metadata!["github:url"].Should().Be("https://github.com/octocat/docs/blob/HEAD/docs/a%20b.md");
        file.ResourceUri.Should().BeNull("an address would put a public document behind cloud permission filtering");
    }

    [Fact]
    public async Task GetChangesAsync_UnchangedHead_ReturnsAnEmptyDelta()
    {
        string head = Commit(new Dictionary<string, string> { ["a.md"] = "a" });
        var connector = Connector();
        await connector.GetChangesAsync(cursor: null);

        var delta = await connector.GetChangesAsync(head);

        delta.Upserted.Should().BeEmpty();
        delta.DeletedPaths.Should().BeEmpty();
        delta.NextCursor.Should().Be(head);
        delta.RequiresFullResync.Should().BeFalse();
    }

    [Fact]
    public async Task GetChangesAsync_AfterACommit_ReportsOnlyWhatChanged()
    {
        string first = Commit(new Dictionary<string, string>
        {
            ["keep.md"] = "same",
            ["edit.md"] = "before",
            ["gone.md"] = "bye",
            ["code.cs"] = "x",
        });
        var connector = Connector();
        await connector.GetChangesAsync(cursor: null);

        string second = Commit(
            new Dictionary<string, string> { ["edit.md"] = "after", ["new/added.md"] = "hi", ["code.cs"] = "y" },
            "gone.md");

        var delta = await connector.GetChangesAsync(first);

        delta.NextCursor.Should().Be(second);
        delta.Upserted.Select(f => f.Path).Should().BeEquivalentTo("/edit.md", "/new/added.md");
        delta.DeletedPaths.Should().Equal("/gone.md");
    }

    [Fact]
    public async Task GetChangesAsync_Rename_DeletesTheOldPathAndUpsertsTheNew()
    {
        string first = Commit(new Dictionary<string, string> { ["old.md"] = "content that moves" });
        var connector = Connector();
        await connector.GetChangesAsync(cursor: null);

        Commit(new Dictionary<string, string> { ["moved/new.md"] = "content that moves" }, "old.md");

        var delta = await connector.GetChangesAsync(first);

        delta.Upserted.Select(f => f.Path).Should().Equal("/moved/new.md");
        delta.DeletedPaths.Should().Equal("/old.md");
    }

    [Fact]
    public async Task GetChangesAsync_HistoryRewrittenUpstream_StillDiffsAgainstTheMirroredCommit()
    {
        string first = Commit(new Dictionary<string, string> { ["a.md"] = "a", ["b.md"] = "b" });
        var connector = Connector();
        await connector.GetChangesAsync(cursor: null);

        // A force-push: upstream history is replaced by an unrelated commit, so the cursor no
        // longer exists on the remote. The mirror kept it, which is what makes this a diff
        // rather than a resync.
        using (var repo = new Repository(_upstream))
        {
            var who = new Signature("t", "t@example.com", DateTimeOffset.UtcNow);
            File.Delete(Path.Combine(_upstream, "b.md"));
            File.WriteAllText(Path.Combine(_upstream, "c.md"), "c");
            Commands.Stage(repo, "*");
            var tree = repo.ObjectDatabase.CreateTree(repo.Index);
            var orphan = repo.ObjectDatabase.CreateCommit(who, who, "rewrite", tree, [], prettifyMessage: false);
            repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), orphan.Id, "force-push");
        }

        var delta = await connector.GetChangesAsync(first);

        delta.RequiresFullResync.Should().BeFalse();
        delta.Upserted.Select(f => f.Path).Should().Equal("/c.md");
        delta.DeletedPaths.Should().Equal("/b.md");
    }

    [Fact]
    public async Task GetChangesAsync_CursorTheMirrorNeverSaw_RequiresAFullResync()
    {
        Commit(new Dictionary<string, string> { ["a.md"] = "a" });

        var delta = await Connector().GetChangesAsync("0123456789abcdef0123456789abcdef01234567");

        delta.RequiresFullResync.Should().BeTrue();
        delta.NextCursor.Should().BeNull("a resync response carries no cursor");
    }

    [Theory]
    [InlineData("not-a-sha")]
    [InlineData("")]
    public async Task GetChangesAsync_MalformedCursor_RequiresAFullResync(string cursor)
    {
        Commit(new Dictionary<string, string> { ["a.md"] = "a" });

        var delta = await Connector().GetChangesAsync(cursor);

        delta.RequiresFullResync.Should().BeTrue();
    }

    [Fact]
    public async Task GetChangesAsync_ExcludePattern_DropsMatchingFilesFromListingAndDiff()
    {
        string first = Commit(new Dictionary<string, string> { ["README.md"] = "r", ["CHANGELOG.md"] = "c" });
        var connector = Connector(exclude: ["changelog.md"]);

        var initial = await connector.GetChangesAsync(cursor: null);
        Commit(new Dictionary<string, string> { ["CHANGELOG.md"] = "c2" });
        var delta = await connector.GetChangesAsync(first);

        initial.Upserted.Select(f => f.Path).Should().Equal("/README.md");
        delta.Upserted.Should().BeEmpty();
        delta.DeletedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task GetChangesAsync_RemoteUnreachable_Throws()
    {
        var connector = new GitHubConnector(new GitHubConnectorConfig
        {
            Owner = "octocat",
            Repo = "docs",
            MirrorPath = _mirror,
            RemoteUrl = Path.Combine(_root, "does-not-exist"),
        });

        Func<Task> act = () => connector.GetChangesAsync(cursor: null);

        await act.Should().ThrowAsync<LibGit2SharpException>();
    }

    [Fact]
    public void Constructor_IssuesKindWithoutHttpClient_Throws()
    {
        Action act = () => Connector(kind: GitHubContentKind.IssuesAndPullRequests);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("remote authentication required but no callback set", true)]
    [InlineData("too many redirects or authentication replays", true)]
    [InlineData("failed to resolve address for github.com", false)]
    public void IsAuthenticationRefusal_ClassifiesLibgit2Messages(string message, bool expected)
    {
        GitHubConnector.IsAuthenticationRefusal(new LibGit2SharpException(message)).Should().Be(expected);
    }

    [Fact]
    public async Task ReadFileAsync_AfterSync_ReturnsTheContentAtHead()
    {
        Commit(new Dictionary<string, string> { ["docs/a.md"] = "# First" });
        var connector = Connector();
        await connector.GetChangesAsync(cursor: null);
        Commit(new Dictionary<string, string> { ["docs/a.md"] = "# Second" });
        await connector.GetChangesAsync(cursor: null);

        (await ReadAllAsync(connector, "/docs/a.md")).Should().Be("# Second");
    }

    [Fact]
    public async Task ReadFileAsync_NewConnectorOverTheSameMirror_ReadsWithoutFetching()
    {
        Commit(new Dictionary<string, string> { ["a.md"] = "mirrored" });
        await Connector().GetChangesAsync(cursor: null);

        // The pipeline builds a fresh connector per document; it must read what the sync
        // fetched, not whatever upstream has moved on to since.
        Commit(new Dictionary<string, string> { ["a.md"] = "not fetched yet" });

        (await ReadAllAsync(Connector(), "/a.md")).Should().Be("mirrored");
    }

    [Theory]
    [InlineData("/missing.md")]
    [InlineData("/code.cs")]
    [InlineData("/docs")]
    [InlineData("/")]
    public async Task ReadFileAsync_PathThatIsNotADoc_ThrowsFileNotFound(string path)
    {
        Commit(new Dictionary<string, string> { ["docs/a.md"] = "a", ["code.cs"] = "c" });
        var connector = Connector();
        await connector.GetChangesAsync(cursor: null);

        Func<Task> act = () => connector.ReadFileAsync(path);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ReadFileAsync_BeforeAnySync_ThrowsFileNotFound()
    {
        Func<Task> act = () => Connector().ReadFileAsync("/a.md");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ExistsAsync_ReflectsTheMirroredHead()
    {
        Commit(new Dictionary<string, string> { ["a.md"] = "a" });
        var connector = Connector();

        (await connector.ExistsAsync("/a.md")).Should().BeFalse("nothing has been fetched yet");

        await connector.GetChangesAsync(cursor: null);

        (await connector.ExistsAsync("/a.md")).Should().BeTrue();
        (await connector.ExistsAsync("/b.md")).Should().BeFalse();
    }

    [Fact]
    public async Task ListFilesAsync_Prefix_LimitsToThatDirectory()
    {
        Commit(new Dictionary<string, string> { ["docs/a.md"] = "a", ["docsx/b.md"] = "b", ["c.md"] = "c" });
        var connector = Connector();
        await connector.GetChangesAsync(cursor: null);

        var files = await connector.ListFilesAsync("docs");

        files.Select(f => f.Path).Should().Equal("/docs/a.md");
    }
}
