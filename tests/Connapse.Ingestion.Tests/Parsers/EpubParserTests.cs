using System.IO.Compression;
using System.Text;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Parsers;
using Connapse.Ingestion.Validation;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class EpubParserTests
{
    private readonly EpubParser _parser = new();

    /// <summary>A minimal EPUB 3: container, package, navigation document and the given chapters.</summary>
    internal static byte[] Epub(
        string title,
        string author,
        (string File, string Body)[] chapters,
        string[]? spineOnly = null,
        Action<ZipArchive>? extra = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path, level).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }

            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);

            var spineFiles = chapters.Select(c => c.File).Concat(spineOnly ?? []).ToList();
            var files = spineFiles.Distinct().ToList();
            string manifest = string.Concat(files.Select((f, i) => $"""<item id="c{i}" href="{Uri.EscapeDataString(f)}" media-type="application/xhtml+xml"/>"""));
            string spine = string.Concat(spineFiles.Select(f => $"""<itemref idref="c{files.IndexOf(f)}"/>"""));
            Add("OEBPS/content.opf", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:identifier id="id">urn:uuid:5f0c6a5e-1b1d-4c5e-9f00-000000000600</dc:identifier>
                    <dc:title>{title}</dc:title>
                    <dc:creator>{author}</dc:creator>
                    <dc:language>en</dc:language>
                    <meta property="dcterms:modified">2026-10-02T00:00:00Z</meta>
                  </metadata>
                  <manifest>
                    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                    {manifest}
                  </manifest>
                  <spine>{spine}</spine>
                </package>
                """);
            Add("OEBPS/nav.xhtml", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                <head><title>Contents</title></head>
                <body><nav epub:type="toc"><ol>{string.Concat(files.Select(f => $"""<li><a href="{Uri.EscapeDataString(f)}">{f}</a></li>"""))}</ol></nav></body>
                </html>
                """);

            foreach (var (file, body) in chapters)
            {
                Add($"OEBPS/{file}", $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <html xmlns="http://www.w3.org/1999/xhtml"><head><title>{file}</title></head><body>{body}</body></html>
                    """);
            }

            extra?.Invoke(zip);
        }

        return output.ToArray();
    }

    private static readonly (string, string)[] TwoChapters =
    [
        ("cover.xhtml", """<img src="cover.jpg" alt="Cover"/>"""),
        ("ch1.xhtml", "<h1>Chapter One</h1><p>The lighthouse stood on the northern rocks.</p>"),
        ("ch2.xhtml", "<h1>Chapter Two</h1><h2>The storm</h2><p>Waves broke over the breakwater at midnight.</p>"),
    ];

    private async Task<ParsedDocument> ParseAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return await _parser.ParseAsync(stream, "book.epub");
    }

    private static Action<ZipArchive> Encryption(string algorithm, string uri) => zip =>
    {
        using var writer = new StreamWriter(zip.CreateEntry("META-INF/encryption.xml").Open());
        writer.Write($"""
            <?xml version="1.0"?>
            <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
              <enc:EncryptedData>
                <enc:EncryptionMethod Algorithm="{algorithm}"/>
                <enc:CipherData><enc:CipherReference URI="{uri}"/></enc:CipherData>
              </enc:EncryptedData>
            </encryption>
            """);
    };

    [Fact]
    public async Task ParseAsync_Book_WritesTitleAuthorAndChaptersInReadingOrder()
    {
        var result = await ParseAsync(Epub("The Keeper's Log", "Ada Lovelace", TwoChapters));

        result.Content.Should().StartWith("# The Keeper's Log");
        result.Content.Should().Contain("**Author:** Ada Lovelace");
        result.Content.IndexOf("The lighthouse stood").Should().BeLessThan(result.Content.IndexOf("Waves broke"));
        result.Metadata["Title"].Should().Be("The Keeper's Log");
        result.Metadata["Author"].Should().Be("Ada Lovelace");
        result.Metadata["ChapterCount"].Should().Be("3");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_ChapterHeadings_SitOneLevelBelowTheBookTitle()
    {
        var result = await ParseAsync(Epub("The Keeper's Log", "Ada Lovelace", TwoChapters));

        var lines = result.Content.Split('\n');
        lines.Should().ContainSingle(l => l.StartsWith("# "), "the book title is the only top-level heading");
        lines.Should().Contain("## Chapter One").And.Contain("## Chapter Two").And.Contain("### The storm");
    }

    [Fact]
    public async Task ParseAsync_LeavesTheCallersStreamOpen()
    {
        // The pipeline reads the stream again after parsing; VersOne.Epub closes what it is given.
        using var stream = new MemoryStream(Epub("Book", "Author", TwoChapters));

        await _parser.ParseAsync(stream, "book.epub");

        stream.CanRead.Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_ImageOnlyCover_AddsNothing()
    {
        var result = await ParseAsync(Epub("Book", "Author", TwoChapters));

        result.Content.Should().NotContain("cover.jpg").And.NotContain("Cover");
    }

    [Fact]
    public async Task ParseAsync_DrmEncryptedChapters_FailAsEncrypted()
    {
        byte[] book = Epub("Locked", "Author", TwoChapters,
            extra: Encryption("http://www.w3.org/2001/04/xmlenc#aes128-cbc", "OEBPS/ch1.xhtml"));

        var act = () => ParseAsync(book);

        (await act.Should().ThrowAsync<PermanentIngestionException>()).Which.Message.Should().Contain("[encrypted]");
    }

    [Theory]
    [InlineData("OEBPS/chapter%20one.xhtml", "chapter one.xhtml")]
    [InlineData("OEBPS/ch1.dat", "ch1.dat")]
    [InlineData("OEBPS/text/../ch1.xhtml", "ch1.xhtml")]
    public async Task ParseAsync_EncryptedChapterUnderAnySpelling_FailsAsEncrypted(string uri, string chapterFile)
    {
        // Matched against the chapters read, not by extension: a chapter need not end in .xhtml,
        // and encryption.xml may percent-encode or dot-segment its path.
        (string, string)[] chapters = [(chapterFile, "<p>Ciphertext in real life.</p>")];
        byte[] book = Epub("Locked", "Author", chapters, extra: Encryption("http://www.w3.org/2001/04/xmlenc#aes128-cbc", uri));

        var act = () => ParseAsync(book);

        await act.Should().ThrowAsync<PermanentIngestionException>().WithMessage("*[encrypted]*");
    }

    [Fact]
    public async Task ParseAsync_EncryptedResourceOutsideTheReadingOrder_IsNotDrm()
    {
        byte[] book = Epub("Partly locked", "Author", TwoChapters,
            extra: Encryption("http://www.w3.org/2001/04/xmlenc#aes128-cbc", "OEBPS/extras/answers.xhtml"));

        var result = await ParseAsync(book);

        result.Content.Should().Contain("The lighthouse stood");
    }

    [Fact]
    public async Task ParseAsync_ChapterRepeatedInTheSpine_IsReadOnce()
    {
        byte[] book = Epub("Loop", "Author", TwoChapters, spineOnly: Enumerable.Repeat("ch1.xhtml", 5000).ToArray());

        var result = await ParseAsync(book);

        result.Content.Split("The lighthouse stood").Should().HaveCount(2, "one occurrence splits the text in two");
    }

    [Fact]
    public async Task ParseAsync_TextOverTheExtractionLimit_StopsReading()
    {
        var monitor = NSubstitute.Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<Core.UploadSettings>>();
        monitor.CurrentValue.Returns(new Core.UploadSettings { MaxExtractedCharacters = 50 });
        var parser = new EpubParser(monitor);
        using var stream = new MemoryStream(Epub("Long", "Author", TwoChapters));

        var act = () => parser.ParseAsync(stream, "book.epub");

        await act.Should().ThrowAsync<PermanentIngestionException>().WithMessage("*[output_too_large]*");
    }

    [Fact]
    public async Task ParseAsync_ObfuscatedFontsOnly_AreNotDrm()
    {
        byte[] book = Epub("Fonts", "Author", TwoChapters,
            extra: Encryption("http://www.idpf.org/2008/embedding", "OEBPS/fonts/serif.otf"));

        var result = await ParseAsync(book);

        result.Content.Should().Contain("The lighthouse stood");
    }

    [Fact]
    public async Task ParseAsync_ChapterMissingFromThePackage_IsCountedAndTheRestKept()
    {
        byte[] book = Epub("Gap", "Author", TwoChapters, spineOnly: ["missing.xhtml"]);

        var result = await ParseAsync(book);

        result.Content.Should().Contain("The lighthouse stood").And.Contain("Waves broke");
        result.Metadata.Should().ContainKey(PdfParser.MetadataKeyPageErrors).WhoseValue.Should().Be("1");
        result.Warnings.Should().Contain(w => w.Contains("missing.xhtml"));
    }

    [Fact]
    public async Task ParseAsync_BookWithNoChapterText_YieldsNoContent()
    {
        var result = await ParseAsync(Epub("Picture book", "Author", [("p1.xhtml", """<img src="p1.jpg"/>""")]));

        result.Content.Should().BeEmpty();
        result.Warnings.Should().Contain("Document contains no readable text content");
    }

    [Fact]
    public async Task ParseAsync_NotAZip_YieldsNoContentWithAWarning()
    {
        var result = await ParseAsync(Encoding.UTF8.GetBytes("not a book"));

        result.Content.Should().BeEmpty();
        result.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public void ContentSniffer_PngNamedEpub_NamesTheRealFormat()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

        ContentSniffer.DescribeMismatch(png, ".epub").Should().Be("its content is a PNG image, not an EPUB file");
    }
}
