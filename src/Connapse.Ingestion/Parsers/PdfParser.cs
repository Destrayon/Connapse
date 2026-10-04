using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Validation;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
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
public partial class PdfParser(IOptionsMonitor<UploadSettings>? limits = null) : IDocumentParser
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
    /// 3: ruled tables as Markdown tables via Tabula (#597); 44.0% to 47.4%.
    /// 4: OCR for pages with no text layer or a garbled one (#598).
    /// 5: the Layout text mode, and table headings set on several lines merged into one header row (#642).
    /// 6: table regions without ruling lines read by the SLANet+ table-structure model (#652).
    /// 7: OCR'd pages read by layout region too: running headers dropped, regions ordered, tables
    /// read by SLANet+ (#653).
    /// </summary>
    public int Version => 7;

    /// <summary>
    /// Text mode, table mode, header and footer removal, and OCR on or off with its resolution.
    /// Not the OCR page budget or thread count: pages the budget left unread already mark the
    /// document incomplete, which a reindex retries, and threads do not change the text.
    /// </summary>
    public string OutputSettings
    {
        get
        {
            UploadSettings settings = limits?.CurrentValue ?? DefaultLimits;
            PdfTableMode tables = Enum.TryParse(settings.PdfTableMode, ignoreCase: true, out PdfTableMode parsed)
                ? parsed
                : Enum.Parse<PdfTableMode>(UploadSettings.DefaultPdfTableMode);
            string ocr = settings.PdfOcr ? $"{settings.PdfOcrDpi}dpi" : "off";
            return $"text={PdfTextModes.Parse(settings.PdfTextMode)};tables={tables};" +
                   $"headers={settings.PdfRemoveRepeatedHeadersAndFooters};ocr={ocr}";
        }
    }

    /// <summary>Parser metadata key: pages whose text came from OCR.</summary>
    public const string MetadataKeyOcrPages = "OcrPages";

    private static readonly UploadSettings DefaultLimits = new();

    public async Task<ParsedDocument> ParseAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var metadata = new Dictionary<string, string>();
        UploadSettings settings = limits?.CurrentValue ?? DefaultLimits;
        var parseClock = Stopwatch.StartNew();

        try
        {
            // PDFium renders pages for OCR from the file's bytes, and PdfPig reads the same array.
            byte[] pdf = AsArray(await TextParser.ReadAllBytesAsync(stream, cancellationToken));

            // PdfPig is synchronous, but we'll wrap it in Task.Run for cancellation support
            return await Task.Run(() =>
            {
                using var document = PdfDocument.Open(pdf);

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
                PdfTableMode tableMode = Enum.TryParse(settings.PdfTableMode, ignoreCase: true, out PdfTableMode parsedTables)
                    ? parsedTables
                    : Enum.Parse<PdfTableMode>(UploadSettings.DefaultPdfTableMode);
                var pages = new PageText?[document.NumberOfPages];
                var failedPages = new HashSet<int>();
                var layout = mode == PdfTextMode.Layout ? new LayoutRun(pdf, settings, tableMode, parseClock) : null;

                for (int i = 1; i <= document.NumberOfPages; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        Page page = document.GetPage(i);
                        pages[i - 1] = layout?.Extract(page, i, warnings, cancellationToken)
                                       ?? ExtractPage(page, i, mode == PdfTextMode.Layout ? PdfTextMode.ContentOrder : mode, tableMode, warnings);
                    }
                    catch (Exception ex)
                    {
                        failedPages.Add(i);
                        warnings.Add($"Error extracting text from page {i}: {ex.Message}");
                    }
                }

                if (settings.PdfOcr)
                    OcrUnreadablePages(pdf, pages, failedPages, settings, parseClock, metadata, warnings, layout, cancellationToken);

                if (failedPages.Count > 0)
                    metadata[MetadataKeyPageErrors] = failedPages.Count.ToString();

                layout?.Report(metadata, warnings);

                if (settings.PdfRemoveRepeatedHeadersAndFooters)
                {
                    // An OCR'd page has lines but no blocks, and block removal skips a document
                    // with any such page; the line-based removal covers every page.
                    if (mode is PdfTextMode.XYCut or PdfTextMode.Docstrum && pages.All(p => p is null || p.Blocks is not null))
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
                    // Indexed as empty, a scan is a Ready document that matches nothing; with OCR
                    // off, say why instead.
                    if (!settings.PdfOcr && failedPages.Count == 0)
                        throw new PermanentIngestionException("the PDF has no text layer and OCR is turned off [no_text_layer]");

                    warnings.Add("PDF contains no extractable text.");
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
    /// Replaces the text of every page that has none, or none worth reading, with what OCR finds on
    /// it, up to the document's OCR budget. A page whose extraction threw counts as having none, and
    /// is no longer a page error once OCR has read it.
    /// <para>
    /// A page OCR did not get to -- over the page or time budget, too large to render, failed, or
    /// with OCR unavailable -- joins <paramref name="incompletePages"/>, the page errors. That marks
    /// the document's extraction incomplete, so a reindex of unchanged bytes keeps the index it has
    /// rather than replacing it with less, and a later reindex retries it.
    /// </para>
    /// </summary>
    private static void OcrUnreadablePages(
        byte[] pdf, PageText?[] pages, HashSet<int> incompletePages, UploadSettings settings, Stopwatch parseClock,
        Dictionary<string, string> metadata, List<string> warnings, LayoutRun? layout, CancellationToken ct)
    {
        int budget = Math.Max(0, settings.MaxOcrPagesPerDocument);
        int read = 0;
        int skipped = 0;
        var watch = new Stopwatch();

        // A page costs seconds, and more on a small server: a long scan that ran OCR into the parse
        // deadline would lose every page, so OCR stops with time to spare and keeps what it read.
        // Measured from the start of the parse, which extraction has already used part of.
        var timeBudget = TimeSpan.FromSeconds(Math.Max(1, settings.ParseTimeoutSeconds) * OcrShareOfParseTimeout);
        bool outOfTime = false;
        bool unavailable = false;
        var laidOut = new List<(int Page, PdfOcr.OcrPage Lines)>();

        for (int i = 1; i <= pages.Length; i++)
        {
            string? text = pages[i - 1]?.Text;
            bool unreadable = string.IsNullOrWhiteSpace(text)
                || TextQuality.DescribeGarbled(".pdf", TextQuality.Measure(text), settings) is not null;
            if (!unreadable)
                continue;

            if (unavailable || read >= budget || (outOfTime = outOfTime || parseClock.Elapsed >= timeBudget))
            {
                if (!unavailable)
                    skipped++;
                incompletePages.Add(i);
                continue;
            }

            ct.ThrowIfCancellationRequested();
            watch.Start();
            try
            {
                PdfOcr.OcrPage? lines = PdfOcr.ReadLines(pdf, i - 1, settings.PdfOcrDpi, settings.PdfOcrThreads, ct);
                string? ocr = lines is null ? null : OcrLayout.Arrange(lines.Lines);
                if (lines is not null && layout is not null && !string.IsNullOrWhiteSpace(ocr))
                    laidOut.Add((i, lines));
                read++;
                if (ocr is null)
                {
                    incompletePages.Add(i);
                    warnings.Add($"Page {i} is too large to render for OCR at a readable resolution.");
                }
                else if (!string.IsNullOrWhiteSpace(ocr))
                {
                    pages[i - 1] = new PageText(ocr, null);

                    // The page has text now; a warning that its extraction failed would read as a
                    // page that produced none.
                    if (incompletePages.Remove(i))
                    {
                        string failed = $"Error extracting text from page {i}: ";
                        int at = warnings.FindIndex(w => w.StartsWith(failed, StringComparison.Ordinal));
                        if (at >= 0)
                            warnings[at] = $"Page {i} was read by OCR because its text layer could not be extracted ({warnings[at][failed.Length..]})";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or BadImageFormatException)
            {
                // A deployment without the native libraries, not a bad page: every other page would
                // fail the same way, so say so once and stop.
                warnings.Add($"OCR is unavailable in this deployment, so pages without a text layer were not read: {ex.GetBaseException().Message}");
                unavailable = true;
                incompletePages.Add(i);
            }
            catch (Exception ex)
            {
                read++;
                incompletePages.Add(i);
                warnings.Add($"OCR failed on page {i}: {ex.Message}");
            }
            finally
            {
                watch.Stop();
            }
        }

        // In Layout mode the OCR'd pages are then read by layout region (#653): running headers
        // dropped, regions ordered, tables read. Only after every page that could be read was:
        // layout is an improvement on a page OCR has already read, and must not cost a later page
        // its OCR. It has until a later share of the deadline, and stops there.
        if (layout is not null)
        {
            var until = TimeSpan.FromSeconds(Math.Max(1, settings.ParseTimeoutSeconds) * LayoutOcrShareOfParseTimeout);
            foreach ((int page, PdfOcr.OcrPage lines) in laidOut)
            {
                ct.ThrowIfCancellationRequested();
                if (parseClock.Elapsed >= until)
                    break;
                if (layout.ComposeOcr(page, lines, until, warnings, ct) is { } composed)
                    pages[page - 1] = new PageText(composed, null);
            }
        }

        if (read > 0)
        {
            metadata[MetadataKeyOcrPages] = read.ToString();
            metadata["OcrSecondsPerPage"] = (watch.Elapsed.TotalSeconds / read).ToString("F2", CultureInfo.InvariantCulture);
        }

        if (skipped > 0)
        {
            warnings.Add(outOfTime
                ? $"{skipped} page(s) with no readable text were not OCR'd: OCR stopped {parseClock.Elapsed.TotalSeconds:0} seconds into the parse to finish within its deadline."
                : $"{skipped} page(s) with no readable text were not OCR'd: the {budget}-page OCR limit per document was reached.");
        }
    }

    /// <summary>The share of the parse deadline OCR may spend, leaving the rest for extraction and the reply.</summary>
    private const double OcrShareOfParseTimeout = 0.6;

    /// <summary>
    /// The share of the parse deadline the layout model may spend. Past it, pages are read in content
    /// order, as without the model; OCR's own share is measured from the same start, so a scan in a
    /// long document still gets its turn.
    /// </summary>
    private const double LayoutShareOfParseTimeout = 0.4;

    /// <summary>
    /// How far into the parse deadline layout may still lay out pages OCR has read (#653): after
    /// OCR's own 60%, with the rest left for the reply.
    /// </summary>
    private const double LayoutOcrShareOfParseTimeout = 0.8;

    /// <summary>Parser metadata key: pages read with the layout model.</summary>
    public const string MetadataKeyLayoutPages = "LayoutPages";

    /// <summary>
    /// The layout model's pass over a document's pages (#642): each page with a text layer is
    /// rendered and labelled, then read region by region. A page the model cannot do -- rotated, past
    /// the time budget, or the model failed or is missing -- returns null and is read in content
    /// order instead, as without it.
    /// </summary>
    private sealed class LayoutRun(byte[] pdf, UploadSettings settings, PdfTableMode tableMode, Stopwatch parseClock)
    {
        private readonly TimeSpan _budget = TimeSpan.FromSeconds(Math.Max(1, settings.ParseTimeoutSeconds) * LayoutShareOfParseTimeout);
        private readonly Stopwatch _watch = new();
        private int _read;
        private int _skipped;
        private string? _unavailable;

        public PageText? Extract(Page page, int pageNumber, List<string> warnings, CancellationToken ct)
        {
            // A rotated page renders turned while its text layer keeps unrotated coordinates.
            if (_unavailable is not null || page.Rotation.Value != 0 || !page.Letters.Any(l => !string.IsNullOrWhiteSpace(l.Value)))
                return null;
            if (parseClock.Elapsed >= _budget)
            {
                _skipped++;
                return null;
            }

            _watch.Start();
            try
            {
                var regions = PdfLayout.Detect(pdf, pageNumber - 1, settings.PdfOcrThreads, ct);
                string? text = PdfLayoutText.Extract(page, regions, tables: tableMode != PdfTableMode.Off,
                    recognize: (left, bottom, right, top) =>
                        RecognizeTable(pageNumber, page.CropBox.Bounds.Left, page.CropBox.Bounds.Top, left, bottom, right, top, _budget, warnings, ct));
                if (text is null)
                    return null;
                _read++;
                return new PageText(text, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException
                                           or BadImageFormatException or FileNotFoundException or OnnxRuntimeException)
            {
                _unavailable = ex.GetBaseException().Message;
                return null;
            }
            catch (Exception ex)
            {
                warnings.Add($"Layout analysis failed on page {pageNumber}, read in content order: {ex.Message}");
                return null;
            }
            finally
            {
                _watch.Stop();
            }
        }

        private int _ocrPages;

        /// <summary>
        /// An OCR'd page's lines by layout region (#653); null -- arranged by position instead --
        /// past the time budget, when the model is missing or fails, or when layout leaves no text.
        /// </summary>
        public string? ComposeOcr(int pageNumber, PdfOcr.OcrPage ocr, TimeSpan until, List<string> warnings, CancellationToken ct)
        {
            if (_unavailable is not null || parseClock.Elapsed >= until)
                return null;

            _watch.Start();
            try
            {
                var size = PDFtoImage.Conversion.GetPageSize(pdf, pageNumber - 1);
                var regions = PdfLayout.Detect(pdf, pageNumber - 1, settings.PdfOcrThreads, ct);
                string? text = PdfLayoutText.ComposeOcr(ocr, size.Width, size.Height, regions,
                    tableMode == PdfTableMode.Off ? null
                        : (left, bottom, right, top) => RecognizeTable(pageNumber, 0, size.Height, left, bottom, right, top, until, warnings, ct));
                if (text is not null)
                    _ocrPages++;
                return text;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException
                                           or BadImageFormatException or FileNotFoundException or OnnxRuntimeException)
            {
                _unavailable = ex.GetBaseException().Message;
                return null;
            }
            catch (Exception ex)
            {
                warnings.Add($"Layout analysis failed on OCR'd page {pageNumber}, arranged by position: {ex.Message}");
                return null;
            }
            finally
            {
                _watch.Stop();
            }
        }

        private string? _tablesUnavailable;
        private int _tables;
        private readonly Stopwatch _tableWatch = new();

        /// <summary>
        /// The table-structure model's reading of a table region (#652); null -- the region read the
        /// other ways -- when the model is missing, fails, or the time budget is spent.
        /// </summary>
        private PdfTableStructure.Structure? RecognizeTable(
            int pageNumber, double cropLeft, double cropTop, double left, double bottom, double right, double top,
            TimeSpan until, List<string> warnings, CancellationToken ct)
        {
            if (_tablesUnavailable is not null || parseClock.Elapsed >= until)
                return null;

            _tableWatch.Start();
            try
            {
                var structure = PdfTableStructure.Recognize(pdf, pageNumber - 1, cropLeft, cropTop, left, bottom, right, top, settings.PdfOcrThreads, ct);
                _tables++;
                return structure;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException
                                           or BadImageFormatException or FileNotFoundException or OnnxRuntimeException)
            {
                _tablesUnavailable = ex.GetBaseException().Message;
                return null;
            }
            catch (Exception ex)
            {
                warnings.Add($"Table structure recognition failed on page {pageNumber}, read by alignment: {ex.Message}");
                return null;
            }
            finally
            {
                _tableWatch.Stop();
            }
        }

        public void Report(Dictionary<string, string> metadata, List<string> warnings)
        {
            if (_ocrPages > 0)
                metadata["LayoutOcrPages"] = _ocrPages.ToString(CultureInfo.InvariantCulture);
            if (_tables > 0)
            {
                metadata["LayoutTables"] = _tables.ToString(CultureInfo.InvariantCulture);
                metadata["LayoutSecondsPerTable"] = (_tableWatch.Elapsed.TotalSeconds / _tables).ToString("F2", CultureInfo.InvariantCulture);
            }
            if (_tablesUnavailable is not null)
                warnings.Add($"Table structure recognition is unavailable in this deployment, so tables were read by alignment: {_tablesUnavailable}");
            if (_read > 0)
            {
                metadata[MetadataKeyLayoutPages] = _read.ToString(CultureInfo.InvariantCulture);
                metadata["LayoutSecondsPerPage"] = (_watch.Elapsed.TotalSeconds / _read).ToString("F2", CultureInfo.InvariantCulture);
            }
            if (_unavailable is not null)
                warnings.Add($"Layout analysis is unavailable in this deployment, so pages were read in content order: {_unavailable}");
            if (_skipped > 0)
                warnings.Add($"{_skipped} page(s) were read in content order: layout analysis stopped {parseClock.Elapsed.TotalSeconds:0} seconds into the parse to finish within its deadline.");
        }
    }

    private static byte[] AsArray(ReadOnlyMemory<byte> bytes) =>
        MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array
            : bytes.ToArray();

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
    /// Lines must match exactly, except page numbers ("7", "Page 7", "Page 7 of 40", "7/40"),
    /// which match each other: ignoring digits everywhere also matched table rows such as
    /// "Revenue 2024: 12" and "Revenue 2025: 13" and deleted them. A page is never emptied.
    /// </summary>
    private static void RemoveRepeatedEdgeLines(PageText?[] pages)
    {
        if (pages.Length < 3)
            return;

        var linesPerPage = pages
            .Select(p => p?.LineText.Split('\n').Select(l => l.TrimEnd('\r')).ToList())
            .ToArray();

        // A table repeated at the top of each page has the same header row and separator on
        // every one; removing them as a running header left the rows without their columns.
        static bool IsTableLine(string line) => line.TrimStart().StartsWith('|');

        var pagesPerEdgeLine = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var lines in linesPerPage)
        {
            if (lines is null) continue;
            foreach (string key in EdgeKeys(lines.Where(l => !IsTableLine(l)).ToList()).Distinct())
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
            var kept = lines.Where((line, index) =>
                IsTableLine(line) || !(edge.Contains(index) && repeated.Contains(EdgeKey(line)))).ToList();

            if (kept.Any(l => l.Trim().Length > 0))
                page.LineText = string.Join("\n", kept);
        }
    }

    private static IEnumerable<string> EdgeKeys(List<string> lines)
    {
        var nonBlank = lines.Where(l => l.Trim().Length > 0).ToList();
        return nonBlank.Take(EdgeLines).Concat(nonBlank.TakeLast(EdgeLines)).Select(EdgeKey);
    }

    /// <summary>The line with whitespace collapsed; every page-number line shares one key.</summary>
    private static string EdgeKey(string line)
    {
        string collapsed = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return PageNumberLine().IsMatch(collapsed) ? PageNumberKey : collapsed;
    }

    private const string PageNumberKey = "\u0000page-number";

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?:(?:page|p\.?)\s*)?[-\u2013\u2014]?\s*\d{1,4}\s*[-\u2013\u2014]?(?:\s*(?:of|/)\s*\d{1,4})?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PageNumberLine();

    private static PageText ExtractPage(Page page, int pageNumber, PdfTextMode mode, PdfTableMode tableMode, List<string> warnings)
    {
        // Tables take over a page they appear on, for the line-based modes: the page is then read
        // in regions around them. The block modes order their own regions and keep doing so.
        if (mode is PdfTextMode.Raw or PdfTextMode.ContentOrder && tableMode != PdfTableMode.Off)
        {
            try
            {
                if (PdfTables.ExtractWithTables(page, tableMode) is { } withTables)
                    return new PageText(withTables, null);
            }
            catch (Exception ex)
            {
                // Table extraction is an improvement on a page that already reads; its failure
                // must not cost the page. The plain extractor below reads it instead.
                warnings.Add($"Table extraction failed on page {pageNumber}, read as plain text: {ex.Message}");
            }
        }

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

    /// <summary>
    /// Regions, their reading order, and running headers and footers from a layout model run on the
    /// rendered page (PP-DocLayout v3); tables in table regions. Pages it cannot do read as ContentOrder.
    /// </summary>
    Layout,
}

internal static class PdfTextModes
{
    /// <summary>An unrecognised value falls back to the default rather than failing every PDF.</summary>
    public static PdfTextMode Parse(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out PdfTextMode mode) ? mode : Enum.Parse<PdfTextMode>(UploadSettings.DefaultPdfTextMode);
}
