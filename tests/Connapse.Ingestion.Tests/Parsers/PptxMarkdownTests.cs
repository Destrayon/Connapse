using Connapse.Ingestion.Parsers;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using FluentAssertions;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class PptxMarkdownTests
{
    private static uint _ids = 2;

    /// <summary>A text shape; each inner array is one paragraph, each string one run.</summary>
    private static P.Shape TextShape(P.PlaceholderValues? placeholder, params string[][] paragraphs)
    {
        var appProperties = new P.ApplicationNonVisualDrawingProperties();
        if (placeholder is { } type)
            appProperties.Append(new P.PlaceholderShape { Type = type });

        var body = new P.TextBody(new A.BodyProperties(), new A.ListStyle());
        foreach (var runs in paragraphs)
            body.Append(new A.Paragraph(runs.Select(r => (OpenXmlElement)new A.Run(new A.Text(r))).ToArray()));

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = _ids++, Name = "s" },
                new P.NonVisualShapeDrawingProperties(),
                appProperties),
            new P.ShapeProperties(),
            body);
    }

    private static P.GraphicFrame TableFrame(params string[][] rows)
    {
        var table = new A.Table(new A.TableProperties(), new A.TableGrid());
        foreach (var row in rows)
        {
            var tableRow = new A.TableRow { Height = 370840 };
            foreach (var text in row)
                tableRow.Append(new A.TableCell(new A.TextBody(new A.BodyProperties(), new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text(text)))), new A.TableCellProperties()));
            table.Append(tableRow);
        }

        return new P.GraphicFrame(
            new P.NonVisualGraphicFrameProperties(
                new P.NonVisualDrawingProperties { Id = _ids++, Name = "t" },
                new P.NonVisualGraphicFrameDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.Transform(),
            new A.Graphic(new A.GraphicData(table) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));
    }

    private static async Task<(string Content, IReadOnlyList<string> Warnings)> ParseAsync(
        params (OpenXmlElement[] Shapes, string? Notes)[] slides)
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation(new P.SlideIdList());
            uint slideId = 256;

            foreach (var (shapes, notes) in slides)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                var tree = new P.ShapeTree(
                    new P.NonVisualGroupShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                        new P.NonVisualGroupShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.GroupShapeProperties(new A.TransformGroup()));
                tree.Append(shapes);
                slidePart.Slide = new P.Slide(new P.CommonSlideData(tree));

                if (notes is not null)
                {
                    var notesPart = slidePart.AddNewPart<NotesSlidePart>();
                    notesPart.NotesSlide = new P.NotesSlide(new P.CommonSlideData(new P.ShapeTree(
                        new P.NonVisualGroupShapeProperties(
                            new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                            new P.NonVisualGroupShapeDrawingProperties(),
                            new P.ApplicationNonVisualDrawingProperties()),
                        new P.GroupShapeProperties(new A.TransformGroup()),
                        TextShape(P.PlaceholderValues.SlideNumber, ["7"]),
                        TextShape(P.PlaceholderValues.Body, [notes]))));
                }

                presentationPart.Presentation.SlideIdList!.Append(
                    new P.SlideId { Id = slideId++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
            }
            presentationPart.Presentation.Save();
        }

        stream.Position = 0;
        var parsed = await new OfficeParser().ParseAsync(stream, "deck.pptx");
        return (parsed.Content, parsed.Warnings);
    }

    [Fact]
    public async Task Convert_RunsOfOneParagraph_StayOnOneLine()
    {
        var (content, _) = await ParseAsync(([TextShape(null, ["Revenue grew ", "12%", " in Q3."])], null));

        content.Should().Contain("Revenue grew 12% in Q3.");
    }

    [Fact]
    public async Task Convert_TitlePlaceholder_BecomesTheSlideHeading()
    {
        var (content, _) = await ParseAsync(
            ([TextShape(P.PlaceholderValues.Title, ["Quarterly results"]), TextShape(null, ["Revenue grew."])], null),
            ([TextShape(P.PlaceholderValues.CenteredTitle, ["Next steps"])], null));

        content.Should().Contain("## Slide 1: Quarterly results");
        content.Should().Contain("## Slide 2: Next steps");
        content.Split("Quarterly results").Length.Should().Be(2, "the title is the heading, not repeated as body text");
    }

    [Fact]
    public async Task Convert_SpeakerNotes_AreIncludedUnderTheirSlide()
    {
        var (content, _) = await ParseAsync(
            ([TextShape(P.PlaceholderValues.Title, ["Pricing"])], "Explain that the discount ends in March."),
            ([TextShape(P.PlaceholderValues.Title, ["Roadmap"])], null));

        content.Should().Contain("Speaker notes: Explain that the discount ends in March.");
        content.IndexOf("ends in March", StringComparison.Ordinal).Should()
            .BeLessThan(content.IndexOf("## Slide 2", StringComparison.Ordinal));
        content.Should().NotContain("Speaker notes: 7", "the notes page's slide number is not a note");
    }

    [Fact]
    public async Task Convert_Table_BecomesAMarkdownTable()
    {
        var (content, _) = await ParseAsync(([TableFrame(["Region", "Q1"], ["North", "12"])], null));

        content.Should().Contain("| Region | Q1 |\n| --- | --- |\n| North | 12 |");
    }

    [Fact]
    public async Task Convert_GroupedShapes_AreRead()
    {
        var group = new P.GroupShape(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = _ids++, Name = "g" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(),
            TextShape(null, ["Inside a group."]));

        var (content, _) = await ParseAsync(([group], null));

        content.Should().Contain("Inside a group.");
    }

    [Fact]
    public async Task Convert_SlideNumberPlaceholder_IsSkipped()
    {
        var (content, _) = await ParseAsync(([TextShape(null, ["Body."]), TextShape(P.PlaceholderValues.SlideNumber, ["42"])], null));

        content.Should().NotContain("42");
    }

    [Fact]
    public async Task Convert_ShapeWrappedInAlternateContent_IsRead()
    {
        var alternate = new AlternateContent(
            new AlternateContentChoice(TextShape(null, ["Wrapped for newer readers."])) { Requires = "p14" },
            new AlternateContentFallback(TextShape(null, ["Wrapped for newer readers."])));

        var (content, _) = await ParseAsync(([alternate], null));

        content.Split("Wrapped for newer readers.").Length.Should().Be(2, "one branch is read, never both");
    }

    [Fact]
    public async Task Convert_SlideTextThatLooksLikeMarkdown_IsEscaped()
    {
        var (content, _) = await ParseAsync(([TextShape(null, ["# of customers grew"], ["| not a table"])], null));

        content.Should().Contain("\\# of customers grew");
        content.Should().Contain("\\| not a table");
    }

    [Fact]
    public async Task Convert_MergedTableCell_IsRepeatedUnderEachColumnItCovers()
    {
        var frame = TableFrame(["Region", "Q1", "Q2"], ["North", "Closed", ""]);
        var cells = frame.Descendants<A.TableRow>().Last().Elements<A.TableCell>().ToList();
        cells[1].GridSpan = 2;
        cells[2].HorizontalMerge = true;

        var (content, _) = await ParseAsync(([frame], null));

        content.Should().Contain("| North | Closed | Closed |");
    }

    [Fact]
    public async Task Convert_TableWithAnEmptyFirstRow_KeepsItAsTheHeader()
    {
        var (content, _) = await ParseAsync(([TableFrame(["", ""], ["North", "12"])], null));

        content.Should().Contain("|  |  |\n| --- | --- |\n| North | 12 |");
    }

    [Fact]
    public async Task Convert_ChartThatCannotBeRead_IsReportedNotSilentlySkipped()
    {
        var chartFrame = new P.GraphicFrame(
            new P.NonVisualGraphicFrameProperties(
                new P.NonVisualDrawingProperties { Id = _ids++, Name = "c" },
                new P.NonVisualGraphicFrameDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.Transform(),
            new A.Graphic(new A.GraphicData(new DocumentFormat.OpenXml.Drawing.Charts.ChartReference { Id = "rIdMissing" })
            {
                Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart",
            }));

        var (content, warnings) = await ParseAsync(([TextShape(null, ["Revenue by region."]), chartFrame], null));

        content.Should().Contain("Revenue by region.");
        warnings.Should().Contain("Slide 1 has a chart whose text could not be read");
    }

    [Fact]
    public async Task Convert_DeckWithNoText_IsReportedAsEmpty()
    {
        var (content, warnings) = await ParseAsync(([], null), ([], null));

        content.Should().BeEmpty("slide headings alone are not content");
        warnings.Should().Contain("Presentation contains no extractable text");
    }
}
