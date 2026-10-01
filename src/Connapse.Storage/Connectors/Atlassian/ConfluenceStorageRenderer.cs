using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>A Confluence page comment, with its body still in storage format.</summary>
public sealed record ConfluenceComment(string AuthorName, DateTimeOffset Created, string StorageBody);

/// <summary>Everything the renderer needs to turn one page into markdown.</summary>
/// <param name="Breadcrumb">Space name, ancestor titles, then the page title.</param>
/// <param name="StorageBody">The page body in Confluence storage format (XHTML with <c>ac:</c> and <c>ri:</c> elements).</param>
/// <param name="Comments">Footer and inline comments to append after the body.</param>
/// <param name="UserNames">Account id to display name, for rendering user mentions.</param>
public sealed record ConfluenceRenderInput(
    IReadOnlyList<string> Breadcrumb,
    string StorageBody,
    IReadOnlyList<ConfluenceComment> Comments,
    IReadOnlyDictionary<string, string> UserNames);

/// <summary>The markdown, plus the titles of pages the content links to.</summary>
public sealed record ConfluenceRenderOutput(string Markdown, IReadOnlyList<string> LinkedPageTitles);

/// <summary>
/// Converts Confluence storage format to markdown. A pure function: no I/O, never throws on
/// malformed input. The breadcrumb becomes the top heading and the page's own headings shift
/// down a level, so the document-aware chunker carries the breadcrumb onto every chunk.
/// </summary>
public static partial class ConfluenceStorageRenderer
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "hr", "img", "col", "input", "area", "base", "link", "meta", "source", "wbr",
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "div", "section", "article", "header", "footer", "aside", "main", "nav", "figure", "figcaption",
        "dl", "dt", "dd", "details", "summary", "center",
    };

    [GeneratedRegex(@"<!\[CDATA\[(.*?)\]\]>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CdataPattern();

    [GeneratedRegex("""<([A-Za-z][\w:.-]*)((?:\s+[^\s=>/]+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+))?)*)\s*/>""", RegexOptions.CultureInvariant)]
    private static partial Regex SelfClosingPattern();

    [GeneratedRegex(@"[\s ]+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^[\s>]*(`{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex FenceOpenPattern();

    /// <summary>Renders the page, then its comments, to markdown.</summary>
    public static ConfluenceRenderOutput Render(ConfluenceRenderInput input)
    {
        var state = new RenderState(input.UserNames ?? new Dictionary<string, string>());
        var sb = new StringBuilder();

        string[] crumbs = (input.Breadcrumb ?? [])
            .Select(c => CleanSingleLine((c ?? string.Empty).Replace('>', '›')))
            .Where(c => c.Length > 0)
            .ToArray();
        if (crumbs.Length > 0)
            sb.Append("# ").Append(string.Join(" > ", crumbs)).Append("\n\n");

        sb.Append(RenderBody(input.StorageBody, state, flattenHeadings: false));

        List<ConfluenceComment> comments = (input.Comments ?? []).ToList();
        if (comments.Count > 0)
        {
            sb.Append("\n\n## Comments\n\n");
            foreach (ConfluenceComment comment in comments)
            {
                string author = CleanSingleLine(comment.AuthorName ?? string.Empty);
                string date = comment.Created.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                sb.Append("--- Comment by ").Append(author).Append(", ").Append(date).Append(" ---\n");
                sb.Append(RenderBody(comment.StorageBody, state, flattenHeadings: true)).Append("\n\n");
            }
        }

        return new ConfluenceRenderOutput(NormalizeLines(sb.ToString(), maxBlankLines: 1) + "\n", state.LinkedTitles);
    }

    /// <summary>The account ids of every user mentioned in a storage-format body.</summary>
    public static IReadOnlySet<string> MentionedAccountIds(string storageBody)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            IElement? body = Parse(storageBody);
            if (body is null)
                return ids;
            foreach (IElement user in body.QuerySelectorAll("*").Where(e => e.LocalName == "ri:user"))
            {
                string? id = user.GetAttribute("ri:account-id");
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id.Trim());
            }
        }
        catch (Exception)
        {
            // Best effort: a body we cannot parse mentions nobody we can look up.
        }

        return ids;
    }

    private static string RenderBody(string? storageBody, RenderState state, bool flattenHeadings)
    {
        if (string.IsNullOrWhiteSpace(storageBody))
            return string.Empty;

        try
        {
            IElement? body = Parse(storageBody);
            if (body is null)
                return TextFallback(storageBody);
            var walker = new Walker(state, flattenHeadings);
            return walker.Children(body).Trim();
        }
        catch (Exception)
        {
            return TextFallback(storageBody);
        }
    }

    /// <summary>
    /// Storage format is an XHTML fragment with undeclared namespaces and HTML entities, so a strict
    /// XML parser rejects it. The HTML parser accepts prefixed names, but does not honour
    /// self-closing syntax on unknown elements and turns CDATA into comments; both are fixed up first.
    /// </summary>
    private static IElement? Parse(string storageBody)
    {
        string html = CdataPattern().Replace(storageBody, m => WebUtility.HtmlEncode(m.Groups[1].Value));
        html = SelfClosingPattern().Replace(html, m =>
            VoidElements.Contains(m.Groups[1].Value)
                ? m.Value
                : $"<{m.Groups[1].Value}{m.Groups[2].Value}></{m.Groups[1].Value}>");
        return new HtmlParser().ParseDocument("<!DOCTYPE html><html><body>" + html + "</body></html>").Body;
    }

    private static string TextFallback(string storageBody) =>
        WhitespacePattern().Replace(WebUtility.HtmlDecode(TagPattern().Replace(storageBody, " ")), " ").Trim();

    private static string CleanSingleLine(string text) =>
        WhitespacePattern().Replace(text, " ").Trim();

    /// <summary>
    /// Trims trailing whitespace from every line and caps runs of blank lines, leaving the inside
    /// of fenced code blocks' blank lines alone.
    /// </summary>
    private static string NormalizeLines(string text, int maxBlankLines)
    {
        var result = new List<string>();
        string? fence = null;
        int blanks = 0;
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.TrimEnd();
            string stripped = line.TrimStart(' ', '>').Trim();
            if (fence is null)
            {
                Match open = FenceOpenPattern().Match(line);
                if (open.Success)
                    fence = open.Groups[1].Value;
            }
            else if (stripped == fence)
            {
                fence = null;
            }

            if (fence is null && line.Length == 0)
            {
                if (++blanks <= maxBlankLines && result.Count > 0)
                    result.Add(line);
                continue;
            }

            blanks = 0;
            result.Add(line);
        }

        return string.Join("\n", result).Trim('\n');
    }

    private sealed class RenderState(IReadOnlyDictionary<string, string> userNames)
    {
        private readonly HashSet<string> _seenTitles = new(StringComparer.Ordinal);

        public List<string> LinkedTitles { get; } = [];

        public string UserName(string? accountId) =>
            accountId is not null && userNames.TryGetValue(accountId, out string? name) && !string.IsNullOrWhiteSpace(name)
                ? CleanSingleLine(name)
                : "a user";

        public void RecordLink(string title)
        {
            if (_seenTitles.Add(title))
                LinkedTitles.Add(title);
        }
    }

    private sealed class Walker(RenderState state, bool flattenHeadings)
    {
        public string Children(INode node)
        {
            var sb = new StringBuilder();
            foreach (INode child in node.ChildNodes)
                sb.Append(Node(child));
            return sb.ToString();
        }

        private string Node(INode node)
        {
            if (node.NodeType == NodeType.Text)
                return WhitespacePattern().Replace(node.TextContent, " ");
            return node is IElement element ? Element(element) : string.Empty;
        }

        private string Element(IElement e)
        {
            string name = e.LocalName;
            switch (name)
            {
                case "script" or "style" or "ac:parameter" or "ac:placeholder" or "ac:adf-fallback":
                    return string.Empty;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    return Heading(e, name[1] - '0');
                case "p":
                    return Block(Children(e));
                case "br":
                    return "\n";
                case "hr":
                    return "\n\n---\n\n";
                case "strong" or "b":
                    return Wrap("**", Children(e));
                case "em" or "i":
                    return Wrap("*", Children(e));
                case "s" or "del" or "strike":
                    return Wrap("~~", Children(e));
                case "code":
                    return InlineCode(e.TextContent);
                case "pre":
                    return Block(Fence(e.TextContent, null));
                case "a":
                    return Anchor(e);
                case "blockquote":
                    return Block(Quote(Children(e)));
                case "ul" or "ol":
                    return Block(List(e));
                case "table":
                    return Block(Table(e));
                case "time":
                    return e.GetAttribute("datetime") ?? Children(e);
                case "ac:structured-macro" or "ac:macro":
                    return Macro(e);
                case "ac:link":
                    return Link(e);
                case "ac:image":
                    return Image(e);
                case "ac:emoticon":
                    return e.GetAttribute("ac:emoji-fallback") ?? string.Empty;
                case "ac:task-list":
                    return Block(TaskList(e));
                case "ri:user":
                    return state.UserName(e.GetAttribute("ri:account-id")?.Trim());
            }

            if (BlockElements.Contains(name) || name.StartsWith("ac:layout", StringComparison.Ordinal))
                return Block(Children(e));
            return Children(e);
        }

        private static string Block(string content)
        {
            string trimmed = content.Trim();
            return trimmed.Length == 0 ? string.Empty : "\n\n" + trimmed + "\n\n";
        }

        private string Heading(IElement e, int level)
        {
            string text = CleanSingleLine(Children(e));
            if (text.Length == 0)
                return string.Empty;
            return flattenHeadings
                ? Block("**" + text + "**")
                : Block(new string('#', Math.Min(level + 1, 6)) + " " + text);
        }

        private static string Wrap(string marker, string inner)
        {
            string core = inner.Trim();
            if (core.Length == 0)
                return inner.Length > 0 ? " " : string.Empty;
            string lead = inner.StartsWith(' ') ? " " : string.Empty;
            string trail = inner.EndsWith(' ') ? " " : string.Empty;
            return lead + marker + core + marker + trail;
        }

        private static string InlineCode(string text)
        {
            string flat = CleanSingleLine(text);
            if (flat.Length == 0)
                return string.Empty;
            string ticks = flat.Contains('`') ? "``" : "`";
            return ticks + flat + ticks;
        }

        private static string Fence(string body, string? language)
        {
            string text = body.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n').TrimEnd();
            int longest = 0;
            foreach (Match run in Regex.Matches(text, "`+"))
                longest = Math.Max(longest, run.Length);
            string fence = new('`', Math.Max(3, longest + 1));
            return fence + (language ?? string.Empty) + "\n" + text + "\n" + fence;
        }

        private static string Quote(string body) =>
            string.Join("\n", body.Trim().Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));

        private string Anchor(IElement e)
        {
            string text = Children(e).Trim();
            string href = (e.GetAttribute("href") ?? string.Empty).Trim();
            if (text.Length == 0)
                return string.Empty;
            return href.Length == 0 || href.Contains(' ') ? text : $"[{text}]({href})";
        }

        private string List(IElement list)
        {
            bool ordered = list.LocalName == "ol";
            var lines = new List<string>();
            int index = 1;
            foreach (IElement item in list.Children.Where(c => c.LocalName == "li"))
            {
                string marker = ordered ? $"{index++}. " : "- ";
                lines.Add(Item(marker, Children(item)));
            }

            return string.Join("\n", lines);
        }

        private static string Item(string marker, string body)
        {
            string tight = NormalizeLines(body, maxBlankLines: 0);
            string pad = new(' ', marker.Length);
            string[] parts = tight.Split('\n');
            return marker + string.Join("\n", parts.Select((p, i) => i == 0 || p.Length == 0 ? p : pad + p));
        }

        private string TaskList(IElement list)
        {
            var lines = new List<string>();
            foreach (IElement task in list.Children.Where(c => c.LocalName == "ac:task"))
            {
                string status = task.Children.FirstOrDefault(c => c.LocalName == "ac:task-status")?.TextContent.Trim() ?? string.Empty;
                IElement? body = task.Children.FirstOrDefault(c => c.LocalName == "ac:task-body");
                string marker = status.Equals("complete", StringComparison.OrdinalIgnoreCase) ? "- [x] " : "- [ ] ";
                lines.Add(Item(marker, body is null ? string.Empty : Children(body)));
            }

            return string.Join("\n", lines);
        }

        private string Table(IElement table)
        {
            List<List<string>> rows = table.QuerySelectorAll("tr")
                .Where(tr => tr.ParentElement is not null && ReferenceEquals(Owner(tr), table))
                .Select(tr => tr.Children
                    .Where(c => c.LocalName is "td" or "th")
                    .Select(Cell)
                    .ToList())
                .Where(r => r.Count > 0)
                .ToList();
            if (rows.Count == 0)
                return string.Empty;

            int width = rows.Max(r => r.Count);
            var sb = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                sb.Append("| ").Append(string.Join(" | ", rows[i].Concat(Enumerable.Repeat(string.Empty, width - rows[i].Count)))).Append(" |\n");
                if (i == 0)
                    sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).Append('\n');
            }

            return sb.ToString();
        }

        private static IElement? Owner(IElement tr)
        {
            for (IElement? p = tr.ParentElement; p is not null; p = p.ParentElement)
            {
                if (p.LocalName == "table")
                    return p;
            }

            return null;
        }

        private string Cell(IElement cell) =>
            CleanSingleLine(Children(cell)).Replace("|", "\\|");

        private string Macro(IElement macro)
        {
            string name = (macro.GetAttribute("ac:name") ?? string.Empty).Trim().ToLowerInvariant();
            string Param(string key) => macro.Children
                .Where(c => c.LocalName == "ac:parameter" && c.GetAttribute("ac:name") == key)
                .Select(c => c.TextContent.Trim())
                .FirstOrDefault() ?? string.Empty;
            IElement? rich = macro.Children.FirstOrDefault(c => c.LocalName == "ac:rich-text-body");
            IElement? plain = macro.Children.FirstOrDefault(c => c.LocalName == "ac:plain-text-body");

            switch (name)
            {
                case "code":
                    return Block(Fence(plain?.TextContent ?? string.Empty, Param("language")));
                case "info" or "note" or "warning" or "tip" or "panel":
                    return Block(Panel(name, Param("title"), rich));
                case "expand":
                    string title = CleanSingleLine(Param("title"));
                    return (title.Length > 0 ? Block("**" + title + "**") : string.Empty)
                        + (rich is null ? string.Empty : Block(Children(rich)));
                case "status":
                    return CleanSingleLine(Param("title"));
                case "jira":
                    return CleanSingleLine(Param("key"));
                case "include" or "excerpt-include":
                    return "[includes: " + IncludedTitle(macro) + "]";
                case "toc" or "children" or "pagetree":
                    return string.Empty;
            }

            if (rich is not null)
                return Block(Children(rich));
            return plain is not null ? Block(plain.TextContent) : string.Empty;
        }

        private string Panel(string kind, string title, IElement? body)
        {
            string label = kind switch
            {
                "info" => "Info",
                "note" => "Note",
                "warning" => "Warning",
                "tip" => "Tip",
                _ => "Panel",
            };
            string cleanTitle = CleanSingleLine(title);
            string content = body is null ? string.Empty : Children(body).Trim();
            string text = cleanTitle.Length > 0 ? $"{label}: {cleanTitle}\n\n{content}" : $"{label}: {content}";
            return Quote(text);
        }

        private static string IncludedTitle(IElement macro)
        {
            IElement? page = macro.QuerySelectorAll("*").FirstOrDefault(e => e.LocalName == "ri:page");
            string title = CleanSingleLine(page?.GetAttribute("ri:content-title") ?? string.Empty);
            return title.Length > 0 ? title : "another page";
        }

        private string Link(IElement link)
        {
            IElement? target = link.Children.FirstOrDefault(c => c.LocalName.StartsWith("ri:", StringComparison.Ordinal));
            IElement? body = link.Children.FirstOrDefault(c => c.LocalName is "ac:plain-text-link-body" or "ac:link-body");
            string text = body is null ? string.Empty : CleanSingleLine(Children(body));

            switch (target?.LocalName)
            {
                case "ri:user":
                    return state.UserName(target.GetAttribute("ri:account-id")?.Trim());
                case "ri:attachment":
                    return Attachment(target);
                case "ri:page":
                    string title = CleanSingleLine(target.GetAttribute("ri:content-title") ?? string.Empty);
                    if (title.Length > 0)
                        state.RecordLink(title);
                    return text.Length > 0 ? text : title;
                case "ri:blog-post":
                    return text.Length > 0 ? text : CleanSingleLine(target.GetAttribute("ri:content-title") ?? string.Empty);
                case "ri:space":
                    return text.Length > 0 ? text : CleanSingleLine(target.GetAttribute("ri:space-key") ?? string.Empty);
                default:
                    return text;
            }
        }

        private static string Attachment(IElement target)
        {
            string file = CleanSingleLine(target.GetAttribute("ri:filename") ?? string.Empty);
            return file.Length > 0 ? $"[attachment: {file}]" : "[attachment]";
        }

        private static string Image(IElement image)
        {
            IElement? attachment = image.Children.FirstOrDefault(c => c.LocalName == "ri:attachment");
            if (attachment is not null)
                return Attachment(attachment);
            string alt = CleanSingleLine(image.GetAttribute("ac:alt") ?? string.Empty);
            return alt.Length > 0 ? $"[image: {alt}]" : "[image]";
        }
    }
}
