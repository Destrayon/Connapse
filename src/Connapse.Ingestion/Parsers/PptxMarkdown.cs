using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Dgm = DocumentFormat.OpenXml.Drawing.Diagrams;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Writes a PowerPoint deck as Markdown, one section per slide.
/// <para>
/// The previous reader put every text run on its own line, so "Revenue grew <b>12%</b> in Q3"
/// became three lines; it dropped speaker notes, where the explanation of a slide usually
/// lives; and it read a table as a column of loose cells.
/// </para>
/// </summary>
internal static class PptxMarkdown
{
    public static string Convert(PresentationPart presentation, List<string> warnings, CancellationToken ct)
    {
        var slideIds = presentation.Presentation?.SlideIdList?.Elements<SlideId>().ToList() ?? [];
        var output = new StringBuilder();
        int number = 0;
        bool anyText = false;

        foreach (var slideId in slideIds)
        {
            ct.ThrowIfCancellationRequested();
            number++;

            if (slideId.RelationshipId?.Value is not { } relationshipId ||
                presentation.GetPartById(relationshipId) is not SlidePart slidePart ||
                slidePart.Slide?.CommonSlideData?.ShapeTree is not { } tree)
            {
                continue;
            }

            var slide = new SlideContext(slidePart, number, warnings);
            var body = new StringBuilder();
            AppendShapes(body, tree.ChildElements, slide);

            string notes = NotesText(slidePart);
            anyText |= slide.Title is not null || body.Length > 0 || notes.Length > 0;

            output.Append("## Slide ").Append(number);
            if (slide.Title is not null) output.Append(": ").Append(slide.Title);
            output.Append("\n\n");

            if (body.Length > 0)
                output.Append(body.ToString().Trim()).Append("\n\n");

            if (notes.Length > 0)
                output.Append("Speaker notes: ").Append(notes).Append("\n\n");
        }

        // Slide headings alone are not content: a deck of pictures has nothing to index.
        return anyText ? output.ToString().Trim() : string.Empty;
    }

    private sealed class SlideContext(SlidePart part, int number, List<string> warnings)
    {
        public SlidePart Part { get; } = part;
        public int Number { get; } = number;
        public List<string> Warnings { get; } = warnings;
        public string? Title { get; set; }
    }

    private static void AppendShapes(StringBuilder output, IEnumerable<OpenXmlElement> shapes, SlideContext slide)
    {
        foreach (var element in shapes)
        {
            switch (element)
            {
                case Shape shape when shape.TextBody is { } textBody:
                    var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?
                        .GetFirstChild<PlaceholderShape>()?.Type?.Value;
                    if (placeholder == PlaceholderValues.SlideNumber || placeholder == PlaceholderValues.DateAndTime)
                        break;

                    var paragraphs = Paragraphs(textBody).ToList();
                    if (paragraphs.Count == 0)
                        break;

                    if (slide.Title is null && (placeholder == PlaceholderValues.Title || placeholder == PlaceholderValues.CenteredTitle))
                    {
                        slide.Title = string.Join(' ', paragraphs);
                        break;
                    }

                    foreach (string paragraph in paragraphs)
                        output.Append(MarkdownText.EscapeLine(paragraph)).Append('\n');
                    output.Append('\n');
                    break;

                case GraphicFrame frame:
                    AppendGraphicFrame(output, frame, slide);
                    break;

                case GroupShape group:
                    AppendShapes(output, group.ChildElements, slide);
                    break;

                case AlternateContent alternate:
                    // One branch, never both: the Choice, unless it yields no text, in which case
                    // the Fallback is the only copy there is.
                    var chosen = new StringBuilder();
                    foreach (var choice in alternate.Elements<AlternateContentChoice>().Take(1))
                        AppendShapes(chosen, choice.ChildElements, slide);
                    if (chosen.Length == 0)
                        foreach (var fallback in alternate.Elements<AlternateContentFallback>())
                            AppendShapes(chosen, fallback.ChildElements, slide);
                    output.Append(chosen);
                    break;
            }
        }
    }

