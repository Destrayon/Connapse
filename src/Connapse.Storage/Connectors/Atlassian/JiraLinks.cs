using System.Text.Json;
using Connapse.Core;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>
/// The links a Jira issue asserts in the link layer (CON-34). <see cref="Metadata"/> reads them from the issue the
/// connector fetches to render it, so they cost no further request; <see cref="For"/> turns that metadata into rows.
/// <list type="bullet">
/// <item>Every issue link, typed by its link type's name (<c>blocks</c>, <c>relates</c>, <c>duplicate</c>…) and
/// pointing the way the type's outward phrase reads, so both ends assert one edge.</item>
/// <item><c>epic</c> to the issue's epic, <c>parent</c> from a sub-task to its parent.</item>
/// <item><c>fix-version</c> to a node per project version.</item>
/// <item><c>confluence-page</c> to each Confluence page the description links to.</item>
/// </list>
/// Sub-task and child lists are left out: the child asserts the link, and the parent's copy would only repeat it.
/// </summary>
public static class JiraLinks
{
    public const string Epic = "epic";
    public const string Parent = "parent";
    public const string FixVersion = "fix-version";
    public const string ConfluencePage = "confluence-page";

    private const string Prefix = "jira:";
    private const string SiteKey = Prefix + "site";
    private const string IssueKeyKey = Prefix + "issueKey";
    private const string LinksKey = Prefix + "links";
    private const string ParentKey = Prefix + "parent";
    private const string ParentKindKey = Prefix + "parentKind";
    private const string FixVersionsKey = Prefix + "fixVersions";
    private const string PagesKey = Prefix + "pages";

    /// <summary>Whether a document's metadata could carry links, so a document that never did skips the link tables.</summary>
    public static bool MayHaveLinks(IReadOnlyDictionary<string, string> metadata) => metadata.ContainsKey(SiteKey);

