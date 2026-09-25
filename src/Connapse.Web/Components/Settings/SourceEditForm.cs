using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Storage.Connectors;

namespace Connapse.Web.Components.Settings;

/// <summary>
/// Editing an existing source: what every source has (name, description, sync interval) and the
/// settings a provider lets change after creation. Where a source reads from — its bucket,
/// directory, or repository — is not editable: pointing a source somewhere else is a new source.
/// <para>
/// A class rather than page logic, like <see cref="SourceForm"/>: the scope keys written here must
/// match what <c>ConnectorFactory</c> reads, and the page has no test harness.
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
        };
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "A source name is required.";

        if (SyncIntervalSeconds is { } interval && interval < 60)
            return "Sync interval must be at least 60 seconds.";

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
