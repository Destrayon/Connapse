using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

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
    public static string Convert(PresentationPart presentation, CancellationToken ct)
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

            string? title = null;
            var body = new StringBuilder();
            AppendShapes(body, tree.ChildElements, ref title);

            string notes = NotesText(slidePart);
            anyText |= title is not null || body.Length > 0 || notes.Length > 0;

            output.Append("## Slide ").Append(number);
            if (title is not null) output.Append(": ").Append(title);
            output.Append("\n\n");

            if (body.Length > 0)
                output.Append(body.ToString().Trim()).Append("\n\n");

            if (notes.Length > 0)
                output.Append("Speaker notes: ").Append(notes).Append("\n\n");
        }

        // Slide headings alone are not content: a deck of pictures has nothing to index.
        return anyText ? output.ToString().Trim() : string.Empty;
    }

    private static void AppendShapes(StringBuilder output, IEnumerable<OpenXmlElement> shapes, ref string? title)
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

                    if (title is null && (placeholder == PlaceholderValues.Title || placeholder == PlaceholderValues.CenteredTitle))
                    {
                        title = string.Join(' ', paragraphs);
                        break;
                    }

                    foreach (string paragraph in paragraphs)
                        output.Append(paragraph).Append('\n');
                    output.Append('\n');
                    break;

                case GraphicFrame frame when frame.Descendants<A.Table>().FirstOrDefault() is { } table:
                    string markdown = TableMarkdown(table);
                    if (markdown.Length > 0)
                        output.Append(markdown).Append("\n\n");
                    break;

                case GroupShape group:
                    AppendShapes(output, group.ChildElements, ref title);
                    break;
            }
        }
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

    private static string TableMarkdown(A.Table table)
    {
        var rows = table.Elements<A.TableRow>()
            // A merged cell's continuations are empty cells; they stay, so the columns line up.
            .Select(row => row.Elements<A.TableCell>()
                .Select(cell => Collapse(string.Join(' ', cell.TextBody is null ? [] : Paragraphs(cell.TextBody))).Replace("|", "\\|"))
                .ToList())
            .Where(cells => cells.Any(c => c.Length > 0))
            .ToList();
        if (rows.Count == 0)
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
