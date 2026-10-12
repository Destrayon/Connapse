using System.Text.RegularExpressions;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// The address every Confluence document carries: <c>atlassian://{cloudId}/confluence/page/{contentId}</c>
/// for a page or blog post, with <c>/attachment/{attachmentId}</c> appended for an attachment. The
/// cloud id names the site, so a check can find the connection that reads it.
/// </summary>
public static partial class AtlassianUri
{
    public const string Scheme = "atlassian://";

    // Exactly the two shapes above: a GUID cloud id and digit-only content ids, nothing after them.
    [GeneratedRegex(
        @"^atlassian://(?<cloud>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/confluence/page/(?<content>[0-9]{1,19})(/attachment/[0-9]{1,19})?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string ForPage(string cloudId, string contentId) =>
        $"{SitePrefix(cloudId)}confluence/page/{contentId}";

    public static string ForAttachment(string cloudId, string contentId, string attachmentId) =>
        $"{ForPage(cloudId, contentId)}/attachment/{attachmentId}";

    /// <summary>The prefix every document on a site starts with, <c>atlassian://{cloudId}/</c>.</summary>
    public static string SitePrefix(string cloudId) => $"{Scheme}{Normalize(cloudId)}/";

    public static bool IsAtlassian(string? uri) =>
        uri is not null && uri.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>
    /// Reads the site and the content whose permission governs the document. For an attachment that
    /// is its page, so <paramref name="contentId"/> is the page id either way. The cloud id comes back
    /// in lowercase GUID form.
    /// </summary>
    public static bool TryParse(string? uri, out string cloudId, out string contentId)
    {
        cloudId = "";
        contentId = "";
        if (uri is null)
            return false;

        Match match = Pattern().Match(uri);
        if (!match.Success)
            return false;

        cloudId = Normalize(match.Groups["cloud"].Value);
        contentId = match.Groups["content"].Value;
        return true;
    }

    private static string Normalize(string cloudId) =>
        Guid.TryParse(cloudId, out Guid id) ? id.ToString("D") : cloudId;
}
