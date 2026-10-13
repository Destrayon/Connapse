using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Components.Sources;

namespace Connapse.Web.Services;

/// <summary>
/// Checks a Confluence source's scope the way the New source dialog does, for the REST API: the
/// space must be one the site's service account can view now and not already a source on the
/// connection, and the attachment cap must be in range. The scope stored is rebuilt from
/// Atlassian's answer, so an API-created source matches a UI-created one. Field names are the
/// scope keys the dialog writes: <c>spaceId</c>, <c>includeAttachments</c>, <c>maxAttachmentMb</c>.
/// </summary>
public sealed class ConfluenceSourceScope : AtlassianSourceScope
{
    private static readonly ScopeTexts Texts = new(
        NotAnObject: "The scope must be a JSON object naming spaceId, as in {\"spaceId\":\"98306\"}.",
        IdError: "\"spaceId\" must be a Confluence space id, a string of digits such as \"98306\".",
        MissingId: connectionId =>
            $"A Confluence source's scope needs \"spaceId\": the id of a space on the site. List them with GET /api/v1/atlassian/sites/{connectionId}/spaces.",
        Unavailable: "Could not check the space against this site. Try again.",
        NotOnSite: (spaceId, connectionId) =>
            $"Space {spaceId} is not a space the site's service account can view. Give the service account the Viewer role in that space, "
            + $"or pick an id from GET /api/v1/atlassian/sites/{connectionId}/spaces.",
        Duplicate: name => $"Space '{name}' already has a source on this connection.",
        IdFixed: "A Confluence source's space cannot be changed. Add the space as a new source.");

    private ConfluenceSourceScope()
    {
    }

    public static Task<Result> ResolveAsync(
        string? scopeJson, Connection connection, AtlassianSiteService sites, ISourceStore sourceStore, CancellationToken ct) =>
        ResolveAsync<ConfluenceSpaceInfo, ConfluenceSpaceSelection>(scopeJson, connection, sites, sourceStore, Texts, notReady: null, ct);

    /// <summary>
    /// A changed scope for an existing Confluence source. Its space is fixed: indexed documents are
    /// keyed to it, so moving a source to another space in place is refused. Only what to read from
    /// the space may change, under the same rules as when it was created.
    /// </summary>
    public static Result Update(string currentScopeJson, string? newScopeJson) =>
        Update(currentScopeJson, newScopeJson, "spaceId", Texts);
}
