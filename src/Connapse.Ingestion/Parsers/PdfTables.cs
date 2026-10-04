using System.Text;
using Tabula;
using Tabula.Detectors;
using Tabula.Extractors;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Connapse.Ingestion.Parsers;

/// <summary>Which tables PdfParser extracts with Tabula.</summary>
public enum PdfTableMode
{
    /// <summary>No table extraction; tables come out as the text extractor reads them.</summary>
    Off,

    /// <summary>Tables drawn with ruling lines (lattice mode).</summary>
    Ruled,

    /// <summary>Ruled tables, and borderless tables found from text alignment (stream mode).</summary>
    RuledAndStream,
}

/// <summary>
/// Finds tables on a PDF page with Tabula and writes the page as text with each table as a
/// Markdown table in its place.
/// <para>
/// A text extractor reads a table cell by cell in drawing order, or row by row with the columns
/// run together; either way a cell loses the row and column it belongs to, which is what a
/// question about a table asks for. Before this, olmOCR-bench's table checks passed 0.1%.
/// </para>
/// </summary>
internal static partial class PdfTables
{
    /// <summary>
    /// The page's text with its tables as Markdown, or null when no table was found, in which
    /// case the caller's own extractor reads the page as before.
    /// </summary>
    public static string? ExtractWithTables(Page page, PdfTableMode mode)
    {
        if (mode == PdfTableMode.Off)
            return null;

        List<Table> tables = FindTables(page, mode);
        if (tables.Count == 0)
            return null;

        // Space glyphs are letters to PdfPig; left in, each becomes a "word" of its own.
        var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters)
            .Where(w => !tables.Any(t => Contains(t, w.BoundingBox.Centroid)))
            .ToList();

        // The text outside the tables, as regions in reading order, so a two-column page around a
        // table still reads each column to its end. Joining whole baselines across the page
        // instead interleaved the columns into alternating fragments.
        var regions = words.Count == 0
            ? []
            : UnsupervisedReadingOrderDetector.Instance.Get(RecursiveXYCut.Instance.GetBlocks(words)).ToList();

        // Each table goes before the first region, in reading order, that starts below it and
        // shares some of its width: that is where a reader moving down the column meets it.
        // Keyed by region index: two regions with the same text (a repeated label) must not be
        // confused when finding where a table goes.
        var output = regions.Select((r, i) => (Region: i, Text: r.Text, IsTable: false)).ToList();
        foreach (var table in tables.OrderBy(t => t.Top))
        {
            int at = regions.FindIndex(r =>
                r.BoundingBox.Top <= table.Top && r.BoundingBox.Left < table.Right && r.BoundingBox.Right > table.Left);
            var entry = (Region: -1, Text: ToMarkdown(table), IsTable: true);
            if (at < 0)
            {
                output.Add(entry);
            }
            else
            {
                int index = output.FindIndex(o => o.Region == at);
                output.Insert(index < 0 ? output.Count : index, entry);
            }
        }

