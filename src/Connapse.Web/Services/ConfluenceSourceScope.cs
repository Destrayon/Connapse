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
    private const string SpaceIdError = "\"spaceId\" must be a Confluence space id, a string of digits such as \"98306\".";

    private ConfluenceSourceScope()
    {
    }

    public static async Task<Result> ResolveAsync(
        string? scopeJson, Connection connection, AtlassianSiteService sites, ISourceStore sourceStore, CancellationToken ct)
    {
        if (Read(scopeJson, "spaceId", SpaceIdError) is not { } parsed)
            return Result.Fail(Outcome.Invalid, "scopeJson", "The scope must be a JSON object naming spaceId, as in {\"spaceId\":\"98306\"}.");
        if (parsed.Error is not null)
            return Result.Fail(Outcome.Invalid, parsed.Field!, parsed.Error);
        if (parsed.Id is not { } spaceId)
            return Result.Fail(Outcome.Invalid, "spaceId",
                $"A Confluence source's scope needs \"spaceId\": the id of a space on the site. List them with GET /api/v1/atlassian/sites/{connection.Id}/spaces.");

        var selection = new ConfluenceSpaceSelection
        {
            // Left out, attachments are on only when the site's last test confirmed their scope, as on the dialog.
            IncludeAttachments = parsed.IncludeAttachments ?? ConfluenceSpaceSelection.AttachmentScopeConfirmed(connection.Health),
            MaxAttachmentMb = parsed.MaxAttachmentMb ?? ConfluenceSpaceSelection.DefaultMaxAttachmentMb,
        };
        selection.Selected.Add(spaceId);
        if (selection.Validate() is not null)
            return Result.Fail(Outcome.Invalid, "maxAttachmentMb", selection.MaxAttachmentMbError!);

        if (SiteOf(connection, out string siteHost) is { } incomplete)
            return incomplete;

        // Asked now, as the site's service account, exactly as the dialog asks before it creates anything.
        var onSite = await sites.ListSpacesAsync(connection.Id, includePersonal: true, ct);
        if (onSite.Outcome != AtlassianSpacesOutcome.Listed)
            return Result.Fail(Outcome.Unavailable, "spaceId",
                onSite.Error ?? "Could not check the space against this site. Try again.");

        var verified = selection.VerifiedAgainst(onSite.Spaces!, out var refused);
        if (refused.Count > 0)
            return Result.Fail(Outcome.Invalid, "spaceId",
                $"Space {spaceId} is not a space the site's service account can view. Give the service account the Viewer role in that space, "
                + $"or pick an id from GET /api/v1/atlassian/sites/{connection.Id}/spaces.");

        var existing = ConfluenceSpaceSelection.SpaceIdsOf(await sourceStore.ListByConnectionAsync(connection.Id, ct));
        var requests = verified.ToRequests(connection.Id, siteHost, existing, out var skipped);
        if (skipped.Count > 0)
            return Result.Fail(Outcome.Duplicate, "spaceId", $"Space '{skipped[0]}' already has a source on this connection.");

        var request = requests.Single();
        return new Result(Outcome.Valid, request.ScopeJson, request.Name);
    }

    /// <summary>
    /// A changed scope for an existing Confluence source. Its space is fixed: indexed documents are
    /// keyed to it, so moving a source to another space in place is refused. Only what to read from
    /// the space may change, under the same rules as when it was created.
    /// </summary>
    public static Result Update(string currentScopeJson, string? newScopeJson) =>
        Update(currentScopeJson, newScopeJson, "spaceId", SpaceIdError,
            "A Confluence source's space cannot be changed. Add the space as a new source.");
}
