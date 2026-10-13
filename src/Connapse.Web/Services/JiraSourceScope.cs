using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
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
    private const string ProjectIdError = "\"projectId\" must be a Jira project id, a string of digits such as \"10000\".";

    public const string JiraNotReady = "This site's last test didn't pass for Jira. Test it on the Connections page.";

    private JiraSourceScope()
    {
    }

    public static async Task<Result> ResolveAsync(
        string? scopeJson, Connection connection, AtlassianSiteService sites, ISourceStore sourceStore, CancellationToken ct)
    {
        if (Read(scopeJson, "projectId", ProjectIdError) is not { } parsed)
            return Result.Fail(Outcome.Invalid, "scopeJson", "The scope must be a JSON object naming projectId, as in {\"kind\":\"jira-project\",\"projectId\":\"10000\"}.");
        if (parsed.Error is not null)
            return Result.Fail(Outcome.Invalid, parsed.Field!, parsed.Error);
        if (parsed.Id is not { } projectId)
            return Result.Fail(Outcome.Invalid, "projectId",
                $"A Jira source's scope needs \"projectId\": the id of a project on the site. List them with GET /api/v1/atlassian/sites/{connection.Id}/projects.");

        // A site last tested before Jira was, or whose Jira probe failed, is offered no Jira source on the dialog either.
        if (!AtlassianSiteStatus.IsReady(connection, AtlassianSiteScopes.JiraProduct))
            return Result.Fail(Outcome.Invalid, "connectionId", JiraNotReady);

        var selection = new JiraProjectSelection
        {
            // Left out, attachments are on only when the site's last test confirmed their scope, as on the dialog.
            IncludeAttachments = parsed.IncludeAttachments ?? JiraProjectSelection.AttachmentScopeConfirmed(connection.Health),
            MaxAttachmentMb = parsed.MaxAttachmentMb ?? JiraProjectSelection.DefaultMaxAttachmentMb,
        };
        selection.Selected.Add(projectId);
        if (selection.Validate() is not null)
            return Result.Fail(Outcome.Invalid, "maxAttachmentMb", selection.MaxAttachmentMbError!);

        if (SiteOf(connection, out string siteHost) is { } incomplete)
            return incomplete;

        // Asked now, as the site's service account, exactly as the dialog asks before it creates anything.
        var onSite = await sites.ListProjectsAsync(connection.Id, ct);
        if (onSite.Outcome != AtlassianSpacesOutcome.Listed)
            return Result.Fail(Outcome.Unavailable, "projectId",
                onSite.Error ?? "Could not check the project against this site. Try again.");

        var verified = selection.VerifiedAgainst(onSite.Projects!, out var refused);
        if (refused.Count > 0)
            return Result.Fail(Outcome.Invalid, "projectId",
                $"Project {projectId} is not a project the site's service account can browse. Give the service account the Browse projects permission in it, "
                + $"or pick an id from GET /api/v1/atlassian/sites/{connection.Id}/projects.");

        var existing = JiraProjectSelection.ProjectIdsOf(await sourceStore.ListByConnectionAsync(connection.Id, ct));
        var requests = verified.ToRequests(connection.Id, siteHost, existing, out var skipped);
        if (skipped.Count > 0)
            return Result.Fail(Outcome.Duplicate, "projectId", $"Project '{skipped[0]}' already has a source on this connection.");

        var request = requests.Single();
        return new Result(Outcome.Valid, request.ScopeJson, request.Name);
    }

    /// <summary>
    /// A changed scope for an existing Jira source. Its project is fixed, as a Confluence source's
    /// space is; only whether attachments are read, and how large, may change.
    /// </summary>
    public static Result Update(string currentScopeJson, string? newScopeJson) =>
        Update(currentScopeJson, newScopeJson, "projectId", ProjectIdError,
            "A Jira source's project cannot be changed. Add the project as a new source.");
}
