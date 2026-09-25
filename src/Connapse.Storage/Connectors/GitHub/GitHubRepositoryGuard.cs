using Connapse.Core.Utilities;

namespace Connapse.Storage.Connectors.GitHub;

/// <summary>
/// The check every GitHub sync makes before reading: is this still the repository the source
/// indexes, and, for a public source, is it still public?
/// <para>
/// An installation token reads a private repository without complaint, and a public source's
/// content is searchable by everyone, so a public repository that turns private must stop being
/// read and be hidden. The sync engine hides a source whose read throws
/// <see cref="GitHubRepositoryUnavailableException"/>.
/// </para>
/// </summary>
internal static class GitHubRepositoryGuard
{
    private sealed record RepositoryPayload(long Id, bool Private, string? Visibility);

    /// <summary>
    /// Refuses a repository that can no longer be read as this source: gone, no longer public for a
    /// public source, or — for any source that recorded its repository id — a different repository
    /// now answering at the name. A rename keeps the id (GitHub redirects the old name), so it
    /// passes; a transfer or deletion followed by a new repository taking the name does not, and a
    /// private source's documents must never be granted by, or refreshed from, someone else's
    /// repository. Asked conditionally: an unchanged repository answers 304, which costs nothing.
    /// </summary>
    /// <returns>The ETag to send next time.</returns>
    /// <exception cref="GitHubRepositoryUnavailableException">Gone, not public when it must be, or not the recorded repository.</exception>
    public static async Task<string?> VerifyAsync(
        GitHubApiClient api, GitHubConnectorConfig config, string? etag, CancellationToken ct)
    {
        (bool Changed, RepositoryPayload? Value, string? ETag) answer;
        try
        {
            answer = await api.GetIfChangedAsync<RepositoryPayload>(
                $"repos/{Uri.EscapeDataString(config.Owner)}/{Uri.EscapeDataString(config.Repo)}", etag, ct);
        }
        catch (GitHubNotFoundException ex)
        {
            throw Unavailable(config, ex);
        }

        if (!answer.Changed)
            return answer.ETag;

        var repo = answer.Value
            ?? throw Unavailable(config, new InvalidOperationException("GitHub returned no repository."));

        if (config.RepoId is { } expected && repo.Id != expected)
        {
            throw Unavailable(config, new InvalidOperationException(
                $"The name now belongs to repository {repo.Id}, not the recorded {expected}."));
        }

        if (!config.RequirePublic)
            return answer.ETag;

        // "internal" repositories are visible to an enterprise's members, not to the public.
        bool isPublic = repo.Visibility is { } visibility
            ? string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase)
            : !repo.Private;

        if (!isPublic)
        {
            throw Unavailable(config, new InvalidOperationException(
                $"GitHub reports the repository as {repo.Visibility ?? "private"}."));
        }

        return answer.ETag;
    }

    public static GitHubRepositoryUnavailableException Unavailable(GitHubConnectorConfig config, Exception inner) =>
        new(UnavailableMessage(config), inner);

    /// <summary>What an administrator sees when a repository can no longer be read as its source.</summary>
    public static string UnavailableMessage(GitHubConnectorConfig config) =>
        $"GitHub repository {LogSanitizer.Sanitize(config.Owner)}/{LogSanitizer.Sanitize(config.Repo)} can no longer be read"
        + (config.RequirePublic ? " as a public repository" : "")
        + ". It may have been renamed, deleted, " + (config.RequirePublic ? "made private, " : "")
        + "or removed from the App's installation. Its documents are hidden from search until it can be read again.";
}
