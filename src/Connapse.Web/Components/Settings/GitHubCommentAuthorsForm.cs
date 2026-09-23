using System.Text.Json.Nodes;
using Connapse.Storage.Connectors.GitHub;

namespace Connapse.Web.Components.Settings;

/// <summary>
/// Whose comments a GitHub issues source indexes, as a checklist of the authors it has synced.
/// Bots start unticked and people ticked; only the exceptions are saved, as the scope's
/// <c>includeCommentAuthors</c> (bots kept) and <c>excludeCommentAuthors</c> (people dropped) —
/// the keys <c>ConnectorFactory</c> reads.
/// </summary>
public sealed class GitHubCommentAuthorsForm
{
    public const string IncludeKey = "includeCommentAuthors";
    public const string ExcludeKey = "excludeCommentAuthors";

    public sealed class Item(GitHubCommentAuthor author, bool included)
    {
        public GitHubCommentAuthor Author { get; } = author;
        public bool Included { get; set; } = included;
    }

    public IReadOnlyList<Item> Items { get; }

    private GitHubCommentAuthorsForm(IReadOnlyList<Item> items) => Items = items;

    /// <summary>The checklist as the source's scope has it now.</summary>
    public static GitHubCommentAuthorsForm From(string? scopeJson, IReadOnlyList<GitHubCommentAuthor> authors)
    {
        var scope = Parse(scopeJson);
        var policy = new GitHubCommentPolicy(Strings(scope, IncludeKey), Strings(scope, ExcludeKey));
        return new GitHubCommentAuthorsForm(
            authors.Select(a => new Item(a, policy.Allows(a.Login, a.IsBot))).ToList());
    }

    /// <summary>
    /// The scope with the checklist applied. Other keys are kept, and an author named in the scope
    /// but no longer seen keeps their entry, so a quiet bot does not come back on its own.
    /// </summary>
    public string ApplyTo(string? scopeJson)
    {
        var scope = Parse(scopeJson);
        var seen = new HashSet<string>(Items.Select(i => i.Author.Login), StringComparer.OrdinalIgnoreCase);

        var include = Strings(scope, IncludeKey).Where(l => !seen.Contains(l))
            .Concat(Items.Where(i => i.Author.IsBot && i.Included).Select(i => i.Author.Login));
        var exclude = Strings(scope, ExcludeKey).Where(l => !seen.Contains(l))
            .Concat(Items.Where(i => !i.Author.IsBot && !i.Included).Select(i => i.Author.Login));

        Set(scope, IncludeKey, include);
        Set(scope, ExcludeKey, exclude);
        return scope.ToJsonString();
    }

    private static JsonObject Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonNode.Parse(json) as JsonObject ?? [];

    private static List<string> Strings(JsonObject scope, string key) =>
        scope[key] is JsonArray array
            ? array.Select(n => n?.GetValue<string>()).OfType<string>().ToList()
            : [];

    private static void Set(JsonObject scope, string key, IEnumerable<string> logins)
    {
        var list = logins.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
            scope.Remove(key);
        else
            scope[key] = new JsonArray([.. list.Select(l => (JsonNode)l)]);
    }
}
