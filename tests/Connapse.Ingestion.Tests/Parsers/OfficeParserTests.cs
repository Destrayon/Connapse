using System.Text;
using Connapse.Ingestion.Parsers;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class OfficeParserTests
{
    private readonly OfficeParser _parser = new();

    [Fact]
    public void SupportedExtensions_ContainsExpectedTypes()
    {
        _parser.SupportedExtensions.Should().Contain(".docx");
        _parser.SupportedExtensions.Should().Contain(".pptx");
        _parser.SupportedExtensions.Should().HaveCount(2);
    }

    [Fact]
    public async Task ParseAsync_DocxFile_ExtractsText()
    {
        using var stream = CreateWordDocument("Hello, World!", "This is a test document.");

        var result = await _parser.ParseAsync(stream, "test.docx");

        result.Content.Should().Contain("Hello, World!");
        result.Content.Should().Contain("This is a test document.");
        result.Metadata["FileType"].Should().Be("Word Document");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_DocxWithMetadata_ExtractsMetadata()
    {
        using var stream = CreateWordDocumentWithMetadata(
            title: "Test Document",
            author: "Test Author",
            subject: "Test Subject",
            content: "Document content here");

        var result = await _parser.ParseAsync(stream, "test.docx");

        result.Metadata["Title"].Should().Be("Test Document");
        result.Metadata["Author"].Should().Be("Test Author");
        result.Metadata["Subject"].Should().Be("Test Subject");
    }

    [Fact]
    public async Task ParseAsync_DocxWithMultipleParagraphs_PreservesStructure()
    {
        using var stream = CreateWordDocument(
            "Paragraph 1",
            "Paragraph 2",
            "Paragraph 3");

        var result = await _parser.ParseAsync(stream, "test.docx");

        var lines = result.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain(l => l.Contains("Paragraph 1"));
        lines.Should().Contain(l => l.Contains("Paragraph 2"));
        lines.Should().Contain(l => l.Contains("Paragraph 3"));
    }

    [Fact]
    public async Task ParseAsync_PptxFile_ExtractsSlideContent()
    {
        using var stream = CreatePowerPointDocument(
            new[] { "Slide 1 Title", "Slide 1 Content" },
            new[] { "Slide 2 Title", "Slide 2 Content" });

        var result = await _parser.ParseAsync(stream, "test.pptx");

        result.Content.Should().Contain("Slide 1 Title");
        result.Content.Should().Contain("Slide 1 Content");
        result.Content.Should().Contain("Slide 2 Title");
        result.Content.Should().Contain("Slide 2 Content");
        result.Metadata["FileType"].Should().Be("PowerPoint Presentation");
        result.Metadata["SlideCount"].Should().Be("2");
    }

    [Fact]
    public async Task ParseAsync_PptxFile_IncludesSlideMarkers()
    {
        using var stream = CreatePowerPointDocument(
            new[] { "Slide 1 Title" },
            new[] { "Slide 2 Title" });

        var result = await _parser.ParseAsync(stream, "test.pptx");

        result.Content.Should().Contain("--- Slide 1 ---");
        result.Content.Should().Contain("--- Slide 2 ---");
    }

    [Fact]
    public async Task ParseAsync_UnsupportedExtension_ThrowsException()
    {
        using var stream = new MemoryStream();

        var act = async () => await _parser.ParseAsync(stream, "test.xlsx");

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*not supported*");
    }

    [Fact]
    public async Task ParseAsync_InvalidDocxFile_ReturnsWarning()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Invalid content"));

        var result = await _parser.ParseAsync(stream, "invalid.docx");

        result.Content.Should().BeEmpty();
        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("Error parsing Office document");
    }

    [Fact]
    public async Task ParseAsync_SupportsCancellation()
    {
        using var stream = CreateWordDocument("Some content");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _parser.ParseAsync(stream, "test.docx", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static MemoryStream CreateWordDocument(params string[] paragraphs)
    {
        var stream = new MemoryStream();

        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = new Body();

            foreach (var text in paragraphs)
            {
                var paragraph = new Paragraph(
                    new Run(
                        new Text(text)));
                body.Append(paragraph);
            }

            mainPart.Document.Append(body);
            mainPart.Document.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateWordDocumentWithMetadata(
        string title, string author, string subject, string content)
    {
        var stream = new MemoryStream();

        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            document.PackageProperties.Title = title;
            document.PackageProperties.Creator = author;
            document.PackageProperties.Subject = subject;
            document.PackageProperties.Created = DateTime.UtcNow;

            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(
                new Body(
                    new Paragraph(
                        new Run(
                            new Text(content)))));
            mainPart.Document.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreatePowerPointDocument(params string[][] slidesContent)
    {
        var stream = new MemoryStream();

        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();

            var slideIdList = new P.SlideIdList();
            presentationPart.Presentation.Append(slideIdList);

            uint slideId = 256;

            foreach (var slideTexts in slidesContent)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.Slide = CreateSlide(slideTexts);

                slideIdList.Append(new P.SlideId
                {
                    Id = slideId++,
                    RelationshipId = presentationPart.GetIdOfPart(slidePart)
                });
            }

            presentationPart.Presentation.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static P.Slide CreateSlide(string[] texts)
    {
        var slide = new P.Slide(
            new P.CommonSlideData(
                new P.ShapeTree()));

        var shapeTree = slide.CommonSlideData!.ShapeTree;

        // Add non-visual properties
        shapeTree!.Append(new P.NonVisualGroupShapeProperties(
            new P.NonVisualDrawingProperties { Id = 1, Name = "" },
            new P.NonVisualGroupShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()));

        shapeTree.Append(new P.GroupShapeProperties(new D.TransformGroup()));

        uint shapeId = 2;
        foreach (var text in texts)
        {
            var shape = new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = shapeId++, Name = $"Shape{shapeId}" },
                    new P.NonVisualShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(),
                new P.TextBody(
                    new D.BodyProperties(),
                    new D.ListStyle(),
                    new D.Paragraph(
                        new D.Run(
                            new D.Text(text)))));

            shapeTree.Append(shape);
        }

        return slide;
    }
}
