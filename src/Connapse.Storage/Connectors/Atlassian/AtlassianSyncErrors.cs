using Connapse.Core;
using Connapse.Core.Utilities;
using Connapse.Storage.ConnectionTesters;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>
/// The messages a Confluence sync records when it fails, each saying what happened and what to do,
/// and the setup guide's troubleshooting entry for each. Atlassian's own answer (status, error code)
/// travels as the exception's <see cref="IErrorDetail.Detail"/>, never in the message.
/// </summary>
public static class AtlassianSyncErrors
{
    public const string CredentialRefused =
        "Atlassian refused this site's service account credential, so Connapse can't read the site. "
        + "On the Connections page, open the site and choose Replace credentials; its sources sync again after that.";

    public const string TokenRedirected =
        "Atlassian's sign-in service answered with a redirect, which Connapse doesn't follow with a service account "
        + "credential. Check that no proxy redirects this server's requests to auth.atlassian.com, then sync again.";

    public const string NoAccessToken =
        "Atlassian's sign-in service answered without an access token. Wait a few minutes and sync again.";

    public const string FreshTokenRefused =
        "Atlassian refused the service account's requests even with a new token. Check that the service account has "
        + "a Confluence role on this site and that its credential has every scope the setup guide lists.";

    public const string Unavailable =
        "Confluence on this site didn't answer properly, so nothing changed this cycle. Connapse tries again on the "
        + "next sync. If it keeps failing, check that this server can reach api.atlassian.com over HTTPS.";

    public const string JiraUnavailable =
        "Jira on this site didn't answer properly, so nothing changed this cycle. Connapse tries again on the "
        + "next sync. If it keeps failing, check that this server can reach api.atlassian.com over HTTPS.";

    /// <summary>
    /// True only when Atlassian's token endpoint refused the client ID or secret. A redirect or a
    /// token answer without a token also fails at the token step, but says nothing about the
    /// credential. The site test and a sync both decide "needs new credentials" with this.
    /// </summary>
    public static bool IsCredentialRefused(AtlassianAuthException ex) =>
        ex.TokenRefused && ex.Message == CredentialRefused;

    private const string SpacePrefix = "Confluence space ";
    private const string SpaceUnreadablePhrase = " can no longer be read";

    /// <summary>A space that answered 403 (access taken away) or 404 (deleted, or no longer visible).</summary>
    public static string SpaceUnreadable(string spaceKey, int status)
    {
        string key = LogSanitizer.Sanitize(spaceKey);
        return status == 404
            ? $"{SpacePrefix}{key}{SpaceUnreadablePhrase}: it was deleted, or the service account can no longer see it. "
              + "If the space still exists, give the service account the Viewer role in it (Space settings → Users), then "
              + "sync again; if it was deleted, delete this source. Its documents are hidden from search until then."
            : $"{SpacePrefix}{key}{SpaceUnreadablePhrase}: the service account lost access to it. Give the service "
              + "account the Viewer role in the space (Space settings → Users), then sync again. Its documents are "
              + "hidden from search until then.";
    }

    /// <summary>A Jira project that answered 403 (access taken away) or 404 (deleted, or no longer visible).</summary>
    public static string JiraProjectUnreadable(string projectKey, int status)
    {
        string key = LogSanitizer.Sanitize(projectKey);
        return status == 404
            ? $"Jira project {key} can no longer be read: it was deleted, or the service account can no longer see it. "
              + "If the project still exists, give the service account the Browse projects permission in it, then sync "
              + "again; if it was deleted, delete this source. Its documents are hidden from search until then."
            : $"Jira project {key} can no longer be read: the service account lost access to it. Give the service "
              + "account the Browse projects permission in the project, then sync again. Its documents are hidden "
              + "from search until then.";
    }

    /// <summary>
    /// The troubleshooting entry for a recorded sync error or notice. An error this class did not
    /// write gets the guide's Troubleshooting section as a whole.
    /// </summary>
    public static string TroubleshootingAnchor(string error)
    {
        string message = SyncErrorText.Split(error).Message;
        if (message.StartsWith(SpacePrefix, StringComparison.Ordinal) && message.Contains(SpaceUnreadablePhrase, StringComparison.Ordinal))
            return AtlassianSiteTroubleshooting.SpaceUnreadable;
        if (message.StartsWith(CredentialRefused, StringComparison.Ordinal))
            return AtlassianSiteTroubleshooting.CredentialsRejected;
        if (message.StartsWith(FreshTokenRefused, StringComparison.Ordinal))
            return AtlassianSiteTroubleshooting.CantReadConfluence;
        if (message.StartsWith(TokenRedirected, StringComparison.Ordinal) || message.StartsWith(NoAccessToken, StringComparison.Ordinal)
            || message.StartsWith(Unavailable, StringComparison.Ordinal) || message.StartsWith(JiraUnavailable, StringComparison.Ordinal))
            return AtlassianSiteTroubleshooting.Unreachable;
        if (message.Contains(ConfluenceSpaceConnector.AttachmentScopeNotice, StringComparison.Ordinal))
            return AtlassianSiteTroubleshooting.MissingScope;
        if (message.Contains(ConfluenceSpaceConnector.SkippedAttachmentsPhrase, StringComparison.Ordinal))
            return AtlassianSiteTroubleshooting.AttachmentsSkipped;
        return AtlassianSiteTroubleshooting.General;
    }

    /// <summary>The setup guide address of <see cref="TroubleshootingAnchor"/>.</summary>
    public static string TroubleshootingUrl(string error) => AtlassianSiteTroubleshooting.UrlOf(TroubleshootingAnchor(error));
}
