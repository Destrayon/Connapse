using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Storage.Connectors;
using Connapse.Web.Components.Sources;
using Connapse.Web.Services;

namespace Connapse.Web.Components.Settings;

/// <summary>
/// Editing an existing source: what every source has (name, description, sync interval) and the
/// settings a provider lets change after creation. Where a source reads from — its bucket,
/// directory, or repository — is not editable: pointing a source somewhere else is a new source.
/// <para>
/// A class rather than page logic, like <see cref="SourceForm"/>: the scope keys written here must
/// match what <c>ConnectorFactory</c> reads.
/// </para>
/// </summary>
public sealed class SourceEditForm
{
    public const string IncludeAuthorsKey = "includeCommentAuthors";
    public const string ExcludeAuthorsKey = "excludeCommentAuthors";

    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int? SyncIntervalSeconds { get; set; }

    /// <summary>Which provider-specific section the page shows, if any.</summary>
    public GitHubContentKind? GitHubKind { get; private init; }

    public bool IncludeComments { get; set; } = true;

    /// <summary>Bots whose comments are indexed anyway.</summary>
    public List<string> IncludedBots { get; private init; } = [];

    /// <summary>People whose comments are left out — an automation account GitHub reports as a user.</summary>
    public List<string> ExcludedPeople { get; private init; } = [];

    /// <summary>Newline-separated file-name globs for a docs source. Blank means the markdown defaults.</summary>
    public string? DocPatterns { get; set; }

    /// <summary>
    /// A Confluence source's attachment settings, checked by the same rules as the New source
    /// dialog and the REST API; null for any other source. Its space is not editable.
    /// </summary>
    public ConfluenceSpaceSelection? Confluence { get; private init; }

    /// <summary>A Jira source's attachment settings, as <see cref="Confluence"/>; its project is not editable.</summary>
    public JiraProjectSelection? Jira { get; private init; }

    /// <summary>An Atlassian source's attachment settings, whichever product it reads; null for any other source.</summary>
    public AtlassianSourceSelection? Attachments => (AtlassianSourceSelection?)Confluence ?? Jira;

    public static SourceEditForm From(Source source, ConnectionProvider? provider)
    {
        var scope = Parse(source.ScopeJson);
        GitHubContentKind? kind = null;
        if ((provider ?? source.Provider) == ConnectionProvider.GitHub)
        {
            kind = Enum.TryParse(Str(scope, "kind"), ignoreCase: true, out GitHubContentKind k) && Enum.IsDefined(k)
                ? k
                : GitHubContentKind.Docs;
        }

        ConfluenceSpaceSelection? confluence = null;
        JiraProjectSelection? jira = null;
        if ((provider ?? source.Provider) == ConnectionProvider.Atlassian)
        {
            var (include, maxMb) = AttachmentsOf(scope);
            switch (SourceScopeCodec.AtlassianKindOf(source.ScopeJson))
            {
                case AtlassianScopeKind.JiraProject:
                    jira = new JiraProjectSelection { IncludeAttachments = include, MaxAttachmentMb = maxMb };
                    break;
                case AtlassianScopeKind.ConfluenceSpace:
                    confluence = new ConfluenceSpaceSelection { IncludeAttachments = include, MaxAttachmentMb = maxMb };
                    break;
            }
        }

        return new SourceEditForm
        {
            Name = source.Name,
            Description = source.Description,
            SyncIntervalSeconds = source.SyncIntervalSeconds,
            GitHubKind = kind,
            IncludeComments = scope["includeComments"] is JsonValue v && v.TryGetValue(out bool b) ? b : true,
            IncludedBots = Strings(scope, IncludeAuthorsKey),
            ExcludedPeople = Strings(scope, ExcludeAuthorsKey),
            DocPatterns = string.Join('\n', Strings(scope, "includePatterns")),
            Confluence = confluence,
            Jira = jira,
        };
    }

    /// <summary>A Confluence scope's attachment settings, with the connector's defaults for a missing key.</summary>
    private static (bool Include, int MaxMb) AttachmentsOf(JsonObject scope) =>
        (scope["includeAttachments"]?.GetValueKind() != System.Text.Json.JsonValueKind.False,
         scope["maxAttachmentMb"] is JsonValue v && v.TryGetValue(out int mb) ? mb : ConfluenceSpaceSelection.DefaultMaxAttachmentMb);

    public const string NameRequired = "A source name is required.";
    public const string IntervalTooShort = "Sync interval must be at least 60 seconds.";

    /// <summary>The Name field's error from the last check, shown beside the field.</summary>
    public string? NameError { get; private set; }

    /// <summary>The Sync interval field's error from the last check, shown beside the field.</summary>
    public string? SyncIntervalError { get; private set; }

