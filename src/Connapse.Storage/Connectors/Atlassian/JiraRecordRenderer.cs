using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>A comment as the issue document shows it.</summary>
internal sealed record JiraComment(string AuthorName, DateTimeOffset Created, JsonElement? Body);

/// <summary>
/// Turns a Jira issue into the markdown record document that is indexed: the breadcrumb, a header
/// of its fields, the description, each rich-text custom field and the comments it is given. A
/// pure function over Jira's answer; which comments are passed in is the caller's decision.
/// </summary>
internal static partial class JiraRecordRenderer
{
    /// <summary>
    /// <c>Project name &gt; KEY summary</c>. A <c>&gt;</c> inside the project name or summary becomes
    /// <c>›</c> so it cannot forge a level, and each part is kept to one line.
    /// </summary>
    public static string Breadcrumb(string projectName, string key, string summary) =>
        $"{Title(projectName)} > {OneLine(key)} {Title(summary)}".TrimEnd();

    /// <summary>
    /// The issue document. <paramref name="issue"/> is Jira's <c>GET issue/{id}</c> answer;
    /// <paramref name="richTextFieldIds"/> are rendered, in order, under their names.
    /// </summary>
    public static string RenderIssue(
        string projectName,
        JsonElement issue,
        IReadOnlyList<JiraComment> comments,
        IReadOnlyList<string> richTextFieldIds,
        IReadOnlyDictionary<string, string> fieldNames)
    {
        JsonElement fields = Prop(issue, "fields") ?? default;
        string key = Str(issue, "key") ?? "";
        var md = new StringBuilder();
        md.Append("# ").Line(Breadcrumb(projectName, key, Str(fields, "summary") ?? ""));
        md.Append('\n');

        Item(md, ("Type", Name(fields, "issuetype")), ("Status", Name(fields, "status")), ("Priority", Name(fields, "priority")));
        Item(md, ("Assignee", Person(fields, "assignee")), ("Reporter", Person(fields, "reporter")));
        Item(md,
            ("Labels", List(fields, "labels", e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)),
            ("Components", List(fields, "components", e => Str(e, "name"))),
            ("Fix versions", List(fields, "fixVersions", e => Str(e, "name"))));
        if (Prop(fields, "parent") is { ValueKind: JsonValueKind.Object } parent)
            Item(md, ("Parent", IssueRef(parent)));
        foreach (string link in Links(fields))
            Item(md, ("Link", link));
        Item(md, ("Created", Day(fields, "created")), ("Updated", Day(fields, "updated")));

        Section(md, "Description", Prop(fields, "description"));
        foreach (string id in richTextFieldIds)
            Section(md, fieldNames.GetValueOrDefault(id) ?? id, Prop(fields, id));

        if (comments.Count > 0)
        {
            md.Append('\n').Line("## Comments");
            foreach (var comment in comments)
            {
                md.Append('\n')
                    .Line($"--- Comment by {OneLine(comment.AuthorName)}, {comment.Created.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ---");
                string body = Body(comment.Body);
                if (body.Length > 0)
                    md.Line(body);
            }
        }

        return md.ToString();
    }

    /// <summary>
    /// Jira's timestamps (<c>2026-10-01T12:00:00.000+0000</c>) as an instant, or null when the value
    /// is not one. The offset may come without a colon, which .NET's ISO parser refuses.
    /// </summary>
    public static DateTimeOffset? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string normalized = ColonlessOffset().Replace(value.Trim(), "$1:$2");
        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
    }

    private static void Section(StringBuilder md, string heading, JsonElement? value)
    {
        string body = Body(value);
        if (body.Length > 0)
            md.Append('\n').Line("## " + OneLine(heading)).Line(body);
    }

    /// <summary>
    /// ADF as markdown, with any line shaped like a comment delimiter escaped: the record chunker
    /// splits on those lines, and anyone who can comment could otherwise pass text off as someone
    /// else's comment. A plain string (a field that is not ADF) is rendered as one paragraph.
    /// </summary>
    private static string Body(JsonElement? value)
    {
        JsonElement? adf = value is { ValueKind: JsonValueKind.String } text
            ? JsonSerializer.SerializeToElement(new
            {
                type = "doc",
                content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = text.GetString() } } } },
            })
            : value;
        return DelimiterShapedLine().Replace(AdfRenderer.Render(adf).Replace("\r\n", "\n"), @"\$0");
    }

    /// <summary>One header line: the parts that have a value, joined; nothing when none has.</summary>
    private static void Item(StringBuilder md, params (string Label, string? Value)[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Label}: {OneLine(p.Value!)}").ToList();
        if (present.Count > 0)
            md.Append("- ").Line(string.Join(" · ", present));
    }

    /// <summary>Each issue link as <c>{type.outward|inward} {key} {summary}</c>, by which side the other issue is on.</summary>
    private static IEnumerable<string> Links(JsonElement fields)
    {
        if (Prop(fields, "issuelinks") is not { ValueKind: JsonValueKind.Array } links)
            yield break;

        foreach (var link in links.EnumerateArray())
        {
            JsonElement type = Prop(link, "type") ?? default;
            (string? verb, JsonElement? other) = Prop(link, "outwardIssue") is { ValueKind: JsonValueKind.Object } outward
                ? (Str(type, "outward"), outward)
                : (Str(type, "inward"), Prop(link, "inwardIssue"));
            if (other is { ValueKind: JsonValueKind.Object } target && IssueRef(target) is { } reference)
                yield return $"{verb ?? "relates to"} {reference}";
        }
    }

    private static string? IssueRef(JsonElement issue) =>
        Str(issue, "key") is { } key
            ? $"{key} {Str(Prop(issue, "fields") ?? default, "summary")}".TrimEnd()
            : null;

    private static string? Name(JsonElement fields, string field) => Str(Prop(fields, field) ?? default, "name");

    private static string? Person(JsonElement fields, string field) => Str(Prop(fields, field) ?? default, "displayName");

    private static string? List(JsonElement fields, string field, Func<JsonElement, string?> name) =>
        Prop(fields, field) is { ValueKind: JsonValueKind.Array } values
            ? string.Join(", ", values.EnumerateArray().Select(name).Where(n => !string.IsNullOrWhiteSpace(n)))
            : null;

    private static string? Day(JsonElement fields, string field) =>
        ParseTime(Str(fields, field))?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonElement? Prop(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : null;

    private static string? Str(JsonElement node, string name) =>
        Prop(node, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static string Title(string value) => OneLine(value).Replace('>', '›');

    /// <summary>Control characters (line breaks included) become spaces, and runs of whitespace one space.</summary>
    private static string OneLine(string value) =>
        Whitespace().Replace(new string([.. value.Select(c => char.IsControl(c) ? ' ' : c)]), " ").Trim();

    private static StringBuilder Line(this StringBuilder sb, string text) => sb.Append(text).Append('\n');

    [GeneratedRegex(@"^--- .+ ---$", RegexOptions.Multiline)]
    private static partial Regex DelimiterShapedLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"([+-][0-9]{2})([0-9]{2})$")]
    private static partial Regex ColonlessOffset();
}
