using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>
/// Converts Atlassian Document Format (the JSON that Jira stores descriptions, comments and rich-text
/// fields in) to markdown. A pure function over untrusted input: no I/O, and a value of the wrong JSON
/// kind is skipped rather than thrown on. Nesting past <see cref="MaxDepth"/> keeps only its text.
/// </summary>
public static class AdfRenderer
{
    private const int MaxDepth = 64;
    private const int MaxPaddedTableCells = 50_000;

    private static readonly HashSet<string> InlineTypes = new(StringComparer.Ordinal)
    {
        "text", "hardBreak", "mention", "inlineCard", "emoji", "status", "date", "mediaInline", "placeholder",
    };

    /// <summary>Markdown for an ADF document (or any node). Null, a non-object or an empty doc gives "".</summary>
    public static string Render(JsonElement? adf, Func<string, string?>? attachmentName = null)
    {
        if (adf is not { ValueKind: JsonValueKind.Object } root)
            return string.Empty;

        var walker = new Walker(attachmentName);
        string type = TypeOf(root);
        string markdown = InlineTypes.Contains(type) ? walker.Inline(root, 0) : walker.Block(root, 0);
        return markdown.Trim();
    }

    private static string TypeOf(JsonElement node) =>
        Prop(node, "type") is { ValueKind: JsonValueKind.String } t ? t.GetString() ?? string.Empty : string.Empty;

    private static JsonElement? Prop(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out JsonElement value) ? value : null;

    private static string? StringAttr(JsonElement node, string name) =>
        Prop(node, "attrs") is { ValueKind: JsonValueKind.Object } attrs
        && Prop(attrs, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()
            : null;

    private static IEnumerable<JsonElement> Children(JsonElement node)
    {
        if (Prop(node, "content") is not { ValueKind: JsonValueKind.Array } content)
            yield break;
        foreach (JsonElement child in content.EnumerateArray())
        {
            if (child.ValueKind == JsonValueKind.Object)
                yield return child;
        }
    }

    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Backslash-escapes markdown block structure at the start of a line, so text cannot forge a heading, quote or fence.</summary>
    private static string EscapeStructure(string text)
    {
        int start = 0;
        while (start < text.Length && text[start] == ' ')
            start++;
        if (start >= text.Length)
            return text;

        string rest = text[start..];
        bool escape = rest[0] is '#' or '>' or '|'
            || rest.StartsWith("- ", StringComparison.Ordinal)
            || rest.StartsWith("+ ", StringComparison.Ordinal)
            || rest.StartsWith("* ", StringComparison.Ordinal)
            || rest.StartsWith("***", StringComparison.Ordinal)
            || rest.StartsWith("___", StringComparison.Ordinal)
            || IsOrderedMarker(rest)
            || rest.StartsWith("```", StringComparison.Ordinal)
            || rest.StartsWith("~~~", StringComparison.Ordinal)
            || rest.StartsWith("---", StringComparison.Ordinal)
            || rest.All(c => c is '=' or '-' or ' ');
        return escape ? text[..start] + "\\" + rest : text;
    }

    private static bool IsOrderedMarker(string text)
    {
        int digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            digits++;
        return digits > 0 && digits < text.Length - 1 && text[digits] is '.' or ')' && text[digits + 1] == ' ';
    }

    private static string EscapeLines(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(EscapeStructure));

    private static string Quote(string text) =>
        string.Join("\n", text.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));

    private static string Indent(string text, int width)
    {
        string pad = new(' ', width);
        return string.Join("\n", text.Split('\n').Select((l, i) => i == 0 || l.Length == 0 ? l : pad + l));
    }

    private static string SafeLanguage(string language) =>
        new(language.Where(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '#' or '.' or '_' or '-').ToArray());

    private static int LongestBacktickRun(string text)
    {
        int longest = 0;
        int run = 0;
        foreach (char c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    /// <summary>Every text node under <paramref name="node"/>, found without recursion so no nesting depth can overflow the stack.</summary>
    private static string PlainText(JsonElement node)
    {
        var sb = new StringBuilder();
        var stack = new Stack<JsonElement>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            JsonElement current = stack.Pop();
            if (Prop(current, "text") is { ValueKind: JsonValueKind.String } text && text.GetString() is { Length: > 0 } s)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(Clean(s));
            }

            if (Prop(current, "content") is { ValueKind: JsonValueKind.Array } content)
            {
                foreach (JsonElement child in content.EnumerateArray().Reverse())
                {
                    if (child.ValueKind == JsonValueKind.Object)
                        stack.Push(child);
                }
            }
        }

        return sb.ToString();
    }