    /// <summary>
    /// The link metadata of an issue as Jira's REST API returns it (v2 or v3). Every key is present, empty when the
    /// issue has none of that kind, so a reindex does not keep links the issue has dropped.
    /// <paramref name="epicLinkField"/> names Data Center's "Epic Link" field; Cloud gives an issue's epic as its parent.
    /// <paramref name="hosts"/> are the site's hosts; a page URL on any other host is not taken as a link.
    /// </summary>
    public static Dictionary<string, string> Metadata(
        string site, JsonElement issue, string? epicLinkField = null, IReadOnlyCollection<string>? hosts = null)
    {
        JsonElement fields = Prop(issue, "fields") ?? default;
        List<string> links = [];
        if (Prop(fields, "issuelinks") is { ValueKind: JsonValueKind.Array } issueLinks)
        {
            foreach (JsonElement link in issueLinks.EnumerateArray())
            {
                string? type = Str(Prop(link, "type") ?? default, "name")?.Trim().ToLowerInvariant();
                (string direction, JsonElement? other) = Prop(link, "outwardIssue") is { ValueKind: JsonValueKind.Object } outward
                    ? ("out", outward)
                    : ("in", Prop(link, "inwardIssue"));
                if (!string.IsNullOrEmpty(type) && Str(other ?? default, "key") is { } key && AtlassianLinks.IsIssueKey(key))
                    links.Add($"{direction}|{key}|{type}");
            }
        }

        string parent = "", parentKind = "";
        if (Prop(fields, "parent") is { ValueKind: JsonValueKind.Object } parentIssue
            && Str(parentIssue, "key") is { } parentKey && AtlassianLinks.IsIssueKey(parentKey))
        {
            JsonElement parentType = Prop(Prop(parentIssue, "fields") ?? default, "issuetype") ?? default;
            bool subtask = Prop(Prop(fields, "issuetype") ?? default, "subtask") is { ValueKind: JsonValueKind.True };
            bool epic = !subtask && (Str(parentType, "name") == "Epic"
                || Prop(parentType, "hierarchyLevel") is { ValueKind: JsonValueKind.Number } level && level.GetInt32() >= 1);
            (parent, parentKind) = (parentKey, epic ? Epic : Parent);
        }
        else if (epicLinkField is not null && Str(fields, epicLinkField) is { } epicKey && AtlassianLinks.IsIssueKey(epicKey))
            (parent, parentKind) = (epicKey, Epic);

        IEnumerable<string> versions = Prop(fields, "fixVersions") is { ValueKind: JsonValueKind.Array } fixVersions
            ? fixVersions.EnumerateArray().Select(v => Str(v, "name")?.Trim()).OfType<string>().Where(n => n.Length > 0)
            : [];

        // The raw JSON is searched, not rendered text: Cloud's description is a document tree whose links sit in
        // attributes, Data Center's is wiki markup, and the URL is the same in both.
        IEnumerable<string> pages = Prop(fields, "description") is { ValueKind: not JsonValueKind.Null } description
            ? AtlassianLinks.PageRefs(description.GetRawText(), hosts)
            : [];

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SiteKey] = site,
            [IssueKeyKey] = Str(issue, "key") ?? "",
            [LinksKey] = AtlassianLinks.Join(links),
            [ParentKey] = parent,
            [ParentKindKey] = parentKind,
            [FixVersionsKey] = AtlassianLinks.Join(versions),
            [PagesKey] = AtlassianLinks.Join(pages),
        };
    }

    /// <summary>The issue's links, or null when its metadata is not a Jira issue's.</summary>
    public static DocumentLinks? For(string documentId, IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.GetValueOrDefault(SiteKey) is not { Length: > 0 } site
            || metadata.GetValueOrDefault(IssueKeyKey)?.Trim().ToUpperInvariant() is not { } key
            || !AtlassianLinks.IsIssueKey(key))
            return null;

        string self = AtlassianLinks.IssueNode(site, key);
        List<LinkFact> facts = [new(self, LinkFactField.Kind, "issue"), new(self, LinkFactField.Name, key)];
        List<LinkAssertion> links = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        void Add(string type, string from, string to)
        {
            string id = AtlassianLinks.EdgeId(type, from, to);
            if (from != to && seen.Add(id))
                links.Add(new LinkAssertion(id, from, to, type));
        }

        foreach (string link in AtlassianLinks.Values(metadata.GetValueOrDefault(LinksKey)))
        {
            string[] parts = link.Split('|', 3);
            if (parts.Length != 3 || !AtlassianLinks.IsIssueKey(parts[1]) || parts[2].Length == 0)
                continue;
            string other = AtlassianLinks.IssueNode(site, parts[1]);
            if (parts[0] == "out")
                Add(parts[2], self, other);
            else if (parts[0] == "in")
                Add(parts[2], other, self);
        }

        if (metadata.GetValueOrDefault(ParentKey) is { } parent && AtlassianLinks.IsIssueKey(parent))
            Add(metadata.GetValueOrDefault(ParentKindKey) == Epic ? Epic : Parent, self, AtlassianLinks.IssueNode(site, parent));

        string project = key[..key.LastIndexOf('-')];
        foreach (string version in AtlassianLinks.Values(metadata.GetValueOrDefault(FixVersionsKey)))
        {
            // The reader only walks to a node some readable document names, so the version hub needs its own facts.
            string node = VersionNode(site, project, version);
            facts.AddRange([new(node, LinkFactField.Kind, "version"), new(node, LinkFactField.Name, version)]);
            Add(FixVersion, self, node);
        }

        foreach (string page in AtlassianLinks.Values(metadata.GetValueOrDefault(PagesKey)))
        {
            if (AtlassianLinks.PageRefNode(site, page) is { } node)
                Add(ConfluencePage, self, node);
        }

        return new DocumentLinks(documentId, self, facts, links);
    }

    public static string VersionNode(string site, string project, string version) =>
        $"jiraversion:{site}:{project.ToUpperInvariant()}/{version}";

    private static JsonElement? Prop(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out JsonElement value) ? value : null;

    private static string? Str(JsonElement node, string name) =>
        Prop(node, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
