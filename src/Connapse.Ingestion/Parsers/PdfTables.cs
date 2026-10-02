using System.Text;
using Tabula;
using Tabula.Detectors;
using Tabula.Extractors;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
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
internal static class PdfTables
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

        // Everything outside the tables, as lines, placed with the tables by vertical position.
        var elements = new List<(double Top, string Text, bool IsTable)>();
        foreach (var table in tables)
            elements.Add((table.Top, ToMarkdown(table), true));

        // Space glyphs are letters to PdfPig; left in, each becomes a "word" of its own.
        var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters)
            .Where(w => !tables.Any(t => Contains(t, w.BoundingBox.Centroid)))
            .ToList();
        foreach (var (top, text) in GroupIntoLines(words))
            elements.Add((top, text, false));

        var builder = new StringBuilder();
        foreach (var (_, text, isTable) in elements.OrderByDescending(e => e.Top))
        {
            if (isTable && builder.Length > 0) builder.AppendLine();
            builder.AppendLine(text);
            if (isTable) builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static List<Table> FindTables(Page page, PdfTableMode mode)
    {
        PageArea area = ObjectExtractor.ExtractPage(page);

        var ruled = new SpreadsheetExtractionAlgorithm().Extract(area).Where(IsUsable).ToList();
        if (ruled.Count > 0 || mode != PdfTableMode.RuledAndStream)
            return ruled;

        var stream = new List<Table>();
        foreach (var region in new SimpleNurminenDetectionAlgorithm().Detect(area))
        {
            var regionArea = area.GetArea(region.BoundingBox);
            stream.AddRange(new BasicExtractionAlgorithm().Extract(regionArea).Where(IsUsable));
        }
        return stream;
    }

    /// <summary>
    /// At least two rows and two columns, most cells filled, and cells that read like cells
    /// rather than paragraphs. Stream detection otherwise turns ordinary prose into one-column
    /// or ragged "tables".
    /// </summary>
    private static bool IsUsable(Table table)
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

    /// <summary>The first row is the header, as Markdown requires; cells keep their own text.</summary>
    internal static string ToMarkdown(Table table)
    {
        var builder = new StringBuilder();
        bool header = true;
        foreach (var row in table.Rows)
        {
            builder.Append('|');
            foreach (var cell in row)
                builder.Append(' ').Append(Escape(cell.GetText())).Append(" |");
            builder.Append('\n');

            if (header)
            {
                builder.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", row.Count))).Append('\n');
                header = false;
            }
        }
        return builder.ToString().TrimEnd('\n');
    }

    private static string Escape(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("|", "\\|");

    /// <summary>Words grouped into lines by baseline, top to bottom, each read left to right.</summary>
    private static IEnumerable<(double Top, string Text)> GroupIntoLines(List<Word> words)
    {
        var lines = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var line = lines.LastOrDefault();
            double tolerance = Math.Max(1, word.BoundingBox.Height * 0.5);
            if (line is not null && Math.Abs(line[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= tolerance)
                line.Add(word);
            else
                lines.Add([word]);
        }

        foreach (var line in lines)
            yield return (line.Max(w => w.BoundingBox.Top), string.Join(' ', line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
    }
}
