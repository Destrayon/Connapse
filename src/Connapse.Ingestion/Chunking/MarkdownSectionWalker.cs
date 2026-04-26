using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Section produced by walking a Markdown document's heading structure.
/// </summary>
internal record MarkdownSection(
    string HeaderPath,
    Dictionary<string, string> LevelMap,
    int Depth,
    int SpanStart,
    int SpanEnd);

/// <summary>
/// Walks a parsed Markdig <see cref="MarkdownDocument"/> to produce a list of
/// sections demarcated by headings. Code fences, tables, HTML blocks, and
/// front-matter are inert here — they're atomic blocks in Markdig's AST so the
/// walker simply skips them as non-heading blocks.
/// </summary>
internal static class MarkdownSectionWalker
{
    public static List<MarkdownSection> Walk(string content, MarkdownDocument doc)
    {
        var sections = new List<MarkdownSection>();
        var stack = new List<(int Level, string Text)>();
        int currentSpanStart = 0;

        foreach (Block block in doc)
        {
            if (block is not HeadingBlock heading)
            {
                continue;
            }

            // Close the previous section: it spans from the previous heading's body-start
            // up to this heading's block start. Skip when nothing has been opened yet
            // (no preamble before the first heading).
            int sectionEnd = heading.Span.Start;
            if (sectionEnd > currentSpanStart || stack.Count > 0)
            {
                sections.Add(BuildSection(stack, currentSpanStart, sectionEnd));
            }

            while (stack.Count > 0 && stack[^1].Level >= heading.Level)
            {
                stack.RemoveAt(stack.Count - 1);
            }
            stack.Add((heading.Level, ExtractHeadingText(heading)));

            // Markdig's Span.End is inclusive of the last character of the block,
            // so the body of this new section starts one past Span.End.
            currentSpanStart = heading.Span.End + 1;
        }

        // Final section: from currentSpanStart to end of content. Emit even when
        // the body is empty as long as there is a heading on the stack — a trailing
        // heading with no following body still owns its own (zero-length) section.
        if (stack.Count > 0 || currentSpanStart < content.Length)
        {
            sections.Add(BuildSection(stack, currentSpanStart, content.Length));
        }

        // Drop only sections with strictly inverted spans; keep zero-length trailing
        // sections so a heading with no body still surfaces as its own section.
        sections.RemoveAll(s => s.SpanEnd < s.SpanStart);

        return sections;
    }

    public static bool HasMarkdownStructure(MarkdownDocument doc)
    {
        // Descendants (not just top-level) so fenced code inside lists/blockquotes
        // is detected, e.g. a `bash` fence inside a `Setup steps:` bullet item.
        return doc.Descendants<HeadingBlock>().Any()
            || doc.Descendants<FencedCodeBlock>().Any();
    }

    private static MarkdownSection BuildSection(
        IReadOnlyList<(int Level, string Text)> stack,
        int start,
        int end)
    {
        string path = stack.Count == 0 ? string.Empty : string.Join(" > ", stack.Select(s => s.Text));
        var levelMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((int level, string text) in stack)
        {
            levelMap[$"H{level}"] = text;
        }
        return new MarkdownSection(path, levelMap, stack.Count, start, end);
    }

    private static string ExtractHeadingText(HeadingBlock heading)
    {
        if (heading.Inline is null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        AppendInlines(heading.Inline, sb);
        return sb.ToString().Trim();
    }

    private static void AppendInlines(ContainerInline container, System.Text.StringBuilder sb)
    {
        foreach (Inline inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit: sb.Append(lit.Content); break;
                case CodeInline code: sb.Append(code.Content); break;
                case LineBreakInline: sb.Append(' '); break;
                case ContainerInline child: AppendInlines(child, sb); break;
                // AutolinkInline, HtmlEntityInline, etc. — fall back to ToString,
                // which IS sensible for those types (they have meaningful ToString impls).
                default: sb.Append(inline.ToString()); break;
            }
        }
    }
}
