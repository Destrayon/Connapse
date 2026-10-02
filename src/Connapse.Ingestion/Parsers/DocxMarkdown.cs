using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Writes a Word document as Markdown, in document order.
/// <para>
/// The previous reader took every paragraph's InnerText, then appended every table again as
/// pipe-joined rows, so each table appeared twice. It read the body only -- no headers, footers,
/// footnotes or endnotes -- and InnerText also returned the text of tracked deletions and both
/// copies of a text box (its DrawingML version and its VML fallback).
/// </para>
/// </summary>
internal static class DocxMarkdown
{
    public static string Convert(WordprocessingDocument document, CancellationToken ct)
    {
        var main = document.MainDocumentPart;
        var body = main?.Document?.Body;
        if (main is null || body is null)
            return string.Empty;

        var styles = HeadingLevels(main);
        var output = new StringBuilder();

        // Headers and footers repeat on every page; once each is enough to make them searchable.
        // Only the ones a section uses: a document can keep parts no page shows any more, and
        // their stale text must not become searchable.
        var headerIds = body.Descendants<HeaderReference>().Select(r => r.Id?.Value).ToHashSet();
        var footerIds = body.Descendants<FooterReference>().Select(r => r.Id?.Value).ToHashSet();
        AppendDistinct(output, main.HeaderParts.Where(p => headerIds.Contains(main.GetIdOfPart(p))).Select(p => p.Header), styles, ct);

        AppendBlocks(output, body.ChildElements, styles, ct);

        AppendDistinct(output, main.FooterParts.Where(p => footerIds.Contains(main.GetIdOfPart(p))).Select(p => p.Footer), styles, ct);

        // Likewise only notes the text cites.
        var footnoteIds = body.Descendants<FootnoteReference>().Select(r => r.Id?.Value).ToHashSet();
        var endnoteIds = body.Descendants<EndnoteReference>().Select(r => r.Id?.Value).ToHashSet();
        AppendNotes(output, "Footnotes", main.FootnotesPart?.Footnotes?.Elements<Footnote>()
            .Where(n => footnoteIds.Contains(n.Id?.Value)).Select(n => (OpenXmlElement)n), styles, ct);
        AppendNotes(output, "Endnotes", main.EndnotesPart?.Endnotes?.Elements<Endnote>()
            .Where(n => endnoteIds.Contains(n.Id?.Value)).Select(n => (OpenXmlElement)n), styles, ct);

        return output.ToString().Trim();
    }

    /// <summary>Paragraphs, tables and content controls, in order; anything else is skipped.</summary>
    private static void AppendBlocks(StringBuilder output, IEnumerable<OpenXmlElement> elements,
        Dictionary<string, int> styles, CancellationToken ct)
    {
        foreach (var element in elements)
        {
            ct.ThrowIfCancellationRequested();
            switch (element)
            {
                case Paragraph paragraph:
                    string line = ParagraphMarkdown(paragraph, styles);
                    if (line.Length > 0)
                        output.Append(line).Append("\n\n");
                    break;

                case Table table:
                    string markdown = TableMarkdown(table);
                    if (markdown.Length > 0)
                        output.Append(markdown).Append("\n\n");
                    break;

                case SdtBlock sdt when sdt.SdtContentBlock is { } content:
                    AppendBlocks(output, content.ChildElements, styles, ct);
                    break;

                // Any other block-level wrapper -- custom XML, a tracked move or insertion around
                // whole paragraphs -- is entered rather than skipped, so its text is not lost.
                case OpenXmlCompositeElement wrapper
                    when wrapper.Descendants<Paragraph>().Any() || wrapper.Descendants<Table>().Any():
                    AppendBlocks(output, wrapper.ChildElements, styles, ct);
                    break;
            }
        }
    }

