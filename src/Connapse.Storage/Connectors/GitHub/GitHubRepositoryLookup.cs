namespace Connapse.Storage.Connectors.GitHub;

/// <summary>What GitHub says about a repository before a source is created for it.</summary>
/// <param name="Visibility"><c>public</c>, <c>private</c>, or <c>internal</c>.</param>
public sealed record GitHubRepositoryInfo(long Id, string FullName, string Visibility)
{
    public bool IsPublic => string.Equals(Visibility, "public", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Looks a repository up as a GitHub App installation, for the New source dialog: whether it
/// exists, its id, and whether it is public. A private repository is indexed as private, shown
/// only to linked users GitHub lets read it.
/// </summary>
public sealed class GitHubRepositoryLookup(GitHubCredentialPool pool, IHttpClientFactory httpClients)
{
    /// <summary>The REST API asked. Only tests change it.</summary>
    internal string ApiBaseUrl { get; init; } = "https://api.github.com";

    /// <returns>The repository, or null when GitHub has none by that name that the installation can see.</returns>
    public async Task<GitHubRepositoryInfo?> FindAsync(
        string owner, string repo, long installationId, CancellationToken ct = default)
    {
        var api = new GitHubApiClient(
            httpClients.CreateClient(ConnectorFactory.GitHubHttpClientName), ApiBaseUrl,
            // Pinned: the question is what this installation sees, not what any installation does.
            new GitHubAuth(pool, GitHubAccess.Pinned(installationId)));

        try
        {
            var found = await api.GetAsync<Payload>(
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}", ct);

            return new GitHubRepositoryInfo(
                found.Id, found.FullName ?? $"{owner}/{repo}", found.Visibility ?? (found.Private ? "private" : "public"));
        }
        catch (GitHubNotFoundException)
        {
            return null;
        }
    }

    private sealed record Payload(long Id, string? FullName, bool Private, string? Visibility);
}
