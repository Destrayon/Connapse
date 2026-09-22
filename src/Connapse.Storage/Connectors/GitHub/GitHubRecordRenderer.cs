using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Connapse.Storage.Connectors.GitHub;

internal sealed record GitHubRenderedRecord(
    string Path,
    string Markdown,
    IReadOnlyDictionary<string, string> Metadata,
    DateTime LastModified);

/// <summary>
/// Turns one issue or pull request, with its comments, into the markdown document that is indexed,
/// and the graph edges a later GraphRAG phase resolves across sources.
/// <para>
/// Edges here are only the ones that cost no request per record: the rest (timeline
/// cross-references, connected/disconnected events, files a pull request touched) need a call
/// each, which the anonymous budget cannot afford, and are left to a backfill.
/// </para>
/// </summary>
internal static partial class GitHubRecordRenderer
{
    public const string MetadataPrefix = "github:";

    /// <summary>
    /// The line that opens every comment. Kept to one shape — <c>--- … ---</c> on its own line —
    /// so the record chunker can split an oversized record on it.
    /// </summary>
    internal static string CommentDelimiter(GitHubStoredComment c) =>
        c.ReviewPath is null
            ? $"--- Comment by {c.Login} ({Day(c.CreatedAt)}) ---"
            : $"--- Review comment by {c.Login} on {c.ReviewPath} ({Day(c.CreatedAt)}) ---";

    public static string PathFor(GitHubIssue issue) => PathFor(issue.Number, issue.IsPullRequest);

    public static string PathFor(int number, bool isPullRequest) =>
        isPullRequest ? $"/pulls/{number}.md" : $"/issues/{number}.md";

    /// <summary>Parses <c>/issues/{n}.md</c> or <c>/pulls/{n}.md</c>; anything else is not a record path.</summary>
    public static bool TryParsePath(string path, out int number, out bool isPullRequest)
    {
        var match = RecordPathPattern().Match(path);
        isPullRequest = match.Success && match.Groups[1].Value == "pulls";
        number = 0;
        return match.Success
            && int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number > 0;
    }

    public static GitHubRenderedRecord Render(GitHubStoredRecord record, string owner, string repo)
    {
        var issue = record.Issue
            ?? throw new InvalidOperationException($"Record {record.Number} has not been swept yet.");

        var visibleComments = record.Comments
            .Where(kv => !kv.Value.Minimized)
            .OrderBy(kv => kv.Value.CreatedAt)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value)
            .ToList();

        IReadOnlyList<int> closes = issue.IsPullRequest ? ClosingReferences(issue.Body) : [];

        // Mentions already expressed as a stronger edge are not repeated as a plain reference.
        var known = new List<int> { issue.Number };
        known.AddRange(closes);
        known.AddRange(record.Children);
        if (record.Parent is { } parentNumber) known.Add(parentNumber);

        IReadOnlyList<int> references = Mentions([issue.Body, .. visibleComments.Select(c => c.Body)], known);

        string state = issue.PullRequest?.MergedAt is not null ? "merged" : issue.State;
        string[] labels = [.. (issue.Labels ?? []).Select(l => l.Name)];

        var md = new StringBuilder();
        md.Append("# ").Append(issue.Number).Append(": ").Line(issue.Title);

        var facts = new List<string>
        {
            "Type: " + (issue.IsPullRequest ? "Pull request" : "Issue"),
            "Author: " + (issue.User?.Login ?? "ghost"),
            "State: " + state,
        };
        if (labels.Length > 0) facts.Add("Labels: " + string.Join(", ", labels));
        if (issue.Milestone is not null) facts.Add("Milestone: " + issue.Milestone.Title);
        facts.Add("Created: " + Day(issue.CreatedAt));
        if (issue.PullRequest?.MergedAt is { } merged) facts.Add("Merged: " + Day(merged));
        else if (issue.ClosedAt is { } closed) facts.Add("Closed: " + Day(closed));
        md.Line(string.Join(" · ", facts));

        var edges = new List<string>();
        if (closes.Count > 0) edges.Add("Closes: " + Refs(closes));
        if (record.Parent is { } parent) edges.Add("Parent: #" + parent);
        if (record.Children.Count > 0) edges.Add("Sub-issues: " + Refs(record.Children));
        if (references.Count > 0) edges.Add("References: " + Refs(references));
        if (edges.Count > 0) md.Line(string.Join(" · ", edges));

