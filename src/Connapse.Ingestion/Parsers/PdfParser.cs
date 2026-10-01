using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Validation;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Parser for PDF documents using PdfPig.
/// </summary>
/// <param name="limits">
/// Supplies the page cap and the text mode. Optional so tests and tools can build a parser without
/// settings; with none, any page count is parsed in the default mode.
/// </param>
public class PdfParser(IOptionsMonitor<UploadSettings>? limits = null) : IDocumentParser
{
    /// <summary>Parser metadata key: pages whose extraction threw, as opposed to pages with no text.</summary>
    public const string MetadataKeyPageErrors = "PageErrors";

    private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    public IReadOnlySet<string> SupportedExtensions => _supportedExtensions;

    /// <summary>
    /// 2: content-order text with word spacing and repeated header and footer removal, replacing
    /// page.Text (#597). Raised the olmOCR native-PDF score from 39.9% to 44.0%.
    /// </summary>
    public int Version => 2;

    private static readonly UploadSettings DefaultLimits = new();

    public async Task<ParsedDocument> ParseAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var metadata = new Dictionary<string, string>();
        UploadSettings settings = limits?.CurrentValue ?? DefaultLimits;

        try
        {
            // PdfPig is synchronous, but we'll wrap it in Task.Run for cancellation support
            return await Task.Run(() =>
            {
                using var document = PdfDocument.Open(stream);

                metadata["FileType"] = "PDF";
                metadata["PageCount"] = document.NumberOfPages.ToString();

                // Checked before any page is read: the cost of a PDF is in its pages, and
                // opening one only reads the cross-reference table.
                if (limits?.CurrentValue.MaxPdfPages is int maxPages && document.NumberOfPages > maxPages)
                    throw new PermanentIngestionException(
                        $"it has {document.NumberOfPages:N0} pages, over the {maxPages:N0} page limit [too_many_pages]");

                // Extract document-level metadata
                if (document.Information != null)
                {
                    var info = document.Information;
                    if (!string.IsNullOrWhiteSpace(info.Title))
                        metadata["Title"] = info.Title;
                    if (!string.IsNullOrWhiteSpace(info.Author))
                        metadata["Author"] = info.Author;
                    if (!string.IsNullOrWhiteSpace(info.Subject))
                        metadata["Subject"] = info.Subject;
                    if (!string.IsNullOrWhiteSpace(info.Creator))
                        metadata["Creator"] = info.Creator;
                    if (!string.IsNullOrWhiteSpace(info.CreationDate))
                        metadata["CreationDate"] = info.CreationDate;
                }

                PdfTextMode mode = PdfTextModes.Parse(settings.PdfTextMode);
                var pages = new PageText?[document.NumberOfPages];
                int pageErrors = 0;

                for (int i = 1; i <= document.NumberOfPages; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        pages[i - 1] = ExtractPage(document.GetPage(i), mode);
                    }
                    catch (Exception ex)
                    {
                        pageErrors++;
                        warnings.Add($"Error extracting text from page {i}: {ex.Message}");
                    }
                }

                if (pageErrors > 0)
                    metadata[MetadataKeyPageErrors] = pageErrors.ToString();

                if (settings.PdfRemoveRepeatedHeadersAndFooters)
                {
                    if (mode is PdfTextMode.XYCut or PdfTextMode.Docstrum)
                        RemoveDecorations(pages);
                    else
                        RemoveRepeatedEdgeLines(pages);
                }

                // Extract text from all pages
                var textBuilder = new System.Text.StringBuilder();
                for (int i = 1; i <= pages.Length; i++)
                {
                    if (pages[i - 1] is not { } page)
                        continue;

                    string pageText = page.Text;
                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        // A document-wide ratio hides one junk page among many good ones, so each
                        // page is judged on its own too. It is still indexed: the warning says why
                        // that page matches nothing.
                        var pageQuality = TextQuality.Measure(pageText);
                        if (TextQuality.DescribeGarbled(".pdf", pageQuality, settings) is not null)
                            warnings.Add($"Page {i} text is mostly unreadable glyphs");

                        // Add page marker for better context preservation
                        textBuilder.AppendLine($"--- Page {i} ---");
                        textBuilder.AppendLine(pageText);
                        textBuilder.AppendLine();
                    }
                    else
                    {
                        warnings.Add($"Page {i} contains no extractable text (may be scanned image)");
                    }
                }

                var content = textBuilder.ToString();

                if (string.IsNullOrWhiteSpace(content))
                {
                    warnings.Add("PDF contains no extractable text. Consider using OCR for scanned documents.");
                    content = string.Empty;
                }

                return new ParsedDocument(content, metadata, warnings);

            }, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or PermanentIngestionException)
        {
            throw;
        }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
        {
            // PdfPig has already tried the empty password, which opens every PDF that only
            // restricts permissions. What is left needs a password Connapse does not have.
            throw new PermanentIngestionException("the PDF is password-protected [encrypted]");
        }
        catch (Exception ex)
        {
            warnings.Add($"Error parsing PDF: {ex.Message}");
            return new ParsedDocument(string.Empty, metadata, warnings);
        }
    }

    /// <summary>
    /// A page's text, and for the block modes the blocks it came from in reading order, kept so
    /// headers and footers can be recognised across pages before the text is joined.
    /// </summary>
    private sealed class PageText(string text, List<TextBlock>? blocks)
    {
        public List<TextBlock>? Blocks { get; } = blocks;

        /// <summary>The text of a line-based page; replaced when its repeated edge lines are removed.</summary>
        public string LineText { get; set; } = text;

        public string Text => Blocks is null ? LineText : string.Join("\n\n", Blocks.Select(b => b.Text));
    }

    /// <summary>How many lines at each edge of a page can be a running header or footer.</summary>
    private const int EdgeLines = 2;

    /// <summary>
    /// The line-based counterpart of <see cref="RemoveDecorations"/> for the content-order modes,
    /// which have no blocks to compare. A line within the first or last two of a page, appearing
    /// there on at least half the pages (three at minimum), is a running header or footer.
    /// Digits are ignored when comparing, so "Page 3 of 40" matches "Page 4 of 40". A page is
    /// never emptied.
    /// </summary>
    private static void RemoveRepeatedEdgeLines(PageText?[] pages)
    {
        if (pages.Length < 3)
            return;

        var linesPerPage = pages
            .Select(p => p?.LineText.Split('\n').Select(l => l.TrimEnd('\r')).ToList())
            .ToArray();

        var pagesPerEdgeLine = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var lines in linesPerPage)
        {
            if (lines is null) continue;
            foreach (string key in EdgeKeys(lines).Distinct())
                pagesPerEdgeLine[key] = pagesPerEdgeLine.GetValueOrDefault(key) + 1;
        }

        int threshold = Math.Max(3, (pages.Length + 1) / 2);
        var repeated = pagesPerEdgeLine.Where(kv => kv.Value >= threshold).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        if (repeated.Count == 0)
            return;

        for (int i = 0; i < pages.Length; i++)
        {
            if (pages[i] is not { } page || linesPerPage[i] is not { } lines)
                continue;

            var nonBlank = lines.Select((line, index) => (line, index)).Where(x => x.line.Trim().Length > 0).ToList();
            var edge = nonBlank.Take(EdgeLines).Concat(nonBlank.TakeLast(EdgeLines)).Select(x => x.index).ToHashSet();
            var kept = lines.Where((line, index) => !(edge.Contains(index) && repeated.Contains(EdgeKey(line)))).ToList();

            if (kept.Any(l => l.Trim().Length > 0))
                page.LineText = string.Join("\n", kept);
        }
    }

    private static IEnumerable<string> EdgeKeys(List<string> lines)
    {
        var nonBlank = lines.Where(l => l.Trim().Length > 0).ToList();
        return nonBlank.Take(EdgeLines).Concat(nonBlank.TakeLast(EdgeLines)).Select(EdgeKey);
    }

    /// <summary>Whitespace collapsed and digits erased, so page numbers do not make lines differ.</summary>
    private static string EdgeKey(string line)
    {
        var builder = new System.Text.StringBuilder(line.Length);
        bool space = false;
        foreach (char c in line.Trim())
        {
            if (char.IsDigit(c)) continue;
            if (char.IsWhiteSpace(c))
            {
                if (!space) builder.Append(' ');
                space = true;
                continue;
            }
            builder.Append(c);
            space = false;
        }
        return builder.ToString().Trim();
    }

    private static PageText ExtractPage(Page page, PdfTextMode mode)
    {
        switch (mode)
        {
            case PdfTextMode.Raw:
                return new PageText(page.Text, null);

            case PdfTextMode.ContentOrder:
                return new PageText(ContentOrderTextExtractor.GetText(page), null);

            default:
                // Words from letter geometry rather than the content stream, which is where
                // page.Text loses its spaces; blocks from the page's whitespace; then an order
                // that reads a column to its end before the next.
                var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
                IPageSegmenter segmenter = mode == PdfTextMode.Docstrum
                    ? DocstrumBoundingBoxes.Instance
                    : RecursiveXYCut.Instance;
                var blocks = segmenter.GetBlocks(words);
                var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
                return new PageText(string.Empty, ordered.ToList());
        }
    }

    /// <summary>
    /// Drops blocks that repeat in the same place across neighbouring pages: running headers,
    /// footers and page numbers. They match every query about the document's subject and crowd
    /// out the passages that answer it. Needs three or more pages to judge; fewer are left alone.
    /// <para>
    /// Conservative on purpose. PdfPig's default similarity, 0.25, also matched body paragraphs
    /// that differ by a word or two in the same place on each page -- a templated report, a form
    /// -- and removed them. A page is never emptied: if every block on it looks like decoration,
    /// the judgement is more likely wrong than the page blank.
    /// </para>
    /// </summary>
    private static void RemoveDecorations(PageText?[] pages)
    {
        if (pages.Length < 3 || pages.Any(p => p?.Blocks is null))
            return;

        var blocksPerPage = pages.Select(p => (IReadOnlyList<TextBlock>)p!.Blocks!).ToList();
        var decorations = DecorationTextBlockClassifier.Get(blocksPerPage, DecorationMinimumSimilarity);

        for (int i = 0; i < pages.Length; i++)
        {
            var remove = decorations[i].ToHashSet();
            if (remove.Count < pages[i]!.Blocks!.Count)
                pages[i]!.Blocks!.RemoveAll(remove.Contains);
        }
    }

    private const double DecorationMinimumSimilarity = 0.75;
}

/// <summary>How PdfParser turns a page into text.</summary>
public enum PdfTextMode
{
    /// <summary>PdfPig's page.Text: content-stream order, words often run together.</summary>
    Raw,

    /// <summary>PdfPig's ContentOrderTextExtractor: content-stream order with spaces and line breaks.</summary>
    ContentOrder,

    /// <summary>Nearest-neighbour words, recursive XY-cut blocks, unsupervised reading order.</summary>
    XYCut,

    /// <summary>Nearest-neighbour words, Docstrum blocks, unsupervised reading order.</summary>
    Docstrum,
}

internal static class PdfTextModes
{
    /// <summary>An unrecognised value falls back to the default rather than failing every PDF.</summary>
    public static PdfTextMode Parse(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out PdfTextMode mode) ? mode : Enum.Parse<PdfTextMode>(UploadSettings.DefaultPdfTextMode);
}
