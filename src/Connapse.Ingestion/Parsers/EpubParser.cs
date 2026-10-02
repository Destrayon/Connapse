using System.IO.Compression;
using System.Text;
using System.Xml;
using Connapse.Core.Interfaces;
using VersOne.Epub;
using VersOne.Epub.Options;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Parser for EPUB books (.epub), written out as Markdown (#600).
/// <para>
/// The book's chapters are read in reading order, one at a time, and each goes through the HTML
/// path whole -- a chapter is all content, so Readability has nothing to remove and could only
/// drop a short one. The book title is the single top-level heading and each chapter's own
/// headings sit one level below it, so the DocumentAware chunker's breadcrumb reads book, chapter,
/// section.
/// </para>
/// <para>
/// Nothing is downloaded: VersOne.Epub can fetch remote resources a book links to, and that is
/// switched off. A book whose chapters are DRM-encrypted fails as <c>[encrypted]</c> rather than
/// being indexed as ciphertext; font obfuscation, which most commercial EPUBs carry and which
/// leaves the text readable, does not count. A chapter that cannot be read is skipped and counted
/// as a page error, so the document is marked incomplete and a reindex retries it.
/// </para>
/// </summary>
public class EpubParser : IDocumentParser
{
    /// <summary>The two font-obfuscation algorithms, which encrypt fonts only.</summary>
    private static readonly HashSet<string> FontObfuscation = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://www.idpf.org/2008/embedding",
        "http://ns.adobe.com/pdf/enc#RC",
    };

    private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub",
    };

    public IReadOnlySet<string> SupportedExtensions => _supportedExtensions;

    public int Version => 1;

    public async Task<ParsedDocument> ParseAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var metadata = new Dictionary<string, string> { ["FileType"] = "EPUB" };

        try
        {
            long start = stream.Position;
            if (EncryptedContent(stream) is { } encrypted)
                throw new PermanentIngestionException($"the EPUB is DRM-protected: {encrypted} is encrypted [encrypted]");
            stream.Position = start;

            var options = new EpubReaderOptions(EpubReaderOptionsPreset.RELAXED);
            options.ContentDownloaderOptions.DownloadContent = false;

            // VersOne.Epub closes the stream it is given when the book is disposed, and the
            // pipeline still needs its own afterwards: the book gets a view of the same bytes.
            using var book = EpubReader.OpenBook(await ViewOfAsync(stream, cancellationToken), options);
            string title = Normalize(book.Title);
            string author = Normalize(string.Join(", ", book.AuthorList ?? []));
            if (title.Length > 0)
                metadata["Title"] = title;
            if (author.Length > 0)
                metadata["Author"] = author;

            var text = new StringBuilder();
            if (title.Length > 0)
                text.Append("# ").Append(MarkdownText.EscapeLine(title)).Append("\n\n");
            if (author.Length > 0)
                text.Append("**Author:** ").Append(author).Append("\n\n");

            var chapters = book.GetReadingOrder();
            metadata["ChapterCount"] = chapters.Count.ToString();
            int failed = 0;
            bool anyText = false;
            foreach (var chapter in chapters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string markdown = HtmlParser.BodyToMarkdown(chapter.ReadContent(), warnings, cancellationToken);
                    if (string.IsNullOrWhiteSpace(markdown))
                        continue;
                    anyText = true;
                    text.Append(title.Length > 0 ? MarkdownText.DemoteHeadings(markdown, 1) : markdown).Append("\n\n");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    warnings.Add($"Skipped chapter '{chapter.FilePath}': it could not be read ({ex.Message}).");
                }
            }

            if (failed > 0)
                metadata[PdfParser.MetadataKeyPageErrors] = failed.ToString();

            // A title and author with no chapter text is not a book Connapse can search.
            string content = anyText ? text.ToString().Trim() : string.Empty;
            if (!anyText)
                warnings.Add("Document contains no readable text content");

            return new ParsedDocument(content, metadata, warnings);
        }
        catch (Exception ex) when (ex is OperationCanceledException or PermanentIngestionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"Error parsing EPUB: {ex.Message}");
            return new ParsedDocument(string.Empty, metadata, warnings);
        }
    }

    /// <summary>
    /// The first content file META-INF/encryption.xml says is encrypted by something other than
    /// font obfuscation, or null. Read from the ZIP directly, before VersOne.Epub sees the book.
    /// </summary>
    internal static string? EncryptedContent(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("META-INF/encryption.xml");
        if (entry is null)
            return null;

        using var xml = XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });

        string? algorithm = null;
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element)
                continue;

            if (xml.LocalName == "EncryptionMethod")
                algorithm = xml.GetAttribute("Algorithm");
            else if (xml.LocalName == "CipherReference"
                && xml.GetAttribute("URI") is { } uri
                && (algorithm is null || !FontObfuscation.Contains(algorithm))
                && IsTextContent(uri))
            {
                return uri;
            }
        }

        return null;
    }

    private static async Task<Stream> ViewOfAsync(Stream stream, CancellationToken ct)
    {
        if (stream is MemoryStream memory && memory.TryGetBuffer(out ArraySegment<byte> buffer))
            return new MemoryStream(buffer.Array!, buffer.Offset + (int)memory.Position, buffer.Count - (int)memory.Position, writable: false);

        var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        copy.Position = 0;
        return copy;
    }

    private static bool IsTextContent(string uri)
    {
        string extension = Path.GetExtension(uri.Split('#', '?')[0]);
        return extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
