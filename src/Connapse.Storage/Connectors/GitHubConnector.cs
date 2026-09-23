using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors.GitHub;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Thrown when a repository that was public can no longer be read without a credential —
/// it was made private, deleted, or renamed away. GitHub answers all three the same way to an
/// anonymous client, so they cannot be told apart and are not guessed at.
/// </summary>
public sealed class GitHubRepositoryUnavailableException(string message, Exception inner)
    : SourceAccessRevokedException(message, inner);

/// <summary>
/// Read access to a public GitHub repository, unauthenticated.
/// <para>
/// The docs kind keeps a bare mirror of the default branch on local disk and syncs by
/// fetching into it, because git transport sits outside the REST API's 60-requests-an-hour
/// anonymous budget — and a spike showed ETag revalidation still spends that budget. The
/// cursor is the head commit SHA, so a delta is a tree diff between two commits the mirror
/// already holds.
/// </para>
/// <para>
/// Reads come from the mirror too, never the network. The pipeline builds a fresh connector
/// for every document it ingests, and a network read per file would be one request per
/// document against a host that rate-limits anonymous clients; the mirror has every blob the
/// last fetch saw. Only <see cref="GetChangesAsync"/> fetches, so the mirror has one writer —
/// the per-source sync gate keeps that to one at a time.
/// </para>
/// <para>
/// The issues-and-pull-requests kind is delegated to <see cref="GitHubRecordSource"/>, which
/// reads the REST API through <paramref name="http"/>; the docs kind never touches it.
/// </para>
/// </summary>
public sealed class GitHubConnector(
    GitHubConnectorConfig config, HttpClient? http = null, ILogger? logger = null, GitHubAuth? auth = null)
    : ISyncCursorConnector
{
    /// <summary>
    /// Where the fetched default branch is kept. Outside refs/heads and refs/remotes so
    /// nothing in libgit2 treats it as a branch to track or prune.
    /// </summary>
    internal const string HeadRef = "refs/connapse/head";

    private const string RemoteName = "origin";

    private readonly Regex[] _include = CompileGlobs(
        config.IncludePatterns.Count > 0 ? config.IncludePatterns : GitHubConnectorConfig.DefaultDocPatterns);

    private readonly Regex[] _exclude = CompileGlobs(config.ExcludePatterns);

    public ConnectorType Type => ConnectorType.GitHub;

    public bool SupportsLiveWatch => false;

    private readonly GitHubRecordSource? _records = config.Kind == GitHubContentKind.IssuesAndPullRequests
        ? new GitHubRecordSource(
            config,
            http ?? throw new ArgumentNullException(nameof(http), "The issues kind reads the GitHub API and needs an HttpClient."),
            logger ?? NullLogger.Instance,
            auth: auth)
        : null;

    /// <summary>The docs kind's REST access, for the visibility check only. Null without credentials.</summary>
    private readonly GitHubApiClient? _api = auth is not null && http is not null
        ? new GitHubApiClient(http, config.ApiBaseUrl, auth)
        : null;

    internal GitHubConnectorConfig Config => config;

    public async Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct = default)
    {
        if (_records is not null)
            return await _records.GetChangesAsync(cursor, ct);

        // Asked of the API rather than inferred from the fetch: with a token, a private repository
        // fetches as happily as a public one.
        string? token = null;
        if (auth is not null)
        {
            if (_api is not null && config.RequirePublic)
                await GitHubRepositoryGuard.RequirePublicAsync(_api, config, ct);

            token = (await auth.AcquireAsync(new HashSet<long>(), ct)).Token;
        }

        // LibGit2Sharp is synchronous; a fetch of a large repository can take minutes.
        var delta = await Task.Run(() =>
        {
            using var repo = OpenOrInitMirror();
            Fetch(repo, token, ct);

            var head = repo.Lookup<Commit>(HeadRef)
                ?? throw new InvalidOperationException(
                    $"Fetched {Describe()} but its default branch did not resolve to a commit.");

            if (cursor is null)
                return new SyncDelta(ListMatching(head), [], head.Sha, RequiresFullResync: false, IsFullListing: true);

            if (string.Equals(cursor, head.Sha, StringComparison.OrdinalIgnoreCase))
                return new SyncDelta([], [], head.Sha, RequiresFullResync: false);

            // The previous head is looked up locally, not on the remote. The mirror keeps every
            // object it has fetched, so even a force-push that drops the old commit from
            // GitHub's history still leaves both trees here to diff. Missing means the mirror
            // itself was lost, and only a full listing can re-establish where things stand.
            Commit? previous = ObjectId.TryParse(cursor, out ObjectId? id) ? repo.Lookup<Commit>(id) : null;
            if (previous is null)
                return new SyncDelta([], [], NextCursor: null, RequiresFullResync: true);

            return Diff(repo, previous, head);
        }, ct);

        // Asked again after the fetch: a repository made private between the first check and the
        // fetch would otherwise hand its private head to the index.
        if (auth is not null && _api is not null && config.RequirePublic)
            await GitHubRepositoryGuard.RequirePublicAsync(_api, config, ct);

        return delta;
    }

    public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
    {
        if (_records is not null)
            return Task.FromResult(_records.List(prefix));

        using var repo = OpenExistingMirror();
        var head = RequireHead(repo);

        IReadOnlyList<ConnectorFile> files = ListMatching(head);
        if (!string.IsNullOrEmpty(prefix))
        {
            string normalized = "/" + prefix.Trim('/');
            files = [.. files.Where(f => f.Path.StartsWith(normalized + "/", StringComparison.Ordinal))];
        }

        return Task.FromResult(files);
    }

    public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        if (_records is not null)
        {
            var record = _records.Find(path)
                ?? throw new FileNotFoundException(
                    $"'{LogSanitizer.Sanitize(path)}' is not a synced record of {Describe()}.");
            return Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(record.Markdown)));
        }

        using var repo = OpenExistingMirror();
        var blob = FindDoc(RequireHead(repo), path)
            ?? throw new FileNotFoundException(
                $"'{LogSanitizer.Sanitize(path)}' is not a document in the mirrored head of {Describe()}.");

        // Copied out because the blob's stream reads through the repository handle, which is
        // disposed on return. Documents are markdown files, so the copy is small.
        var buffer = new MemoryStream();
        using (var content = blob.GetContentStream())
        {
            content.CopyTo(buffer);
        }
        buffer.Position = 0;

        return Task.FromResult<Stream>(buffer);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        if (_records is not null)
            return Task.FromResult(_records.Find(path) is not null);

        if (!Repository.IsValid(config.MirrorPath))
            return Task.FromResult(false);

        using var repo = new Repository(config.MirrorPath);
        var head = repo.Lookup<Commit>(HeadRef);

        return Task.FromResult(head is not null && FindDoc(head, path) is not null);
    }

    /// <summary>
    /// Returned unchanged. A document's path is already the repo-relative form this connector
    /// reads, and the pipeline passes it straight back for source-owned documents.
    /// </summary>
    public string ResolveJobPath(string relativePath) => relativePath;

    public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("GitHub sources are polled; they do not support live watch.");

    // ── Mirror ─────────────────────────────────────────────────────────────

    private Repository OpenOrInitMirror()
    {
        if (!Repository.IsValid(config.MirrorPath))
        {
            Directory.CreateDirectory(config.MirrorPath);
            Repository.Init(config.MirrorPath, isBare: true);
        }

        var repo = new Repository(config.MirrorPath);

        // Re-pointed rather than trusted, so a source whose owner/repo was edited fetches from
        // the new address instead of the one the mirror was created for.
        string url = config.EffectiveRemoteUrl;
        var remote = repo.Network.Remotes[RemoteName];
        if (remote is null)
            repo.Network.Remotes.Add(RemoteName, url);
        else if (!string.Equals(remote.Url, url, StringComparison.Ordinal))
            repo.Network.Remotes.Update(RemoteName, r => r.Url = url);

        return repo;
    }

    private Repository OpenExistingMirror()
    {
        if (!Repository.IsValid(config.MirrorPath))
            throw new FileNotFoundException(
                $"{Describe()} has not been fetched on this host yet; the next sync creates its mirror.");

        return new Repository(config.MirrorPath);
    }

    private Commit RequireHead(Repository repo) =>
        repo.Lookup<Commit>(HeadRef)
        ?? throw new FileNotFoundException(
            $"{Describe()} has no fetched head yet; the next sync fetches it.");

    private void Fetch(Repository repo, string? token, CancellationToken ct)
    {
        var options = new FetchOptions
        {
            // The default branch only: it is what the docs are read from, and every other ref
            // on a busy repository — thousands of pull-request heads — is weight for nothing.
            TagFetchMode = TagFetchMode.None,

            // Returning false from the progress callback is how libgit2 is cancelled.
            OnTransferProgress = _ => !ct.IsCancellationRequested,
        };

        // An installation token is a password for the `x-access-token` user over HTTPS. Anonymous
        // clones are throttled too, so reading as the App keeps docs off that limit as well.
        if (token is not null)
        {
            options.CredentialsProvider = (_, _, _) =>
                new UsernamePasswordCredentials { Username = "x-access-token", Password = token };
        }

        try
        {
            Commands.Fetch(repo, RemoteName, [$"+HEAD:{HeadRef}"], options, logMessage: null);
        }
        catch (LibGit2SharpException ex) when (IsAuthenticationRefusal(ex))
        {
            // GitHub answers 401 for a repository the fetch may not read: without a token, any
            // that is not public (confirmed against github.com, 2026-09-22); with one, any the
            // installation does not cover. Either way the source is hidden, not deleted.
            throw GitHubRepositoryGuard.Unavailable(config, ex);
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// True for libgit2's GIT_EAUTH. LibGit2Sharp surfaces no error code for it, so the
    /// message is the only signal; both wordings libgit2 uses are matched.
    /// </summary>
    internal static bool IsAuthenticationRefusal(LibGit2SharpException ex) =>
        ex.Message.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("authentication replays", StringComparison.OrdinalIgnoreCase);

    // ── Tree walking ───────────────────────────────────────────────────────

    private SyncDelta Diff(Repository repo, Commit previous, Commit head)
    {
        var upserted = new List<ConnectorFile>();
        var deleted = new List<string>();

        foreach (var change in repo.Diff.Compare<TreeChanges>(previous.Tree, head.Tree))
        {
            switch (change.Status)
            {
                case ChangeKind.Added or ChangeKind.Modified or ChangeKind.TypeChanged or ChangeKind.Copied:
                    AddIfDoc(head, change.Path, upserted, deleted, wasDoc: IsDocMode(change.OldMode) && Matches(change.Path));
                    break;

                case ChangeKind.Renamed:
                    if (IsDocMode(change.OldMode) && Matches(change.OldPath))
                        deleted.Add(ToVirtualPath(change.OldPath));
                    AddIfDoc(head, change.Path, upserted, deleted, wasDoc: false);
                    break;

                case ChangeKind.Deleted:
                    if (IsDocMode(change.OldMode) && Matches(change.OldPath))
                        deleted.Add(ToVirtualPath(change.OldPath));
                    break;
            }
        }

        return new SyncDelta(upserted, deleted, head.Sha, RequiresFullResync: false);
    }

    /// <summary>
    /// Upserts the path if it is a document at head. A path that was a document and no longer
    /// is — a markdown file replaced by a symlink — is reported deleted instead.
    /// </summary>
    private void AddIfDoc(Commit head, string path, List<ConnectorFile> upserted, List<string> deleted, bool wasDoc)
    {
        if (head[path] is { } entry && IsDoc(entry, path))
            upserted.Add(ToConnectorFile(entry, head));
        else if (wasDoc)
            deleted.Add(ToVirtualPath(path));
    }

    private List<ConnectorFile> ListMatching(Commit head)
    {
        var files = new List<ConnectorFile>();
        var pending = new Stack<Tree>([head.Tree]);

        while (pending.TryPop(out var tree))
        {
            foreach (var entry in tree)
            {
                // Submodules (GitLink) are other repositories and are not followed.
                if (entry.TargetType == TreeEntryTargetType.Tree)
                    pending.Push((Tree)entry.Target);
                else if (IsDoc(entry, entry.Path))
                    files.Add(ToConnectorFile(entry, head));
            }
        }

        return files;
    }

    private Blob? FindDoc(Commit head, string path)
    {
        string relative = path.TrimStart('/');
        if (relative.Length == 0)
            return null;

        return head[relative] is { } entry && IsDoc(entry, relative) ? (Blob)entry.Target : null;
    }

    /// <summary>
    /// A regular file matching the globs. Symlinks are refused: their blob is a target path,
    /// not content, and a link pointing outside the docs is not a doc.
    /// </summary>
    private bool IsDoc(TreeEntry entry, string path) =>
        entry.TargetType == TreeEntryTargetType.Blob && IsDocMode(entry.Mode) && Matches(path);

    private static bool IsDocMode(Mode mode) =>
        mode is Mode.NonExecutableFile or Mode.ExecutableFile;

    private bool Matches(string path)
    {
        string name = Path.GetFileName(path);
        return _include.Any(g => IsMatch(g, name)) && !_exclude.Any(g => IsMatch(g, name));
    }

    private ConnectorFile ToConnectorFile(TreeEntry entry, Commit head)
    {
        string escaped = string.Join('/', entry.Path.Split('/').Select(Uri.EscapeDataString));

        return new ConnectorFile(
            Path: ToVirtualPath(entry.Path),
            SizeBytes: ((Blob)entry.Target).Size,

            // Head's commit time, not the file's last-touching commit: finding that walks
            // history per file. Only changed files are reported on the delta path, so the
            // coarser value costs nothing there.
            LastModified: head.Committer.When.UtcDateTime,
            ContentType: null,

            // HEAD rather than a SHA, so the link follows the default branch and does not churn
            // on every commit.
            ResourceUri: $"{config.WebUrl}/blob/HEAD/{escaped}");
    }

    private static string ToVirtualPath(string repoPath) => "/" + repoPath;

    private string Describe() =>
        $"GitHub repository {LogSanitizer.Sanitize(config.Owner)}/{LogSanitizer.Sanitize(config.Repo)}";

    // ── Globs ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Compiled once, with a timeout, for the reason given on SftpConnector's copy: a pattern
    /// from a source's scope must not be able to hang the sync thread by backtracking.
    /// </summary>
    private static Regex[] CompileGlobs(IReadOnlyList<string> patterns) =>
        [.. patterns.Select(p => new Regex(
            "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250)))];

    private static bool IsMatch(Regex glob, string name)
    {
        try
        {
            return glob.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