    private static void AppendDistinct(StringBuilder output, IEnumerable<OpenXmlCompositeElement?> parts,
        Dictionary<string, int> styles, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (part is null) continue;
            var block = new StringBuilder();
            AppendBlocks(block, part.ChildElements, styles, ct);
            string text = block.ToString().Trim();
            if (text.Length > 0 && seen.Add(text))
                output.Append(text).Append("\n\n");
        }
    }

    private static void AppendNotes(StringBuilder output, string heading, IEnumerable<OpenXmlElement>? notes,
        Dictionary<string, int> styles, CancellationToken ct)
    {
        var block = new StringBuilder();
        int number = 1;
        foreach (var note in notes ?? [])
        {
            var text = new StringBuilder();
            AppendBlocks(text, note.ChildElements, styles, ct);
            string noteText = text.ToString().Trim().Replace("\n\n", " ");
            if (noteText.Length > 0)
                block.Append('[').Append(number++).Append("] ").Append(noteText).Append('\n');
        }

        if (block.Length > 0)
            output.Append("## ").Append(heading).Append("\n\n").Append(block).Append('\n');
    }

    private static string ParagraphMarkdown(Paragraph paragraph, Dictionary<string, int> styles)
    {
        string text = Collapse(VisibleText(paragraph));
        if (text.Length == 0)
            return string.Empty;

        var properties = paragraph.ParagraphProperties;
        string? styleId = properties?.ParagraphStyleId?.Val?.Value;
        if (styleId is not null && styles.TryGetValue(styleId, out int level))
            return new string('#', level) + " " + text;

        if (properties?.NumberingProperties is not null)
            return "- " + text;

        // The author's own text, so a leading "#" or "|" is escaped rather than read as structure.
        return MarkdownText.EscapeLine(text);
    }

    /// <summary>
    /// The text a reader sees: w:t runs, tabs and breaks. Deleted text lives in w:delText and is
    /// left out; inserted text is a plain run and kept. A text box carries a VML copy under
    /// mc:Fallback for old readers, which is skipped so the box is read once.
    /// </summary>
    private static string VisibleText(OpenXmlElement element)
    {
        var builder = new StringBuilder();
        Walk(element);
        return builder.ToString();

        void Walk(OpenXmlElement node)
        {
            foreach (var child in node.ChildElements)
            {
                switch (child)
                {
                    case AlternateContent alternate:
                        // One branch, never both: the Choice, unless it holds no text a reader
                        // can see, in which case the Fallback is the only copy there is.
                        string chosen = string.Concat(alternate.Elements<AlternateContentChoice>().Take(1).Select(VisibleText));
                        builder.Append(chosen.Trim().Length > 0
                            ? chosen
                            : string.Concat(alternate.Elements<AlternateContentFallback>().Select(VisibleText)));
                        break;
                    case Text text:
                        builder.Append(text.Text);
                        break;
                    case TabChar:
                        builder.Append(' ');
                        break;
                    case Break or CarriageReturn:
                        builder.Append(' ');
                        break;
                    case Paragraph when builder.Length > 0:
                        // A paragraph nested in a text box or cell: keep it apart from its neighbour.
                        builder.Append(' ');
                        Walk(child);
                        break;
                    default:
                        Walk(child);
                        break;
                }
            }
        }
    }

    /// <summary>Each table once, as a Markdown table; its first row is the header Markdown needs.</summary>
    private static string TableMarkdown(Table table)
    {
        // Rows are laid against the grid: a row starting at a later column (gridBefore) is padded
        // in front, so each value stays under its own header. Empty rows are kept, or a later
        // data row would be promoted to the header Markdown takes from the first.
        var rows = table.Elements<TableRow>()
            .Select(row =>
            {
                int before = row.TableRowProperties?.GetFirstChild<GridBefore>()?.Val?.Value ?? 0;
                return Enumerable.Repeat(string.Empty, before).Concat(row.Elements<TableCell>().SelectMany(CellTexts)).ToList();
            })
            .ToList();
        if (rows.Count == 0 || rows.All(cells => cells.All(c => c.Length == 0)))
            return string.Empty;

        int columns = rows.Max(r => r.Count);
        var builder = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].Concat(Enumerable.Repeat(string.Empty, columns - rows[i].Count));
            builder.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (i == 0)
                builder.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns))).Append('\n');
        }
        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// A cell spanning several grid columns is repeated across them, so every row keeps the same
    /// number of columns and each value stays under its header.
    /// </summary>
    private static IEnumerable<string> CellTexts(TableCell cell)
    {
        string text = Collapse(VisibleText(cell)).Replace("|", "\\|");
        int span = cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
        return Enumerable.Repeat(text, Math.Max(1, span));
    }

    /// <summary>Style id to heading level, from built-in heading styles and their outline levels.</summary>
    private static Dictionary<string, int> HeadingLevels(MainDocumentPart main)
    {
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
        {
            string? id = style.StyleId?.Value;
            if (id is null) continue;

            string name = style.StyleName?.Val?.Value ?? id;
            int? outline = style.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
            if (name.Equals("Title", StringComparison.OrdinalIgnoreCase))
                levels[id] = 1;
            else if (outline is >= 0 and <= 5)
                levels[id] = outline.Value + 1;
            else if (name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(name.AsSpan("heading ".Length), out int n) && n is >= 1 and <= 6)
                levels[id] = n;
        }

        // Documents without a styles part still use the built-in ids.
        for (int n = 1; n <= 6; n++)
            levels.TryAdd($"Heading{n}", n);
        levels.TryAdd("Title", 1);
        return levels;
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