        if (!string.IsNullOrWhiteSpace(issue.Body))
            md.Append('\n').Line(Normalize(issue.Body));

        foreach (var comment in visibleComments)
            md.Append('\n').Line(CommentDelimiter(comment)).Line(Normalize(comment.Body));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MetadataPrefix + "repo"] = $"{owner}/{repo}",
            [MetadataPrefix + "type"] = issue.IsPullRequest ? "pull_request" : "issue",
            [MetadataPrefix + "number"] = issue.Number.ToString(CultureInfo.InvariantCulture),
            [MetadataPrefix + "state"] = state,
            [MetadataPrefix + "author"] = issue.User?.Login ?? "ghost",
            [MetadataPrefix + "url"] = issue.HtmlUrl,
            [MetadataPrefix + "createdAt"] = issue.CreatedAt.UtcDateTime.ToString("O"),
            [MetadataPrefix + "commentCount"] = visibleComments.Count.ToString(CultureInfo.InvariantCulture),
        };
        AddIf(metadata, "labels", labels.Length > 0 ? string.Join(",", labels) : null);
        AddIf(metadata, "milestone", issue.Milestone?.Title);
        AddIf(metadata, "closedAt", issue.ClosedAt?.UtcDateTime.ToString("O"));
        AddIf(metadata, "mergedAt", issue.PullRequest?.MergedAt?.UtcDateTime.ToString("O"));
        AddIf(metadata, "closedBy", issue.ClosedBy?.Login);
        AddIf(metadata, "closes", Csv(closes));
        AddIf(metadata, "parent", record.Parent?.ToString(CultureInfo.InvariantCulture));
        AddIf(metadata, "children", Csv(record.Children));
        AddIf(metadata, "references", Csv(references));

        // A comment edit does not always move the issue's own updated_at, so the record's time is
        // the latest of either — which is what the sync engine's change signature compares.
        DateTimeOffset lastModified = record.Comments.Values
            .Select(c => c.UpdatedAt)
            .Append(issue.UpdatedAt)
            .Max();

        return new GitHubRenderedRecord(PathFor(issue), md.ToString(), metadata, lastModified.UtcDateTime);
    }

    /// <summary>
    /// Issue numbers a pull request's description says it closes, by GitHub's own keywords
    /// (close, fix, resolve and their inflections). GraphQL's <c>closingIssuesReferences</c> would
    /// also include links made in the sidebar, but it refuses anonymous callers.
    /// </summary>
    internal static IReadOnlyList<int> ClosingReferences(string? body) =>
        body is null
            ? []
            : [.. ClosingPattern().Matches(body).Select(m => ParseNumber(m.Groups[1].Value)).Where(n => n > 0).Distinct().Order()];

    /// <summary><c>#N</c> mentions of other records in this repository.</summary>
    internal static IReadOnlyList<int> Mentions(IEnumerable<string?> texts, IEnumerable<int> exclude)
    {
        var skip = exclude.ToHashSet();
        return
        [
            .. texts
                .Where(t => t is not null)
                .SelectMany(t => MentionPattern().Matches(t!))
                .Select(m => ParseNumber(m.Groups[1].Value))
                .Where(n => n > 0 && !skip.Contains(n))
                .Distinct()
                .Order(),
        ];
    }

    private static int ParseNumber(string s) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

    private static string Day(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Refs(IEnumerable<int> numbers) => string.Join(", ", numbers.Select(n => "#" + n));

    private static string? Csv(IReadOnlyList<int> numbers) =>
        numbers.Count > 0 ? string.Join(",", numbers) : null;

    private static void AddIf(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            metadata[MetadataPrefix + key] = value;
    }

    /// <summary>
    /// A line ended with a bare line feed whatever the host: the rendered text is what gets hashed and
    /// sized, and must not differ between a Windows and a Linux server.
    /// </summary>
    private static StringBuilder Line(this StringBuilder sb, string text) => sb.Append(text).Append('\n');

    [GeneratedRegex(@"^/(issues|pulls)/(\d{1,9})\.md$")]
    private static partial Regex RecordPathPattern();

    [GeneratedRegex(@"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?\s+#(\d{1,9})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingPattern();

    // Not preceded by a word character or '/', which would make it part of a URL, an owner/repo#N
    // cross-repository reference, or an HTML entity.
    [GeneratedRegex(@"(?<![\w/&])#(\d{1,9})\b")]
    private static partial Regex MentionPattern();
}
