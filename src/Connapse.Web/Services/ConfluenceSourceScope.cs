using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Components.Sources;

namespace Connapse.Web.Services;

/// <summary>
/// Checks a Confluence source's scope the way the New source dialog does, for the REST API: the
/// space must be one the site's service account can view now and not already a source on the
/// connection, and the attachment cap must be in range. The scope stored is rebuilt from
/// Atlassian's answer, so an API-created source matches a UI-created one. Field names are the
/// scope keys the dialog writes: <c>spaceId</c>, <c>includeAttachments</c>, <c>maxAttachmentMb</c>.
/// </summary>
public static class ConfluenceSourceScope
{
    public enum Outcome { Valid, Invalid, Duplicate, Unavailable }

    /// <summary>The checked scope and default name, or what is wrong and with which field.</summary>
    public sealed record Result(Outcome Outcome, string? ScopeJson = null, string? DefaultName = null, string? Error = null, string? Field = null)
    {
        public static Result Fail(Outcome outcome, string field, string error) => new(outcome, Error: error, Field: field);
    }

    public static async Task<Result> ResolveAsync(
        string? scopeJson, Connection connection, AtlassianSiteService sites, ISourceStore sourceStore, CancellationToken ct)
    {
        if (Read(scopeJson) is not { } parsed)
            return Result.Fail(Outcome.Invalid, "scopeJson", "The scope must be a JSON object naming spaceId, as in {\"spaceId\":\"98306\"}.");
        if (parsed.Error is not null)
            return Result.Fail(Outcome.Invalid, parsed.Field!, parsed.Error);
        if (parsed.SpaceId is not { } spaceId)
            return Result.Fail(Outcome.Invalid, "spaceId",
                $"A Confluence source's scope needs \"spaceId\": the id of a space on the site. List them with GET /api/v1/atlassian/sites/{connection.Id}/spaces.");

        var selection = new ConfluenceSpaceSelection
        {
            IncludeAttachments = parsed.IncludeAttachments ?? true,
            MaxAttachmentMb = parsed.MaxAttachmentMb ?? ConfluenceSpaceSelection.DefaultMaxAttachmentMb,
        };
        selection.Selected.Add(spaceId);
        if (selection.Validate() is not null)
            return Result.Fail(Outcome.Invalid, "maxAttachmentMb", selection.MaxAttachmentMbError!);

        if (AtlassianSite.FromConfigJson(connection.ConfigJson) is not { } site)
            return Result.Fail(Outcome.Invalid, "connectionId",
                $"Connection '{connection.Name}' is not a complete Atlassian site. Re-add it on the Connections page.");

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
        var requests = verified.ToRequests(connection.Id, new Uri(site.SiteUrl).Host, existing, out var skipped);
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
    public static Result Update(string currentScopeJson, string? newScopeJson)
    {
        if (Read(newScopeJson) is not { } parsed)
            return Result.Fail(Outcome.Invalid, "scopeJson", "The scope must be a JSON object.");
        if (parsed.Error is not null)
            return Result.Fail(Outcome.Invalid, parsed.Field!, parsed.Error);

        JsonObject current;
        try
        {
            current = JsonNode.Parse(currentScopeJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            current = [];
        }

        string? currentSpaceId = Read(currentScopeJson)?.SpaceId;
        if (parsed.SpaceId is { } spaceId && !string.Equals(spaceId, currentSpaceId, StringComparison.Ordinal))
            return Result.Fail(Outcome.Invalid, "spaceId",
                "A Confluence source's space cannot be changed. Add the space as a new source.");

        bool includeAttachments = parsed.IncludeAttachments
            ?? (current["includeAttachments"]?.GetValueKind() is JsonValueKind.False ? false : true);
        int maxAttachmentMb = parsed.MaxAttachmentMb
            ?? (current["maxAttachmentMb"] is JsonValue v && v.TryGetValue(out int stored) ? stored : ConfluenceSpaceSelection.DefaultMaxAttachmentMb);

        var selection = new ConfluenceSpaceSelection { IncludeAttachments = includeAttachments, MaxAttachmentMb = maxAttachmentMb };
        if (!selection.ValidateMaxAttachmentMb())
            return Result.Fail(Outcome.Invalid, "maxAttachmentMb", selection.MaxAttachmentMbError!);

        current["includeAttachments"] = includeAttachments;
        current["maxAttachmentMb"] = maxAttachmentMb;
        return new Result(Outcome.Valid, current.ToJsonString());
    }

    private sealed record Parsed(string? SpaceId, bool? IncludeAttachments, int? MaxAttachmentMb, string? Error = null, string? Field = null);

    /// <summary>The scope's fields, or null when it is not a JSON object. A field of the wrong type is reported, not ignored.</summary>
    private static Parsed? Read(string? scopeJson)
    {
        JsonObject? scope;
        try
        {
            scope = JsonNode.Parse(string.IsNullOrWhiteSpace(scopeJson) ? "{}" : scopeJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (scope is null)
            return null;

        string? spaceId = null;
        if (scope["spaceId"] is { } id)
        {
            spaceId = id.GetValueKind() switch
            {
                JsonValueKind.String => id.GetValue<string>().Trim(),
                JsonValueKind.Number => id.ToJsonString(),
                _ => null,
            };
            // The connector's own rule for a content id: digits only, which is all it will sync.
            if (spaceId is not { Length: > 0 and <= 19 } || !spaceId.All(char.IsAsciiDigit))
                return new Parsed(null, null, null, "\"spaceId\" must be a Confluence space id, a string of digits such as \"98306\".", "spaceId");
        }

        bool? includeAttachments = null;
        if (scope["includeAttachments"] is { } include)
        {
            if (include.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                return new Parsed(null, null, null, "\"includeAttachments\" must be true or false.", "includeAttachments");
            includeAttachments = include.GetValue<bool>();
        }

        int? maxAttachmentMb = null;
        if (scope["maxAttachmentMb"] is { } max)
        {
            if (max is not JsonValue value || !value.TryGetValue(out int mb))
                return new Parsed(null, null, null,
                    $"\"maxAttachmentMb\" must be a whole number from {ConfluenceSpaceSelection.MinAttachmentMb} to {ConfluenceSpaceSelection.MaxAttachmentMbLimit}.",
                    "maxAttachmentMb");
            maxAttachmentMb = mb;
        }

        return new Parsed(spaceId, includeAttachments, maxAttachmentMb);
    }
}
