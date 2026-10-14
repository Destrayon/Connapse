using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Components.Connections;
using Connapse.Web.Components.Sources;

namespace Connapse.Web.Services;

/// <summary>
/// Checks a Jira source's scope the way the New source dialog does, for the REST API: the site's
/// last test must have passed for Jira, the project must be one the site's service account can
/// browse now and not already a source on the connection, and the attachment cap must be in range.
/// The scope stored is rebuilt from Jira's answer, as for Confluence. Field names are the scope keys
/// the dialog writes: <c>projectId</c>, <c>includeAttachments</c>, <c>maxAttachmentMb</c>.
/// </summary>
public sealed class JiraSourceScope : AtlassianSourceScope
{
    public const string JiraNotReady = "Jira sources can be added once this site passes a test. Choose Test connection on the Connections page.";

    private static readonly ScopeTexts Texts = new(
        NotAnObject: "The scope must be a JSON object naming projectId, as in {\"kind\":\"jira-project\",\"projectId\":\"10000\"}.",
        IdError: "\"projectId\" must be a Jira project id, a string of digits such as \"10000\".",
        MissingId: connectionId =>
            $"A Jira source's scope needs \"projectId\": the id of a project on the site. List them with GET /api/v1/atlassian/sites/{connectionId}/projects.",
        Unavailable: "Could not check the project against this site. Try again.",
        NotOnSite: (projectId, connectionId) =>
            $"Project {projectId} is not a project the site's service account can browse. Let the service account see it (Viewer under Space settings (Project settings) → Access "
            + "on a private team-managed space, or Browse Spaces (Browse Projects) in a company-managed space's permission scheme), "
            + $"or pick an id from GET /api/v1/atlassian/sites/{connectionId}/projects.",
        Duplicate: name => $"Project '{name}' already has a source on this connection.",
        IdFixed: "A Jira source's project cannot be changed. Add the project as a new source.");

    private JiraSourceScope()
    {
    }

    // The New source dialog refuses the same sites: one whose last test didn't pass for Jira.
    public static Task<Result> ResolveAsync(
        string? scopeJson, Connection connection, AtlassianSiteService sites, ISourceStore sourceStore, CancellationToken ct) =>
        ResolveAsync<JiraProjectInfo, JiraProjectSelection>(scopeJson, connection, sites, sourceStore, Texts, AtlassianProduct.Jira,
            NotReady(connection), ct);

    /// <summary>
    /// Why no Jira source may be added on <paramref name="connection"/> now, or null when one may.
    /// The API and the New source dialog both ask this, against the connection as stored.
    /// </summary>
    public static string? NotReady(Connection connection) =>
        AtlassianSiteStatus.IsReady(connection, AtlassianSiteScopes.JiraProduct) ? null
        : AtlassianSiteStatus.Of(connection).NeedsNewCredentials ? NeedsNewCredentials("Jira")
        : JiraNotReady;

    /// <summary>
    /// A changed scope for an existing Jira source. Its project is fixed, as a Confluence source's
    /// space is; only whether attachments are read, and how large, may change.
    /// </summary>
    public static Result Update(string currentScopeJson, string? newScopeJson) =>
        Update(currentScopeJson, newScopeJson, "projectId", Texts);
}
