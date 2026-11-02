using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Components.Sources;
using Connapse.Web.Services;

namespace Connapse.Web.Endpoints;

/// <summary>
/// What the API returns for a source.
/// <para>
/// Deliberately not the <see cref="Source"/> record. That carries <c>ScopeJson</c>, which
/// names buckets, prefixes and filesystem subpaths, and <c>SyncCursor</c>, an opaque
/// provider continuation token — infrastructure detail a reader has no reason to receive,
/// and the kind of thing that turns a read route into reconnaissance. Serializing the record
/// directly would hand both out.
/// </para>
/// <para>
/// The one exception is a Confluence source's settings, which administrators edit on the Edit
/// source page and PATCH: they come back to administrators as <see cref="Confluence"/>, under
/// the keys <c>scopeJson</c> takes, and a Jira source's, as <see cref="Jira"/>. Other providers'
/// scopes are never returned.
/// </para>
/// </summary>
public record SourceResponse(
    Guid Id,
    string Name,
    string? Description,
    Guid? ConnectionId,
    bool Enabled,
    SyncStatus LastSyncStatus,
    DateTime? LastSyncedAt,
    int? SyncIntervalSeconds,
    int DocumentCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    /// <summary>
    /// How many deletions the last reconcile declined to apply, or null when none are pending.
    /// Unlike <see cref="LastSyncError"/> this is not administrator-only: it is a count, and
    /// names nothing about the remote's contents or structure.
    /// </summary>
    int? WithheldDeletions,
    /// <summary>
    /// Populated for administrators only. A provider's failure text routinely echoes the
    /// thing that failed — "Access Denied for bucket payroll-data" — so returning it to
    /// every reader would give back exactly the infrastructure detail ScopeJson is withheld
    /// to protect.
    /// </summary>
    string? LastSyncError,
    /// <summary>Documents whose ingestion failed. A count, like <see cref="WithheldDeletions"/>.</summary>
    int FailedDocumentCount = 0,
    /// <summary>Documents waiting for or undergoing ingestion.</summary>
    int ProcessingDocumentCount = 0,
    /// <summary>
    /// Always "source". Present so a client consuming both this and the container routes can
    /// tell them apart on a single field, matching the MCP contract.
    /// </summary>
    string Kind = "source",
    /// <summary>
    /// A Confluence source's space and attachment settings, for administrators only; null for
    /// every other source. The connector's defaults fill any key an older scope left out.
    /// </summary>
    ConfluenceSourceSettings? Confluence = null,
    /// <summary>
    /// Administrators only, beside <see cref="LastSyncError"/>: the setup guide's entry for it, as
    /// the Sources page links it. Null when there is no error or no entry for the provider.
    /// </summary>
    string? LastSyncTroubleshootingUrl = null,
    /// <summary>
    /// A Jira source's project and attachment settings, for administrators only, as
    /// <see cref="Confluence"/> is for a space; null for every other source.
    /// </summary>
    JiraSourceSettings? Jira = null,
    /// <summary>
    /// Only on a create's answer, and only when there is something to say: what the connection's
    /// scope check warns about (a connection that declares no allowed locations). Left out otherwise.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Warning = null)
{
    public static SourceResponse From(Source source, bool includeDiagnostics) => new(
        Id: source.Id,
        Name: source.Name,
        Description: source.Description,
        ConnectionId: source.ConnectionId,
        Enabled: source.Enabled,
        LastSyncStatus: source.LastSyncStatus,
        LastSyncedAt: source.LastSyncedAt,
        SyncIntervalSeconds: source.SyncIntervalSeconds,
        DocumentCount: source.DocumentCount,
        CreatedAt: source.CreatedAt,
        UpdatedAt: source.UpdatedAt,
        WithheldDeletions: source.WithheldDeletions,
        LastSyncError: includeDiagnostics ? source.LastSyncError : null,
        FailedDocumentCount: source.FailedDocumentCount,
        ProcessingDocumentCount: source.ProcessingDocumentCount,
        Confluence: includeDiagnostics ? ConfluenceSourceSettings.From(ScopeOf(source.ScopeJson)) : null,
        LastSyncTroubleshootingUrl: includeDiagnostics ? TroubleshootingUrlOf(source) : null,
        Jira: includeDiagnostics ? JiraSourceSettings.From(ScopeOf(source.ScopeJson)) : null);

    private static JsonObject? ScopeOf(string? scopeJson)
    {
        if (string.IsNullOrWhiteSpace(scopeJson))
            return null;
        try
        {
            return JsonNode.Parse(scopeJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TroubleshootingUrlOf(Source source)
    {
        if (string.IsNullOrWhiteSpace(source.LastSyncError) || ScopeOf(source.ScopeJson) is not { } scope)
            return null;
        if (ConfluenceSourceSettings.IsConfluence(scope) || JiraSourceSettings.IsJira(scope))
            return AtlassianSyncErrors.TroubleshootingUrl(source.LastSyncError);
        if (scope["owner"] is not null && scope["repo"] is not null)
            return GitHubMessages.TroubleshootingUrl;
        return null;
    }
}

/// <summary>
/// A Confluence source's settings as the API returns them, under the keys <c>scopeJson</c> takes
/// on create and PATCH. <c>spaceKey</c> and <c>spaceName</c> are what the space was called when the
/// source was added; the space itself is fixed.
/// </summary>
public sealed record ConfluenceSourceSettings(
    string? SpaceId, string? SpaceKey, string? SpaceName, bool IncludeAttachments, int MaxAttachmentMb)
{
    public const string Kind = "confluence-space";

    internal static bool IsConfluence(JsonObject scope) =>
        SourceScopeCodec.AtlassianKindOf(scope) == AtlassianScopeKind.ConfluenceSpace;

    /// <summary>The settings, or null when <paramref name="scope"/> is not a Confluence space's.</summary>
    internal static ConfluenceSourceSettings? From(JsonObject? scope)
    {
        if (scope is null || !IsConfluence(scope))
            return null;

        var (include, maxMb) = AtlassianScopeFields.Attachments(scope);
        return new ConfluenceSourceSettings(
            AtlassianScopeFields.Str(scope, "spaceId"), AtlassianScopeFields.Str(scope, "spaceKey"),
            AtlassianScopeFields.Str(scope, "spaceName"), include, maxMb);
    }
}

/// <summary>Reading an Atlassian scope's fields as the API returns them, for either product.</summary>
internal static class AtlassianScopeFields
{
    /// <summary>An Atlassian scope's attachment settings.</summary>
    internal static (bool Include, int MaxMb) Attachments(JsonObject scope)
    {
        // The same defaults the connector applies when a key is missing, so the API reads what will sync.
        bool include = scope["includeAttachments"]?.GetValueKind() != JsonValueKind.False;
        int maxMb = scope["maxAttachmentMb"] is JsonValue v && v.TryGetValue(out int mb)
            ? mb
            : ConfluenceSpaceSelection.DefaultMaxAttachmentMb;
        return (include, maxMb);
    }

    internal static string? Str(JsonObject scope, string name) => scope[name] switch
    {
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        _ => null,
    };
}

/// <summary>
/// A Jira source's settings as the API returns them, under the keys <c>scopeJson</c> takes on
/// create and PATCH. <c>projectKey</c> and <c>projectName</c> are what the project was called when
/// the source was added; the project itself is fixed.
/// </summary>
public sealed record JiraSourceSettings(
    string? ProjectId, string? ProjectKey, string? ProjectName, bool IncludeAttachments, int MaxAttachmentMb)
{
    internal static bool IsJira(JsonObject scope) =>
        SourceScopeCodec.AtlassianKindOf(scope) == AtlassianScopeKind.JiraProject;

    /// <summary>The settings, or null when <paramref name="scope"/> is not a Jira project's.</summary>
    internal static JiraSourceSettings? From(JsonObject? scope)
    {
        if (scope is null || !IsJira(scope))
            return null;

        var (include, maxMb) = AtlassianScopeFields.Attachments(scope);
        return new JiraSourceSettings(
            AtlassianScopeFields.Str(scope, "projectId"), AtlassianScopeFields.Str(scope, "projectKey"),
            AtlassianScopeFields.Str(scope, "projectName"), include, maxMb);
    }
}
