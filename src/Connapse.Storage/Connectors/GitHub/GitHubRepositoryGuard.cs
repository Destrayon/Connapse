using Connapse.Core.Utilities;

namespace Connapse.Storage.Connectors.GitHub;

/// <summary>
/// The check every GitHub sync makes before reading: is this repository still public?
/// <para>
/// Needed once reads are authenticated. Anonymously, a private repository simply refused to be
/// read; an installation token that covers it reads it without complaint. Until per-user
/// permission filtering exists, indexed content is searchable by everyone, so a repository that
/// turns private must stop being read and be hidden — the sync engine hides a source whose read
/// throws <see cref="GitHubRepositoryUnavailableException"/>.
/// </para>
/// </summary>
internal static class GitHubRepositoryGuard
{
    private sealed record RepositoryPayload(long Id, bool Private, string? Visibility);

    /// <exception cref="GitHubRepositoryUnavailableException">Private, internal, or gone.</exception>
    public static async Task RequirePublicAsync(GitHubApiClient api, GitHubConnectorConfig config, CancellationToken ct)
    {
        RepositoryPayload repo;
        try
        {
            repo = await api.GetAsync<RepositoryPayload>(
                $"repos/{Uri.EscapeDataString(config.Owner)}/{Uri.EscapeDataString(config.Repo)}", ct);
        }
        catch (GitHubNotFoundException ex)
        {
            throw Unavailable(config, ex);
        }

        // "internal" repositories are visible to an enterprise's members, not to the public.
        bool isPublic = repo.Visibility is { } visibility
            ? string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase)
            : !repo.Private;

        if (!isPublic)
        {
            throw Unavailable(config, new InvalidOperationException(
                $"GitHub reports the repository as {repo.Visibility ?? "private"}."));
        }
    }

    public static GitHubRepositoryUnavailableException Unavailable(GitHubConnectorConfig config, Exception inner) =>
        new($"GitHub repository {LogSanitizer.Sanitize(config.Owner)}/{LogSanitizer.Sanitize(config.Repo)} "
            + "is no longer public, or can no longer be read — it may have been made private, renamed, or deleted. "
            + "Its documents are hidden from search, and syncing resumes if it becomes public again.", inner);
}