    /// <summary>
    /// Tables become Markdown tables. A chart contributes its title, series and category names; a
    /// SmartArt diagram its node text. Anything else that may carry text is reported, so a gap is
    /// visible rather than silent.
    /// </summary>
    private static void AppendGraphicFrame(StringBuilder output, GraphicFrame frame, SlideContext slide)
    {
        if (frame.Descendants<A.Table>().FirstOrDefault() is { } table)
        {
            string markdown = TableMarkdown(table);
            if (markdown.Length > 0)
                output.Append(markdown).Append("\n\n");
            return;
        }

        var texts = new List<string>();
        string kind = "graphic";

        if (frame.Descendants<C.ChartReference>().FirstOrDefault()?.Id?.Value is { } chartId)
        {
            kind = "chart";
            if (TryPart(slide.Part, chartId) is ChartPart chart && chart.ChartSpace is { } space)
            {
                texts.AddRange(space.Descendants<A.Text>().Select(t => t.Text));
                texts.AddRange(space.Descendants<C.StringPoint>().Select(p => p.NumericValue?.Text ?? string.Empty));
            }
        }
        else if (frame.Descendants<Dgm.RelationshipIds>().FirstOrDefault()?.DataPart?.Value is { } dataId)
        {
            kind = "diagram";
            if (TryPart(slide.Part, dataId) is DiagramDataPart data && data.DataModelRoot is { } model)
                texts.AddRange(model.Descendants<A.Text>().Select(t => t.Text));
        }
        else
        {
            return; // pictures, video and OLE frames carry no text to read
        }

        var lines = texts.Select(Collapse).Where(t => t.Length > 0).Distinct().ToList();
        if (lines.Count == 0)
        {
            slide.Warnings.Add($"Slide {slide.Number} has a {kind} whose text could not be read");
            return;
        }

        foreach (string line in lines)
            output.Append(MarkdownText.EscapeLine(line)).Append('\n');
        output.Append('\n');
    }

    private static OpenXmlPart? TryPart(SlidePart slide, string id)
    {
        try { return slide.GetPartById(id); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Each paragraph's runs, fields and line breaks joined into one line.</summary>
    private static IEnumerable<string> Paragraphs(OpenXmlElement textBody)
    {
        foreach (var paragraph in textBody.Elements<A.Paragraph>())
        {
            var builder = new StringBuilder();
            foreach (var child in paragraph.ChildElements)
            {
                switch (child)
                {
                    case A.Run run: builder.Append(run.Text?.Text); break;
                    case A.Field field: builder.Append(field.Text?.Text); break;
                    case A.Break: builder.Append(' '); break;
                }
            }

            string text = Collapse(builder.ToString());
            if (text.Length > 0)
                yield return text;
        }
    }

    /// <summary>
    /// The notes page's body placeholder: what the presenter says about the slide. The notes
    /// page also carries a copy of the slide image and a slide number, which are not notes.
    /// </summary>
    private static string NotesText(SlidePart slidePart)
    {
        var tree = slidePart.NotesSlidePart?.NotesSlide?.CommonSlideData?.ShapeTree;
        if (tree is null)
            return string.Empty;

        var notes = tree.Descendants<Shape>()
            .Where(s => s.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?
                .GetFirstChild<PlaceholderShape>()?.Type?.Value == PlaceholderValues.Body)
            .Where(s => s.TextBody is not null)
            .SelectMany(s => Paragraphs(s.TextBody!));

        return string.Join(' ', notes);
    }

    /// <summary>
    /// A merged cell's text is repeated across the columns and rows it covers -- its continuation
    /// cells (hMerge, vMerge) are empty in the file -- so each value sits under its own header.
    /// Empty rows are kept, or a later data row would become the header Markdown takes from the
    /// first row.
    /// </summary>
    private static string TableMarkdown(A.Table table)
    {
        var rows = new List<List<string>>();
        foreach (var row in table.Elements<A.TableRow>())
        {
            var cells = new List<string>();
            int column = 0;
            foreach (var cell in row.Elements<A.TableCell>())
            {
                string text = Collapse(string.Join(' ', cell.TextBody is null ? [] : Paragraphs(cell.TextBody))).Replace("|", "\\|");
                if (cell.HorizontalMerge?.Value == true && cells.Count > 0)
                    text = cells[^1];
                else if (cell.VerticalMerge?.Value == true && rows.Count > 0 && column < rows[^1].Count)
                    text = rows[^1][column];
                cells.Add(text);
                column++;
            }
            rows.Add(cells);
        }

        if (rows.Count == 0 || rows.All(r => r.All(c => c.Length == 0)))
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

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