    private static string? HttpHref(string? href)
    {
        string trimmed = (href ?? string.Empty).Trim();
        if (trimmed.Length == 0
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var sb = new StringBuilder(trimmed.Length);
        foreach (char c in trimmed)
        {
            if (c is ' ' or '(' or ')' or '<' or '>' or '[' or ']' || char.IsControl(c))
                sb.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }

        return sb.ToString();
    }

    private sealed class Walker(Func<string, string?>? attachmentName)
    {
        /// <summary>Renders one block-level node. Inline nodes met here become a paragraph of their own.</summary>
        public string Block(JsonElement node, int depth)
        {
            if (depth > MaxDepth)
                return EscapeLines(PlainText(node));

            string type = TypeOf(node);
            switch (type)
            {
                case "paragraph":
                    return EscapeLines(InlineChildren(node, depth).Trim());
                case "heading":
                {
                    string text = Clean(InlineChildren(node, depth));
                    return text.Length == 0 ? string.Empty : new string('#', Level(node)) + " " + text;
                }
                case "bulletList":
                    return List(node, ordered: false, depth);
                case "orderedList":
                    return List(node, ordered: true, depth);
                case "blockquote":
                {
                    string body = Blocks(node, depth);
                    return body.Length == 0 ? string.Empty : Quote(body);
                }
                case "rule":
                    return "---";
                case "codeBlock":
                    return CodeBlock(node);
                case "panel":
                    return Panel(node, depth);
                case "expand":
                case "nestedExpand":
                    return Expand(node, depth);
                case "table":
                    return Table(node, depth);
                case "blockCard":
                    return HttpHref(StringAttr(node, "url")) ?? string.Empty;
                case "media":
                    return Media(node);
                case "mediaSingle":
                case "mediaGroup":
                    return string.Join("\n", Children(node).Select(c => TypeOf(c) == "media" ? Media(c) : EscapeLines(InlineChildren(c, depth + 1).Trim())).Where(s => s.Length > 0));
                case "taskList":
                    return TaskList(node, depth);
                case "decisionList":
                    return string.Join("\n", Children(node).Select(c => "- " + Clean(InlineChildren(c, depth + 1))));
                default:
                    if (InlineTypes.Contains(type))
                        return EscapeLines(Inline(node, depth).Trim());
                    if (Prop(node, "content") is null && Prop(node, "text") is { ValueKind: JsonValueKind.String } plain)
                        return EscapeLines(Clean(plain.GetString() ?? string.Empty));
                    return Blocks(node, depth);
            }
        }

        /// <summary>The node's children as blocks; consecutive inline children are gathered into one paragraph.</summary>
        private List<(string Text, bool IsList)> BlockParts(JsonElement node, int depth)
        {
            var parts = new List<(string, bool)>();
            var run = new StringBuilder();

            void Flush()
            {
                if (run.ToString().Trim() is { Length: > 0 } paragraph)
                    parts.Add((EscapeLines(paragraph), false));
                run.Clear();
            }

            foreach (JsonElement child in Children(node))
            {
                string type = TypeOf(child);
                if (InlineTypes.Contains(type))
                {
                    run.Append(Inline(child, depth + 1));
                    continue;
                }

                Flush();
                string block = Block(child, depth + 1);
                if (block.Length > 0)
                    parts.Add((block, type is "bulletList" or "orderedList" or "taskList"));
            }

            Flush();
            return parts;
        }

        private string Blocks(JsonElement node, int depth) =>
            string.Join("\n\n", BlockParts(node, depth).Select(p => p.Text));

        private string InlineChildren(JsonElement node, int depth)
        {
            var sb = new StringBuilder();
            foreach (JsonElement child in Children(node))
                sb.Append(Inline(child, depth + 1));
            return sb.ToString();
        }

        public string Inline(JsonElement node, int depth)
        {
            if (depth > MaxDepth)
                return PlainText(node);

            switch (TypeOf(node))
            {
                case "text":
                    return TextNode(node);
                case "hardBreak":
                    return "  \n";
                case "mention":
                {
                    string text = (StringAttr(node, "text") ?? string.Empty).Trim();
                    return text.StartsWith("@@", StringComparison.Ordinal) ? "@" + text.TrimStart('@') : text;
                }
                case "inlineCard":
                    return HttpHref(StringAttr(node, "url")) ?? string.Empty;
                case "status":
                    return Clean(StringAttr(node, "text") ?? string.Empty);
                case "emoji":
                {
                    string text = StringAttr(node, "text") ?? string.Empty;
                    return text.Length > 0 ? text : StringAttr(node, "shortName") ?? string.Empty;
                }
                case "date":
                    return Date(node);
                case "media":
                case "mediaInline":
                    return Media(node);
                default:
                    if (Prop(node, "content") is null && Prop(node, "text") is { ValueKind: JsonValueKind.String } plain)
                        return plain.GetString() ?? string.Empty;
                    return InlineChildren(node, depth);
            }
        }

        private static string TextNode(JsonElement node)
        {
            if (Prop(node, "text") is not { ValueKind: JsonValueKind.String } value || value.GetString() is not { Length: > 0 } text)
                return string.Empty;

            bool code = false, strong = false, em = false, strike = false;
            string? href = null;
            if (Prop(node, "marks") is { ValueKind: JsonValueKind.Array } marks)
            {
                foreach (JsonElement mark in marks.EnumerateArray())
                {
                    switch (TypeOf(mark))
                    {
                        case "code": code = true; break;
                        case "strong": strong = true; break;
                        case "em": em = true; break;
                        case "strike": strike = true; break;
                        case "link": href ??= HttpHref(StringAttr(mark, "href")); break;
                    }
                }
            }

            // Marks hug the words: surrounding whitespace stays outside so "**bold **" never breaks the markup.
            string core = text.Trim();
            if (core.Length == 0)
                return text;
            string lead = text[..(text.Length - text.TrimStart().Length)];
            string trail = text[(text.TrimEnd().Length)..];

            if (code)
            {
                string ticks = new('`', LongestBacktickRun(core) + 1);
                string pad = core[0] == '`' || core[^1] == '`' ? " " : string.Empty;
                core = ticks + pad + core + pad + ticks;
            }

            if (strong)
                core = "**" + core + "**";
            if (em)
                core = "*" + core + "*";
            if (strike)
                core = "~~" + core + "~~";
            if (href is not null)
                core = "[" + (code ? core : core.Replace("[", "\\[").Replace("]", "\\]")) + "](" + href + ")";
            return lead + core + trail;
        }

        private static string Date(JsonElement node)
        {
            if (Prop(node, "attrs") is not { ValueKind: JsonValueKind.Object } attrs || Prop(attrs, "timestamp") is not { } stamp)
                return string.Empty;

            long millis;
            if (stamp.ValueKind == JsonValueKind.Number)
            {
                if (!stamp.TryGetInt64(out millis))
                    return string.Empty;
            }
            else if (stamp.ValueKind != JsonValueKind.String
                || !long.TryParse(stamp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out millis))
            {
                return string.Empty;
            }

            if (millis is < -62_135_596_800_000 or > 253_402_300_799_999)
                return string.Empty;
            return DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>A media id is not an attachment id, so the alt text (the file name) wins; the resolver is a fallback.</summary>
        private string Media(JsonElement node)
        {
            string name = Clean(StringAttr(node, "alt") ?? string.Empty);
            if (name.Length == 0 && attachmentName is not null && StringAttr(node, "id") is { Length: > 0 } id)
            {
                try
                {
                    name = Clean(attachmentName(id) ?? string.Empty);
                }
                catch (Exception)
                {
                    // Best effort: a name that cannot be resolved leaves the placeholder out.
                }
            }

            return name.Length > 0 ? "[attachment: " + name + "]" : string.Empty;
        }

        private static int Level(JsonElement node) =>
            Prop(node, "attrs") is { ValueKind: JsonValueKind.Object } attrs
            && Prop(attrs, "level") is { ValueKind: JsonValueKind.Number } level
            && level.TryGetInt32(out int value)
                ? Math.Clamp(value, 1, 6)
                : 1;

        private static string CodeBlock(JsonElement node)
        {
            var sb = new StringBuilder();
            foreach (JsonElement child in Children(node))
            {
                if (Prop(child, "text") is { ValueKind: JsonValueKind.String } text)
                    sb.Append(text.GetString());
            }

            string code = sb.ToString().TrimEnd('\n', '\r');
            string fence = new('`', Math.Max(3, LongestBacktickRun(code) + 1));
            string language = SafeLanguage(StringAttr(node, "language") ?? string.Empty);
            return fence + language + "\n" + code + "\n" + fence;
        }

        private string Panel(JsonElement node, int depth)
        {
            string label = (StringAttr(node, "panelType") ?? string.Empty).ToLowerInvariant() switch
            {
                "info" => "Info",
                "note" => "Note",
                "warning" => "Warning",
                "success" => "Success",
                "error" => "Error",
                _ => "Panel",
            };
            string body = Blocks(node, depth);
            return body.Length == 0 ? string.Empty : Quote(label + ": " + body);
        }

        private string Expand(JsonElement node, int depth)
        {
            string title = EscapeStructure(Clean(StringAttr(node, "title") ?? string.Empty));
            string body = Blocks(node, depth);
            return string.Join("\n\n", new[] { title, body }.Where(s => s.Length > 0));
        }

        private string List(JsonElement node, bool ordered, int depth)
        {
            int number = Prop(node, "attrs") is { ValueKind: JsonValueKind.Object } attrs
                && Prop(attrs, "order") is { ValueKind: JsonValueKind.Number } order
                && order.TryGetInt32(out int start)
                    ? Math.Max(0, start)
                    : 1;

            var items = new List<string>();
            foreach (JsonElement item in Children(node))
            {
                string marker = ordered ? number.ToString(CultureInfo.InvariantCulture) + ". " : "- ";
                number++;

                List<(string Text, bool IsList)> parts = BlockParts(item, depth);
                var body = new StringBuilder();
                for (int i = 0; i < parts.Count; i++)
                {
                    if (i > 0)
                        body.Append(parts[i].IsList || parts[i - 1].IsList ? "\n" : "\n\n");
                    body.Append(parts[i].Text);
                }

                items.Add((marker + Indent(body.ToString(), marker.Length)).TrimEnd());
            }

            return string.Join("\n", items);
        }

        private string TaskList(JsonElement node, int depth)
        {
            var lines = new List<string>();
            foreach (JsonElement child in Children(node))
            {
                if (TypeOf(child) == "taskList")
                {
                    string nested = Block(child, depth + 1);
                    if (nested.Length > 0)
                        lines.Add("  " + Indent(nested, 2));
                    continue;
                }

                bool done = string.Equals(StringAttr(child, "state"), "DONE", StringComparison.OrdinalIgnoreCase);
                lines.Add((done ? "- [x] " : "- [ ] ") + Clean(InlineChildren(child, depth + 1)));
            }

            return string.Join("\n", lines).TrimEnd();
        }

        private string Table(JsonElement node, int depth)
        {
            var rows = new List<List<string>>();
            foreach (JsonElement row in Children(node))
            {
                if (TypeOf(row) != "tableRow")
                    continue;
                rows.Add(Children(row).Select(cell => Cell(cell, depth + 2)).ToList());
            }

            if (rows.Count == 0)
                return string.Empty;

            int width = Math.Max(1, rows.Max(r => r.Count));
            bool pad = (long)rows.Count * width <= MaxPaddedTableCells;
            var sb = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                IEnumerable<string> cells = pad ? rows[i].Concat(Enumerable.Repeat(string.Empty, width - rows[i].Count)) : rows[i];
                sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                if (i == 0)
                    sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", pad ? width : Math.Max(1, rows[0].Count)))).Append('\n');
            }

            return sb.ToString().TrimEnd('\n');
        }

        private string Cell(JsonElement cell, int depth)
        {
            string text = string.Join("\n", BlockParts(cell, depth).Select(p => p.Text));
            return text.Replace("  \n", "\n").Replace("\r\n", "\n").Replace('\r', '\n').Trim()
                .Replace("|", "\\|")
                .Replace("\n", "<br>");
        }
    }
}
