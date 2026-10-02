using Connapse.Ingestion.Parsers;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class DocxMarkdownTests
{
    private static Paragraph Para(string text, string? style = null)
    {
        var paragraph = new Paragraph(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        if (style is not null)
            paragraph.PrependChild(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        return paragraph;
    }

    private static TableCell Cell(string text, int span = 1)
    {
        var cell = new TableCell(Para(text));
        if (span > 1)
            cell.PrependChild(new TableCellProperties(new GridSpan { Val = span }));
        return cell;
    }

    /// <summary>A document whose body is the given elements, with optional header, footer and footnote.</summary>
    private static async Task<string> ParseAsync(Action<MainDocumentPart>? extra, params OpenXmlElement[] body)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(body));
            extra?.Invoke(main);
            main.Document.Save();
        }
        stream.Position = 0;
        var parsed = await new OfficeParser().ParseAsync(stream, "doc.docx");
        return parsed.Content;
    }

    [Fact]
    public async Task Convert_Table_IsWrittenOnceAsMarkdown()
    {
        var table = new Table(
            new TableRow(Cell("Region"), Cell("Q1")),
            new TableRow(Cell("North"), Cell("12")));

        string content = await ParseAsync(null, Para("Before."), table, Para("After."));

        content.Should().Contain("| Region | Q1 |\n| --- | --- |\n| North | 12 |");
        content.Split("North").Length.Should().Be(2, "the table used to be emitted a second time as pipe-joined rows");
        content.IndexOf("Before.", StringComparison.Ordinal).Should().BeLessThan(content.IndexOf("| Region", StringComparison.Ordinal));
        content.IndexOf("| North", StringComparison.Ordinal).Should().BeLessThan(content.IndexOf("After.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Convert_MergedCell_IsRepeatedSoColumnsStayAligned()
    {
        var table = new Table(
            new TableRow(Cell("Region"), Cell("Q1"), Cell("Q2")),
            new TableRow(Cell("North"), Cell("Closed all year", span: 2)));

        string content = await ParseAsync(null, table);

        content.Should().Contain("| North | Closed all year | Closed all year |");
    }

    [Fact]
    public async Task Convert_HeadingStyles_BecomeMarkdownHeadings()
    {
        string content = await ParseAsync(null, Para("Annual Report", "Title"), Para("Revenue", "Heading2"), Para("It grew."));

        content.Should().Contain("# Annual Report");
        content.Should().Contain("## Revenue");
        content.Should().Contain("It grew.");
    }

    [Fact]
    public async Task Convert_ParagraphThatLooksLikeMarkdown_IsEscapedNotMadeAHeading()
    {
        string content = await ParseAsync(null, Para("# of claims rose"), Para("---"));

        content.Should().Contain("\\# of claims rose");
        content.Should().Contain("\\---");
    }

    [Fact]
    public async Task Convert_ListParagraph_BecomesABullet()
    {
        var item = Para("First item");
        item.PrependChild(new ParagraphProperties(new NumberingProperties(
            new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })));

        string content = await ParseAsync(null, item);

        content.Should().Contain("- First item");
    }

    [Fact]
    public async Task Convert_TrackedChanges_KeepInsertionsAndDropDeletions()
    {
        var paragraph = new Paragraph(
            new Run(new Text("The fee is ") { Space = SpaceProcessingModeValues.Preserve }),
            new DeletedRun(new Run(new DeletedText("ten"))) { Id = "1", Author = "a" },
            new InsertedRun(new Run(new Text("twelve"))) { Id = "2", Author = "a" },
            new Run(new Text(" dollars.") { Space = SpaceProcessingModeValues.Preserve }));

        string content = await ParseAsync(null, paragraph);

        content.Should().Contain("The fee is twelve dollars.");
        content.Should().NotContain("ten");
    }

    [Fact]
    public async Task Convert_ReferencedHeaderFooterAndFootnote_AreIncludedAndUnusedOnesAreNot()
    {
        void Extras(MainDocumentPart main)
        {
            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(Para("Confidential draft"));
            var footer = main.AddNewPart<FooterPart>();
            footer.Footer = new Footer(Para("Contoso Ltd"));
            var stale = main.AddNewPart<FooterPart>();
            stale.Footer = new Footer(Para("Old footer no page uses"));
            var footnotes = main.AddNewPart<FootnotesPart>();
            footnotes.Footnotes = new Footnotes(
                new Footnote(Para("")) { Id = -1 },
                new Footnote(Para("")) { Id = 0 },
                new Footnote(Para("Source: the 2025 audit.")) { Id = 1 },
                new Footnote(Para("A note whose reference was deleted.")) { Id = 2 });

            var body = main.Document.Body!;
            body.Append(new SectionProperties(
                new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
                new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) }));
        }

        var cited = new Paragraph(
            new Run(new Text("Revenue grew.")),
            new Run(new FootnoteReference { Id = 1 }));

        string content = await ParseAsync(Extras, cited);

        content.Should().Contain("Confidential draft");
        content.Should().Contain("Contoso Ltd");
        content.Should().Contain("[1] Source: the 2025 audit.");
        content.Should().NotContain("Old footer no page uses");
        content.Should().NotContain("reference was deleted");
    }

    [Fact]
    public async Task Convert_ParagraphsInsideACustomXmlWrapper_AreRead()
    {
        var wrapper = new CustomXmlBlock(Para("Clause 4.2 limits liability.")) { Element = "clause" };

        string content = await ParseAsync(null, Para("Intro."), wrapper);

        content.Should().Contain("Clause 4.2 limits liability.");
    }

    [Fact]
    public async Task Convert_RowStartingAtALaterGridColumn_KeepsValuesUnderTheirHeaders()
    {
        var offset = new TableRow(
            new TableRowProperties(new GridBefore { Val = 1 }),
            Cell("12"), Cell("15"));
        var table = new Table(
            new TableRow(Cell("Region"), Cell("Q1"), Cell("Q2")),
            offset);

        string content = await ParseAsync(null, table);

        content.Should().Contain("|  | 12 | 15 |");
    }

    [Fact]
    public async Task Convert_EmptyFirstRow_IsNotReplacedByADataRowAsTheHeader()
    {
        var table = new Table(
            new TableRow(Cell(""), Cell("")),
            new TableRow(Cell("North"), Cell("12")));

        string content = await ParseAsync(null, table);

        content.Should().Contain("|  |  |\n| --- | --- |\n| North | 12 |");
    }

    [Fact]
    public async Task Convert_TextOnlyInTheFallback_IsKept()
    {
        var alternate = new AlternateContent(
            new AlternateContentChoice(new Drawing()) { Requires = "wps" },
            new AlternateContentFallback(new Picture(new DocumentFormat.OpenXml.Vml.Shape(
                new DocumentFormat.OpenXml.Vml.TextBox(new TextBoxContent(Para("Only the legacy copy has this.")))))));

        string content = await ParseAsync(null, new Paragraph(new Run(alternate)));

        content.Should().Contain("Only the legacy copy has this.");
    }

    [Fact]
    public async Task Convert_TextBoxWithVmlFallback_IsReadOnce()
    {
        // Word writes a text box twice: DrawingML under mc:Choice, VML under mc:Fallback.
        static TextBoxContent BoxContent() => new(Para("Callout: shipping resumes Monday."));
        var drawing = new Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor(
            new DocumentFormat.OpenXml.Drawing.Graphic(
                new DocumentFormat.OpenXml.Drawing.GraphicData(
                    new DocumentFormat.OpenXml.Office2010.Word.DrawingShape.WordprocessingShape(
                        new DocumentFormat.OpenXml.Office2010.Word.DrawingShape.TextBoxInfo2(BoxContent())))
                {
                    Uri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape",
                })));
        var vml = new Picture(new DocumentFormat.OpenXml.Vml.Shape(new DocumentFormat.OpenXml.Vml.TextBox(BoxContent())));
        var alternate = new AlternateContent(
            new AlternateContentChoice(drawing) { Requires = "wps" },
            new AlternateContentFallback(vml));

        string content = await ParseAsync(null, Para("Intro."), new Paragraph(new Run(alternate)));

        content.Split("shipping resumes Monday").Length.Should().Be(2, "the VML copy is the same text box");
    }
}
