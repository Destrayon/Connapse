using System.Text.RegularExpressions;

namespace Connapse.Storage.CloudScope;

public enum JiraResourceKind { Issue, Comment, Attachment }

public enum JiraRestrictionType { Group, Role }

/// <summary>A group id, or a role named within one project.</summary>
public sealed record JiraRestriction(JiraRestrictionType Type, string Value, string? ProjectId = null)
{
    /// <summary>
    /// A restriction Connapse could not interpret: the all-zero group, which no user is in, so the
    /// document it governs is visible to nobody.
    /// </summary>
    public static readonly JiraRestriction DenyAll = new(JiraRestrictionType.Group, Guid.Empty.ToString("D"));
}

/// <summary>
/// What a Jira document's URI says governs it. <paramref name="CommentId"/> is the restricted
/// comment an attachment is tied to, whose live visibility search re-checks; null otherwise.
/// </summary>
public sealed record JiraResource(
    string CloudId, string IssueId, JiraResourceKind Kind, string? ChildId, bool Internal, JiraRestriction? Restriction,
    string? CommentId = null);

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

    // atlassian://{cloud}/jira/issue/{id}[/comment|attachment/{id}][/comment/{tying id}][/internal][/group/{guid}|/role/{projectId}/{escaped name}].
    // The tying comment follows an attachment only.
    // Role names are Uri.EscapeDataString output, whose characters this class covers.
    [GeneratedRegex(
        @"^atlassian://(?<cloud>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/jira/issue/(?<issue>[0-9]{1,19})"
        + @"(?:/(?<kind>comment|attachment)/(?<child>[0-9]{1,19}))?"
        + @"(?:/comment/(?<tie>[0-9]{1,19}))?"
        + @"(?<internal>/internal)?"
        + @"(?:/group/(?<group>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})"
        + @"|/role/(?<project>[0-9]{1,19})/(?<role>[A-Za-z0-9%._~-]{1,512}))?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex JiraPattern();

    public static string ForPage(string cloudId, string contentId) =>
        $"{SitePrefix(cloudId)}confluence/page/{contentId}";

    public static string ForAttachment(string cloudId, string contentId, string attachmentId) =>
        $"{ForPage(cloudId, contentId)}/attachment/{attachmentId}";

    public static string ForJiraIssue(string cloudId, string issueId) =>
        $"{SitePrefix(cloudId)}jira/issue/{issueId}";

    public static string ForJiraComment(
        string cloudId, string issueId, string commentId, bool internalNote, JiraRestriction? restriction) =>
        ForJiraChild(cloudId, issueId, "comment", commentId, internalNote, restriction);

    /// <summary>
    /// An attachment. One tied to a restricted comment names it in <paramref name="commentId"/> and
    /// carries its internal flag and restriction; an untied one carries neither, or deny-all.
    /// </summary>
    public static string ForJiraAttachment(
        string cloudId, string issueId, string attachmentId, bool internalNote, JiraRestriction? restriction,
        string? commentId = null) =>
        ForJiraChild(cloudId, issueId, "attachment", attachmentId, internalNote, restriction, commentId);

    private static string ForJiraChild(
        string cloudId, string issueId, string kind, string childId, bool internalNote, JiraRestriction? restriction,
        string? commentId = null)
    {
        string uri = $"{ForJiraIssue(cloudId, issueId)}/{kind}/{childId}";
        if (commentId is not null)
            uri += $"/comment/{commentId}";
        if (internalNote)
            uri += "/internal";
        return restriction switch
        {
            null => uri,
            { Type: JiraRestrictionType.Group } => $"{uri}/group/{Normalize(restriction.Value)}",
            _ => $"{uri}/role/{restriction.ProjectId}/{Uri.EscapeDataString(restriction.Value)}",
        };
    }

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

    /// <summary>
    /// Reads a Jira document's address. Rejects shapes no document has: an issue carrying a marker or
    /// restriction, a comment with neither (only restricted comments are separate documents), a tying
    /// comment after anything but an attachment, a tied attachment with neither marker nor
    /// restriction, an untied one with any but deny-all, and a role name that is blank once
    /// unescaped or not escaped in canonical form.
    /// </summary>
    public static bool TryParseJira(string? uri, out JiraResource resource)
    {
        resource = null!;
        if (uri is null)
            return false;

        Match match = JiraPattern().Match(uri);
        if (!match.Success)
            return false;

        bool isChild = match.Groups["kind"].Success;
        bool internalNote = match.Groups["internal"].Success;
        JiraRestriction? restriction = null;
        if (match.Groups["group"].Success)
        {
            restriction = new JiraRestriction(JiraRestrictionType.Group, Normalize(match.Groups["group"].Value));
        }
        else if (match.Groups["role"].Success)
        {
            // Only the exact form ForJiraChild writes: a stray or malformed escape, a lowercase
            // one, or an escaped character that needs none would let two URIs name one role.
            string segment = match.Groups["role"].Value;
            string name = Uri.UnescapeDataString(segment);
            if (string.IsNullOrWhiteSpace(name) || !string.Equals(Uri.EscapeDataString(name), segment, StringComparison.Ordinal))
                return false;
            restriction = new JiraRestriction(JiraRestrictionType.Role, name, match.Groups["project"].Value);
        }

        bool tied = match.Groups["tie"].Success;
        if (!isChild)
        {
            if (internalNote || restriction is not null || tied)
                return false;
            resource = new JiraResource(
                Normalize(match.Groups["cloud"].Value), match.Groups["issue"].Value, JiraResourceKind.Issue, null, false, null);
            return true;
        }

        JiraResourceKind kind = match.Groups["kind"].Value == "comment" ? JiraResourceKind.Comment : JiraResourceKind.Attachment;
        if (kind == JiraResourceKind.Comment && (tied || (!internalNote && restriction is null)))
            return false;
        // An untied attachment is governed by its issue alone, so a restriction there could only be stale.
        bool untiedShape = restriction == JiraRestriction.DenyAll || (restriction is null && !internalNote);
        if (kind == JiraResourceKind.Attachment && (tied ? !internalNote && restriction is null : !untiedShape))
            return false;

        resource = new JiraResource(
            Normalize(match.Groups["cloud"].Value), match.Groups["issue"].Value, kind,
            match.Groups["child"].Value, internalNote, restriction, tied ? match.Groups["tie"].Value : null);
        return true;
    }

    private static string Normalize(string cloudId) =>
        Guid.TryParse(cloudId, out Guid id) ? id.ToString("D") : cloudId;
}
