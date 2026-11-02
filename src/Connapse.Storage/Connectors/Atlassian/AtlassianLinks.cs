using System.Text.RegularExpressions;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>
/// Node ids and reference parsing the Jira and Confluence links share (CON-34), so an issue that names a page and the
/// page itself meet at one node. Ids carry the site (the cloud id), as two sites can each have a FLINK-1 or a page 42.
/// </summary>
public static partial class AtlassianLinks
{
    /// <summary>Separates the values in one link metadata key; titles and link type names may hold commas.</summary>
    public const char Separator = '\n';

    public static string IssueNode(string site, string key) => $"jira:{site}:{key.ToUpperInvariant()}";

    public static string PageNode(string site, string pageId) => $"confluence:{site}:{pageId}";

    /// <summary>A page by its space and title, the only way storage format and old <c>/display/</c> URLs name it.</summary>
    public static string TitleNode(string site, string spaceKey, string title) =>
        $"confluence:{site}:{spaceKey.ToUpperInvariant()}/{title.Trim()}";

    /// <summary>The hosts a site's own links are on: its <c>https://{name}.atlassian.net</c> URL's, or none.</summary>
    public static IReadOnlyCollection<string> SiteHosts(string? siteUrl) =>
        Uri.TryCreate(siteUrl, UriKind.Absolute, out Uri? uri) ? [uri.Host] : [];

    /// <summary>Derived from the link itself, so every document asserting it writes the same id.</summary>
    public static string EdgeId(string type, string from, string to) => $"{type}|{from}|{to}";

    /// <summary>
    /// Confluence pages a text links to, as <c>id:{pageId}</c> or <c>title:{SPACE}/{title}</c>. Only relative URLs
    /// and URLs on <paramref name="hosts"/> are taken: another site can have a page with the same id or title, and a
    /// link to it must not become a link to this site's page.
    /// </summary>
    public static IEnumerable<string> PageRefs(string text, IReadOnlyCollection<string>? hosts)
    {
        foreach (Match m in SitePaths(text, hosts).SelectMany(p => PageUrl().Matches(p)))
        {
            if (m.Groups["id"].Success)
                yield return "id:" + m.Groups["id"].Value;
            else if (m.Groups["pid"].Success)
                yield return "id:" + m.Groups["pid"].Value;
            else
            {
                string title = Uri.UnescapeDataString(m.Groups["title"].Value.Replace('+', ' ')).Trim();
                if (title.Length > 0)
                    yield return $"title:{m.Groups["space"].Value.ToUpperInvariant()}/{title}";
            }
        }
    }

    /// <summary>The node a <see cref="PageRefs"/> value names, or null for a malformed one.</summary>
    public static string? PageRefNode(string site, string pageRef)
    {
        if (pageRef.StartsWith("id:", StringComparison.Ordinal) && pageRef.Length > 3)
            return PageNode(site, pageRef[3..]);
        if (pageRef.StartsWith("title:", StringComparison.Ordinal) && pageRef.IndexOf('/') is int slash and > 6
            && slash < pageRef.Length - 1)
            return TitleNode(site, pageRef[6..slash], pageRef[(slash + 1)..]);
        return null;
    }

    /// <summary>Jira issue keys a text links to by <c>/browse/{KEY}</c> URL, relative or on <paramref name="hosts"/>.</summary>
    public static IEnumerable<string> IssueUrlKeys(string text, IReadOnlyCollection<string>? hosts) =>
        SitePaths(text, hosts).SelectMany(p => BrowseUrl().Matches(p)).Select(m => m.Groups["key"].Value.ToUpperInvariant());

    /// <summary>The path and query of each URL in a text that is relative or whose host is one of the site's.</summary>
    private static IEnumerable<string> SitePaths(string text, IReadOnlyCollection<string>? hosts)
    {
        foreach (Match m in Url().Matches(text))
        {
            string url = m.Value;
            if (url.StartsWith('/'))
            {
                yield return url;
                continue;
            }
            int authority = url.IndexOf("://", StringComparison.Ordinal) + 3;
            int path = url.IndexOf('/', authority);
            if (path > 0 && hosts is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                yield return url[path..];
        }
    }

    [GeneratedRegex(
        @"https?://[^\s""'<>\]\[|\\]+|(?<![\w.:/\\-])/(?:wiki|confluence|jira|browse|display|spaces|pages)/[^\s""'<>\]\[|\\]*",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    public static bool IsIssueKey(string? key) => key is not null && IssueKey().IsMatch(key);

    public static IEnumerable<string> Values(string? joined) =>
        (joined ?? "").Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Join(IEnumerable<string> values) => string.Join(Separator, values.Distinct(StringComparer.Ordinal));

    [GeneratedRegex(
        @"/spaces/[A-Za-z0-9~_-]+/pages/(?<id>\d+)|[?&]pageId=(?<pid>\d+)|/display/(?<space>[A-Za-z0-9~_-]+)/(?<title>[^?#\s""'<>\]\[|]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PageUrl();

    [GeneratedRegex(@"/browse/(?<key>[A-Za-z][A-Za-z0-9_]*-\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex BrowseUrl();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]*-\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex IssueKey();
}
