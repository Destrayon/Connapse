namespace Connapse.Core;

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
