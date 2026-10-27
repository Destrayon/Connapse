using System.Net;
using System.Text.RegularExpressions;
using Connapse.Core;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>
/// The links a Confluence page or blog post asserts in the link layer (CON-34). <see cref="Metadata"/> reads them
/// from the storage-format body and labels the connector fetches to render the page, so they cost no further request;
/// <see cref="For"/> turns that metadata into rows.
/// <list type="bullet">
/// <item><c>parent</c> to the page it sits under in the page tree.</item>
/// <item><c>jira-issue</c> to each Jira issue it names by a Jira macro or a <c>/browse/</c> URL.</item>
/// <item><c>links-to</c> to each page it links, and <c>labelled</c> to a node per label. Both are weaker evidence that
/// two pages answer the same question, so the walk gives them less weight (<see cref="LightTypes"/>).</item>
/// </list>
/// A page is also its title's node, as storage format and old <c>/display/</c> URLs name pages by space and title.
/// </summary>
public static partial class ConfluenceLinks
{
    public const string Parent = "parent";
    public const string JiraIssue = "jira-issue";
    public const string LinksTo = "links-to";
    public const string Labelled = "labelled";

    /// <summary>The link types the walk weighs lighter than a stated relation.</summary>
    public static IReadOnlyCollection<string> LightTypes { get; } = [LinksTo, Labelled];

    private const string Prefix = "confluence:";
    private const string SiteKey = Prefix + "site";
    private const string PageIdKey = Prefix + "pageId";
    private const string SpaceKeyKey = Prefix + "spaceKey";
    private const string TitleKey = Prefix + "title";
    private const string ParentKey = Prefix + "parent";
    private const string LabelsKey = Prefix + "labels";
    private const string PagesKey = Prefix + "pages";
    private const string IssuesKey = Prefix + "issues";

    /// <summary>Whether a document's metadata could carry links, so a document that never did skips the link tables.</summary>
    public static bool MayHaveLinks(IReadOnlyDictionary<string, string> metadata) => metadata.ContainsKey(SiteKey);

    /// <summary>
    /// The link metadata of a page. Every key is present, empty when the page has none of that kind, so a reindex
    /// does not keep links the page has dropped. <paramref name="parentPageId"/> is null when the page sits at the
    /// space's root or under a folder.
    /// </summary>
    public static Dictionary<string, string> Metadata(
        string site, string spaceKey, string pageId, string title, string? parentPageId,
        IEnumerable<string> labels, string storageBody)
    {
        List<string> pages = [];
        foreach (Match m in PageMacro().Matches(storageBody))
        {
            string attributes = m.Groups["attrs"].Value;
            if (Attribute(attributes, "ri:content-title") is not { Length: > 0 } linked)
                continue;
            string space = Attribute(attributes, "ri:space-key") is { Length: > 0 } other ? other : spaceKey;
            pages.Add($"title:{space.ToUpperInvariant()}/{linked.Trim()}");
        }
        pages.AddRange(AtlassianLinks.PageRefs(storageBody));

        IEnumerable<string> issues = JiraMacroKey().Matches(storageBody)
            .Select(m => m.Groups["key"].Value.ToUpperInvariant())
            .Concat(AtlassianLinks.IssueUrlKeys(storageBody));

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SiteKey] = site,
            [PageIdKey] = pageId,
            [SpaceKeyKey] = spaceKey,
            [TitleKey] = title,
            [ParentKey] = parentPageId ?? "",
            [LabelsKey] = AtlassianLinks.Join(labels.Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0)),
            [PagesKey] = AtlassianLinks.Join(pages),
            [IssuesKey] = AtlassianLinks.Join(issues),
        };
    }

    /// <summary>The page's links, or null when its metadata is not a Confluence page's.</summary>
    public static DocumentLinks? For(string documentId, IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.GetValueOrDefault(SiteKey) is not { Length: > 0 } site
            || metadata.GetValueOrDefault(PageIdKey) is not { Length: > 0 } pageId)
            return null;

        string self = AtlassianLinks.PageNode(site, pageId);
        string title = metadata.GetValueOrDefault(TitleKey) ?? "";
        string? titleNode = metadata.GetValueOrDefault(SpaceKeyKey) is { Length: > 0 } space && title.Trim().Length > 0
            ? AtlassianLinks.TitleNode(site, space, title)
            : null;

        List<LinkFact> facts = [new(self, LinkFactField.Kind, "page")];
        if (title.Length > 0)
            facts.Add(new(self, LinkFactField.Name, title));
        List<LinkAssertion> links = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        void Add(string type, string to)
        {
            string id = AtlassianLinks.EdgeId(type, self, to);
            if (to != self && to != titleNode && seen.Add(id))
                links.Add(new LinkAssertion(id, self, to, type));
        }

        if (metadata.GetValueOrDefault(ParentKey) is { Length: > 0 } parent)
            Add(Parent, AtlassianLinks.PageNode(site, parent));
        foreach (string issue in AtlassianLinks.Values(metadata.GetValueOrDefault(IssuesKey)))
        {
            if (AtlassianLinks.IsIssueKey(issue))
                Add(JiraIssue, AtlassianLinks.IssueNode(site, issue));
        }
        foreach (string page in AtlassianLinks.Values(metadata.GetValueOrDefault(PagesKey)))
        {
            if (AtlassianLinks.PageRefNode(site, page) is { } node)
                Add(LinksTo, node);
        }
        foreach (string label in AtlassianLinks.Values(metadata.GetValueOrDefault(LabelsKey)))
            Add(Labelled, LabelNode(site, label));

        return new DocumentLinks(documentId, self, facts, links, titleNode is null ? null : [titleNode]);
    }

    public static string LabelNode(string site, string label) => $"confluencelabel:{site}:{label.ToLowerInvariant()}";

    private static string? Attribute(string attributes, string name)
    {
        Match m = Regex.Match(attributes, Regex.Escape(name) + "=\"(?<v>[^\"]*)\"", RegexOptions.CultureInvariant);
        return m.Success ? WebUtility.HtmlDecode(m.Groups["v"].Value) : null;
    }

    [GeneratedRegex(@"<ri:page\b(?<attrs>[^>]*)>", RegexOptions.CultureInvariant)]
    private static partial Regex PageMacro();

    [GeneratedRegex(
        @"<ac:structured-macro\b[^>]*ac:name=""jira""[^>]*>(?:(?!</ac:structured-macro>).)*?<ac:parameter\b[^>]*ac:name=""key""[^>]*>\s*(?<key>[A-Za-z][A-Za-z0-9_]*-\d+)\s*</ac:parameter>",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex JiraMacroKey();
}
