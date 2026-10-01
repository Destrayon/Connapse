using System.Text;
using Connapse.Ingestion.Validation;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Validation;

[Trait("Category", "Unit")]
public class ContentSnifferTests
{
    private static readonly byte[] Pdf = "%PDF-1.7\n%âãÏÓ\n1 0 obj"u8.ToArray();
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    [Fact]
    public void DescribeMismatch_RealPdf_IsConsistent() =>
        ContentSniffer.DescribeMismatch(Pdf, ".pdf").Should().BeNull();

    [Fact]
    public void DescribeMismatch_PdfHeaderAfterLeadingJunk_IsConsistent()
    {
        // The PDF spec tolerates bytes before the header, and real files carry them.
        byte[] head = [.. Encoding.ASCII.GetBytes("\r\n\r\njunk"), .. Pdf];

        ContentSniffer.DescribeMismatch(head, ".pdf").Should().BeNull();
    }

    [Fact]
    public void DescribeMismatch_PngNamedPdf_NamesTheRealFormat() =>
        ContentSniffer.DescribeMismatch(Png, ".pdf").Should().Be("its content is a PNG image, not a PDF");

    [Fact]
    public void DescribeMismatch_TextNamedPdf_SaysTheHeaderIsMissing() =>
        ContentSniffer.DescribeMismatch("just some words"u8, ".pdf").Should().Be("its content has no PDF header");

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".docx")]
    [InlineData(".pptx")]
    public void DescribeMismatch_EmptyBinaryFile_SaysItIsEmpty(string extension) =>
        ContentSniffer.DescribeMismatch([], extension).Should().Be("the file is empty");

    [Theory]
    [InlineData(".docx")]
    [InlineData(".PPTX")]
    public void DescribeMismatch_ZipNamedOffice_IsConsistent(string extension) =>
        ContentSniffer.DescribeMismatch(Zip, extension).Should().BeNull();

    [Fact]
    public void DescribeMismatch_PdfNamedDocx_NamesTheRealFormat() =>
        ContentSniffer.DescribeMismatch(Pdf, ".docx").Should().Be("its content is a PDF document, not a DOCX file");

    [Fact]
    public void DescribeMismatch_LegacyWordNamedDocx_NamesTheRealFormat() =>
        ContentSniffer.DescribeMismatch(Ole, ".docx")
            .Should().Be("its content is a legacy Office or Outlook file, not a DOCX file");

    [Fact]
    public void DescribeMismatch_PlainTextNamedDocx_IsRejected() =>
        ContentSniffer.DescribeMismatch("Plain text saved with a .docx extension."u8, ".docx")
            .Should().Be("its content is not a DOCX file");

    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".json")]
    public void DescribeMismatch_ImageNamedAsText_IsRejected(string extension) =>
        ContentSniffer.DescribeMismatch(Jpeg, extension).Should().Be("its content is a JPEG image, not text");

    [Theory]
    [InlineData("GIF89a is the header every GIF starts with.\n")]
    [InlineData("%PDF-1.7 is the first line of a PDF; this note explains it.\n")]
    [InlineData("PK\u0003\u0004 aside, this is prose.")]
    public void DescribeMismatch_TextOpeningWithASignature_IsConsistent(string text)
    {
        // A few printable signature bytes do not make a file binary. Only the ZIP case contains
        // control bytes, so it alone is still rejected.
        byte[] head = Encoding.UTF8.GetBytes(text);
        string? mismatch = ContentSniffer.DescribeMismatch(head, ".md");

        if (text.StartsWith("PK"))
            mismatch.Should().Be("its content is a ZIP archive, not text");
        else
            mismatch.Should().BeNull();
    }

    [Fact]
    public void DescribeMismatch_Utf16TextWithoutBom_IsConsistent()
    {
        // Every other byte is zero; that is still text, and the encoding detector's job.
        byte[] head = Encoding.Unicode.GetBytes("Plain UTF-16 text");

        ContentSniffer.DescribeMismatch(head, ".txt").Should().BeNull();
    }

    [Fact]
    public void DescribeMismatch_Utf16TextWithBom_IsConsistent()
    {
        byte[] head = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("text")];

        ContentSniffer.DescribeMismatch(head, ".txt").Should().BeNull();
    }

    [Fact]
    public void DescribeMismatch_EmptyTextFile_IsConsistent() =>
        ContentSniffer.DescribeMismatch([], ".txt").Should().BeNull();

    [Fact]
    public void DescribeMismatch_ExtensionWithNoRule_IsConsistent() =>
        ContentSniffer.DescribeMismatch(Png, ".unknown").Should().BeNull();

    [Fact]
    public void Identify_WebP_IsRecognised() =>
        ContentSniffer.Identify("RIFF\0\0\0\0WEBPVP8 "u8).Should().Be("a WebP image");
}