        var builder = new StringBuilder();
        foreach (var (_, text, isTable) in output)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.AppendLine(text);
        }

        return builder.ToString().TrimEnd();
    }

    private static List<Table> FindTables(Page page, PdfTableMode mode)
    {
        PageArea area = ObjectExtractor.ExtractPage(page);

        var tables = new SpreadsheetExtractionAlgorithm().Extract(area).Where(IsUsable).ToList();
        if (mode != PdfTableMode.RuledAndStream)
            return tables;

        // Borderless tables elsewhere on the page too; a region a ruled table already covers is
        // the same table found twice.
        foreach (var region in new SimpleNurminenDetectionAlgorithm().Detect(area))
        {
            if (tables.Any(t => Overlaps(t, region)))
                continue;

            var regionArea = area.GetArea(region.BoundingBox);
            tables.AddRange(new BasicExtractionAlgorithm().Extract(regionArea).Where(IsUsable));
        }
        return tables;
    }

    private static bool Overlaps(TableRectangle a, TableRectangle b) =>
        a.Left < b.Right && a.Right > b.Left && a.Bottom < b.Top && a.Top > b.Bottom;

    /// <summary>
    /// At least two rows and two columns, most cells filled, and cells that read like cells
    /// rather than paragraphs. Stream detection otherwise turns ordinary prose into one-column
    /// or ragged "tables".
    /// </summary>
    internal static bool IsUsable(Table table)
    {
        if (table.RowCount < 2 || table.ColumnCount < 2)
            return false;

        var texts = table.Rows.SelectMany(r => r).Select(c => c.GetText().Trim()).ToList();
        if (texts.Count == 0)
            return false;

        double filled = texts.Count(t => t.Length > 0) / (double)texts.Count;
        double averageLength = texts.Where(t => t.Length > 0).Select(t => t.Length).DefaultIfEmpty(0).Average();
        return filled >= 0.5 && averageLength <= 60;
    }

    private static bool Contains(Table table, PdfPoint point) =>
        point.X >= table.Left - 1 && point.X <= table.Right + 1 &&
        point.Y >= table.Bottom - 1 && point.Y <= table.Top + 1;

    /// <summary>
    /// The header first, as Markdown requires; cells keep their own text. A column heading set on
    /// several lines comes out of Tabula as several rows, of which only the first would be the
    /// Markdown header -- the one row repeated on every chunk of a long table, so later chunks lost
    /// "Income year" and kept only a fragment. Leading rows above the first row of numbers are
    /// merged, column by column, into one header row (#642).
    /// </summary>
    internal static string ToMarkdown(Table table) =>
        ToMarkdown(table.Rows.Select(r => r.Select(c => c.GetText()).ToList()).ToList());

    /// <summary>
    /// A grid of cell texts as a Markdown table. <paramref name="headerRows"/> says how many leading
    /// rows are headings, when the source knows; otherwise <see cref="HeaderRowCount"/> judges.
    /// </summary>
    internal static string ToMarkdown(IReadOnlyList<IReadOnlyList<string>> cells, int? headerRows = null)
    {
        var rows = cells.Select(r => r.Select(Escape).ToList()).ToList();
        int columns = rows.Max(r => r.Count);
        int headings = Math.Clamp(headerRows ?? HeaderRowCount(rows), 1, Math.Max(1, rows.Count - 1));
        if (rows.Count == 1)
            headings = 1;
        var header = Enumerable.Range(0, columns)
            // A heading spanning two of the merged rows appears in both; it is written once.
            .Select(c => string.Join(' ', WithoutRepeats(rows.Take(headings).Select(r => c < r.Count ? r[c] : "").Where(s => s.Length > 0))))
            .ToList();

        var builder = new StringBuilder();
        AppendRow(builder, header);
        builder.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns))).Append('\n');
        foreach (var row in rows.Skip(headings))
            AppendRow(builder, row.Concat(Enumerable.Repeat("", columns - row.Count)).ToList());
        return builder.ToString().TrimEnd('\n');
    }

    private static IEnumerable<string> WithoutRepeats(IEnumerable<string> texts)
    {
        string? previous = null;
        foreach (string text in texts)
        {
            if (text != previous)
                yield return text;
            previous = text;
        }
    }

    private static void AppendRow(StringBuilder builder, List<string> cells)
    {
        builder.Append('|');
        foreach (string cell in cells)
            builder.Append(' ').Append(cell).Append(" |");
        builder.Append('\n');
    }

    /// <summary>
    /// How many leading rows are column headings: those before the first row whose cells after the
    /// first are mostly numbers, at most five, leaving two rows of data. One when no row of numbers
    /// comes that early -- a table of words, whose rows cannot be told from headings this way.
    /// </summary>
    internal static int HeaderRowCount(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        const int MaxHeaderRows = 5;
        for (int i = 0; i < Math.Min(MaxHeaderRows + 1, rows.Count - 1); i++)
        {
            var values = rows[i].Skip(1).Where(c => c.Length > 0).ToList();
            if (values.Count > 0 && values.Count(c => NumberCell().IsMatch(c)) * 2 > values.Count)
                return Math.Max(1, i);
        }
        return 1;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[-\u2013\u2212+$(]?\s*\d[\d,.\s]*%?\)?$|^[Xx\u2013-]$")]
    private static partial System.Text.RegularExpressions.Regex NumberCell();

    private static string Escape(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("|", "\\|");
}
