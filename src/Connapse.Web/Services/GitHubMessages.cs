using Connapse.Storage.Connectors.GitHub;

namespace Connapse.Web.Services;

/// <summary>
/// What to tell an administrator when a call to GitHub fails: what went wrong and what to do,
/// in place of GitHub's status codes. The raw error is shown separately, on request.
/// </summary>
public static class GitHubMessages
{
    public const string GuideUrl = "https://github.com/Destrayon/Connapse/blob/main/docs/github-setup.md";
    public const string TroubleshootingUrl = GuideUrl + "#troubleshooting";

    /// <param name="ex">What the call threw.</param>
    /// <param name="account">The organisation or account the installation is on, when there is one.</param>
    public static string Describe(Exception ex, string? account = null)
    {
        string where = string.IsNullOrWhiteSpace(account) ? "that organisation or account" : account;
        return ex switch
        {
            GitHubAppException { StatusCode: 401 } =>
                "GitHub no longer accepts the App's private key. Paste a new key under Manual values on the GitHub provider page.",
            GitHubAppException { StatusCode: 404 } =>
                $"The GitHub App is no longer installed on {where}. Install it again, or delete this connection.",
            GitHubAppException { StatusCode: 403 } =>
                $"GitHub refused the request. The App may be suspended on {where}, or its hourly request limit is used up. Try again later.",
            GitHubAppException { StatusCode: null } app when app.InnerException is System.Security.Cryptography.CryptographicException or ArgumentException =>
                "That private key is not a readable .pem file. Paste the whole file, including the BEGIN and END lines.",
            GitHubAppException { StatusCode: null } app => app.Message,
            HttpRequestException or TaskCanceledException =>
                "GitHub could not be reached. Check that this server can reach api.github.com, then try again.",
            _ => "GitHub gave an answer Connapse could not use. The raw error has the details.",
        };
    }
}
