namespace Connapse.Storage.Connectors.GitHub;

/// <summary>
/// Whether Connapse has a GitHub App and where it is installed: what a GitHub connection's save checks.
/// <see cref="ConnapseGitHubApp"/> answers it; the interface lets the check be tested without GitHub.
/// </summary>
public interface IGitHubAppInstallations
{
    /// <summary>True when an App is stored. Reads the store, not GitHub.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    Task<IReadOnlyList<GitHubAppInstallation>> ListInstallationsAsync(CancellationToken ct = default);
}