    /// <summary>Checks the name, as on leaving the field; true when it is valid.</summary>
    public bool ValidateName()
    {
        NameError = string.IsNullOrWhiteSpace(Name) ? NameRequired : null;
        return NameError is null;
    }

    /// <summary>Checks the sync interval, as on leaving the field; true when it is valid.</summary>
    public bool ValidateSyncInterval()
    {
        SyncIntervalError = SyncIntervalSeconds is { } interval && interval < 60 ? IntervalTooShort : null;
        return SyncIntervalError is null;
    }

    /// <summary>
    /// Checks every field, setting each one's own error, and returns the first problem, or null
    /// when the form can be saved.
    /// </summary>
    public string? Validate()
    {
        bool nameOk = ValidateName();
        bool intervalOk = ValidateSyncInterval();
        bool sizeOk = Attachments is not { } attachments || attachments.ValidateMaxAttachmentMb();

        if (!nameOk) return NameError;
        if (!intervalOk) return SyncIntervalError;
        if (!sizeOk) return Attachments!.MaxAttachmentMbError;
        return null;
    }

    /// <summary>
    /// The update for <paramref name="source"/>, or null when nothing changed. The scope is only
    /// sent when it changed, because a changed scope makes the next sync re-read everything.
    /// </summary>
    public UpdateSourceRequest? ToRequest(Source source)
    {
        string scope = ApplyTo(source.ScopeJson);
        bool scopeChanged = !JsonNode.DeepEquals(Parse(scope), Parse(source.ScopeJson));
        string name = Name.Trim();
        string? description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();

        // A blank interval leaves the stored one alone: the store has no "back to the default".
        bool changed = scopeChanged || name != source.Name || description != source.Description
            || (SyncIntervalSeconds is not null && SyncIntervalSeconds != source.SyncIntervalSeconds);

        return changed
            ? new UpdateSourceRequest(name, description ?? "", scopeChanged ? scope : null, SyncIntervalSeconds)
            : null;
    }

    /// <summary>True when saving would change the scope, so the page can say a full re-read follows.</summary>
    public bool ChangesScope(Source source) =>
        !JsonNode.DeepEquals(Parse(ApplyTo(source.ScopeJson)), Parse(source.ScopeJson));

    private string ApplyTo(string? scopeJson)
    {
        var scope = Parse(scopeJson);
        if (GitHubKind == GitHubContentKind.IssuesAndPullRequests)
        {
            bool current = scope["includeComments"] is JsonValue v && v.TryGetValue(out bool b) ? b : true;
            if (IncludeComments != current)
                scope["includeComments"] = IncludeComments;
            Set(scope, IncludeAuthorsKey, IncludedBots);
            Set(scope, ExcludeAuthorsKey, ExcludedPeople);
        }
        else if (GitHubKind == GitHubContentKind.Docs)
        {
            Set(scope, "includePatterns", SourceForm.ParsePatterns(DocPatterns));
        }
        else if (Attachments is { } attachments)
        {
            // Only what changed is sent, through the rules the PATCH API applies, so an
            // untouched source keeps its scope byte for byte and is not re-read.
            var (include, maxMb) = AttachmentsOf(scope);
            var changes = new JsonObject();
            if (attachments.IncludeAttachments != include)
                changes["includeAttachments"] = attachments.IncludeAttachments;
            if (attachments.IncludeAttachments && attachments.MaxAttachmentMb != maxMb)
                changes["maxAttachmentMb"] = attachments.MaxAttachmentMb;

            if (changes.Count > 0
                && (Jira is not null
                    ? JiraSourceScope.Update(scope.ToJsonString(), changes.ToJsonString())
                    : ConfluenceSourceScope.Update(scope.ToJsonString(), changes.ToJsonString()))
                    is { Outcome: AtlassianSourceScope.Outcome.Valid, ScopeJson: { } updated })
                return updated;
        }

        return scope.ToJsonString();
    }

    private static JsonObject Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonNode.Parse(json) as JsonObject ?? [];

    private static string? Str(JsonObject scope, string key) =>
        scope[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static List<string> Strings(JsonObject scope, string key) =>
        scope[key] is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : null).OfType<string>().ToList()
            : [];

    private static void Set(JsonObject scope, string key, IEnumerable<string> values)
    {
        var list = values.Select(v => v.Trim()).Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Untouched when the list is what the scope already says, so an edit elsewhere on the
        // page does not rewrite it — any scope change makes the next sync re-read everything.
        if (list.SequenceEqual(Strings(scope, key), StringComparer.OrdinalIgnoreCase))
            return;

        if (list.Count == 0)
            scope.Remove(key);
        else
            scope[key] = new JsonArray([.. list.Select(v => (JsonNode)v)]);
    }
}
