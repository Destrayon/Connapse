using System.Text.RegularExpressions;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Which of a repository's two content kinds a GitHub source indexes. A repository is
/// represented by up to two sources, one per kind, so each keeps one cursor and one chunker.
/// </summary>
public enum GitHubContentKind
{
    /// <summary>Markdown files in the default branch, synced by git fetch. Cursor: commit SHA.</summary>
    Docs,

    /// <summary>Issues and pull requests, synced through the REST API. Cursor: updated-at.</summary>
    IssuesAndPullRequests,
}

/// <summary>
/// Everything a <see cref="GitHubConnector"/> needs: which repository, which content kind,
/// what to pick up, and where its local mirror lives.
/// </summary>
public partial record GitHubConnectorConfig
{
    /// <summary>
    /// Connection-less sources are pinned here. Enterprise Server needs a credential and so
    /// arrives with the GitHub App work, not before.
    /// </summary>
    public const string PublicHost = "github.com";

    /// <summary>The markdown extensions the text parser reads, as globs.</summary>
    public static readonly IReadOnlyList<string> DefaultDocPatterns = ["*.md", "*.markdown"];

    public string Owner { get; init; } = "";

    public string Repo { get; init; } = "";

    /// <summary>
    /// GitHub's numeric repository id, which survives renames and transfers. Null until the
    /// issues sync (Phase 3) first records it; the docs sync does not need it.
    /// </summary>
    public long? RepoId { get; init; }

    public GitHubContentKind Kind { get; init; } = GitHubContentKind.Docs;

    public string Host { get; init; } = PublicHost;

    /// <summary>File-name globs, matched case-insensitively. Empty means the markdown defaults.</summary>
    public IReadOnlyList<string> IncludePatterns { get; init; } = DefaultDocPatterns;

    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];

    /// <summary>
    /// This source's local directory: the bare git mirror for docs, the record store for issues.
    /// </summary>
    public string MirrorPath { get; init; } = "";

    /// <summary>
    /// Where to fetch from. Null means the public HTTPS clone URL for
    /// <see cref="Owner"/>/<see cref="Repo"/> on <see cref="Host"/>, which is the only value the
    /// factory ever produces; tests point it at a local repository instead.
    /// </summary>
    public string? RemoteUrl { get; init; }

    public string EffectiveRemoteUrl => RemoteUrl ?? $"https://{Host}/{Owner}/{Repo}.git";

    /// <summary>
    /// The REST API the issues kind reads. Like <see cref="RemoteUrl"/>, only tests change it;
    /// the factory always leaves the public API.
    /// </summary>
    public string ApiBaseUrl { get; init; } = "https://api.github.com";

    /// <summary>
    /// Whether an issue's comments are assembled into its document. On by default: the
    /// discussion is most of what an issue knows that the code does not.
    /// </summary>
    public bool IncludeComments { get; init; } = true;

    /// <summary>Comment authors indexed even though GitHub marks them as bots.</summary>
    public IReadOnlyList<string> IncludeCommentAuthors { get; init; } = [];

    /// <summary>Comment authors never indexed — a machine user GitHub reports as a person, say.</summary>
    public IReadOnlyList<string> ExcludeCommentAuthors { get; init; } = [];

    /// <summary>Whose comments are indexed: everyone but bots, adjusted by the two lists above.</summary>
    public GitHub.GitHubCommentPolicy CommentPolicy => new(IncludeCommentAuthors, ExcludeCommentAuthors);

    /// <summary>
    /// The GitHub App installation this source's connection names. Preferred for its reads; for a
    /// public repository any installation may stand in when it is spent.
    /// </summary>
    public long? InstallationId { get; init; }

    /// <summary>
    /// Refuse the repository unless GitHub says it is public. On until per-user permission
    /// filtering exists: an installation token can read private repositories, and without that
    /// filter anything indexed is searchable by everyone.
    /// </summary>
    public bool RequirePublic { get; init; } = true;

    /// <summary>
    /// A private repository, searchable only by people GitHub says can read it. Its documents carry
    /// a <c>github://{repoId}/…</c> address for the permission filter; a public repository's carry
    /// none, because everyone may read them.
    /// </summary>
    public bool IsPrivate { get; init; }

    /// <summary>
    /// Whether each sync first confirms the repository with GitHub: that it is still public, for a
    /// public source, and that the name still belongs to the recorded repository id.
    /// </summary>
    public bool Verified => RequirePublic || IsPrivate || RepoId is not null;

    /// <summary>The permission filter's address for a document at <paramref name="virtualPath"/>, or null for a public repository.</summary>
    public string? ResourceUriFor(string virtualPath) =>
        IsPrivate && RepoId is { } id ? $"github://{id}{(virtualPath.StartsWith('/') ? "" : "/")}{virtualPath}" : null;

    /// <summary>The repository's web address, which a document's citation link hangs off.</summary>
    public string WebUrl => $"https://{Host}/{Owner}/{Repo}";

    /// <summary>
    /// GitHub's own rule for account names: alphanumerics and single hyphens, not leading, at
    /// most 39 characters. Checked because the name is spliced into a URL.
    /// </summary>
    public static bool IsValidOwner(string? owner) =>
        !string.IsNullOrEmpty(owner) && OwnerPattern().IsMatch(owner);

    /// <summary>
    /// Alphanumerics, '.', '-' and '_', at most 100 characters — but not '.' or '..', which
    /// GitHub reserves and which would walk the URL up a level.
    /// </summary>
    public static bool IsValidRepo(string? repo) =>
        !string.IsNullOrEmpty(repo) && repo is not "." and not ".." && RepoPattern().IsMatch(repo);

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoPattern();
}
