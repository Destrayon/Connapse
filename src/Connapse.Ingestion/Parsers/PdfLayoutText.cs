using System.Text;
using Tabula;
using Tabula.Extractors;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Writes a page's text layer in the order and shape its layout regions give (#642): words go to
/// the region they sit in, regions are read in the layout model's order, running headers, footers
/// and page numbers are dropped, and a table region becomes a Markdown table.
/// </summary>
internal static class PdfLayoutText
{
    /// <summary>Regions whose text repeats on every page and answers nothing: dropped.</summary>
    internal static readonly IReadOnlySet<string> Decorations = new HashSet<string>(StringComparer.Ordinal)
    {
        "header", "footer", "number", "header_image", "footer_image",
    };

    /// <summary>
    /// A word with its centre in page fractions (origin top left), as the regions are given, and
    /// where its first letter comes in the content stream.
    /// </summary>
    internal sealed record PlacedWord(
        string Text, double X, double Y, double Left, double Right, double Baseline, double Height, int Sequence = 0);

    /// <summary>The page as text, or null when the page has no words, which leaves it to the other extractors.</summary>
    public static string? Extract(Page page, IReadOnlyList<PdfLayout.Region> regions)
    {
        var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        if (letters.Count == 0)
            return null;

        PdfRectangle crop = page.CropBox.Bounds;
        double width = crop.Width, height = crop.Height;
        if (width <= 0 || height <= 0)
            return null;

        var sequence = new Dictionary<Letter, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < letters.Count; i++)
            sequence[letters[i]] = i;
        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters)
            .Select(w => new PlacedWord(w.Text,
                (w.BoundingBox.Centroid.X - crop.Left) / width,
                (crop.Top - w.BoundingBox.Centroid.Y) / height,
                w.BoundingBox.Left, w.BoundingBox.Right, w.BoundingBox.Bottom, w.BoundingBox.Height,
                w.Letters.Count > 0 && sequence.TryGetValue(w.Letters[0], out int at) ? at : 0))
            .ToList();

        // Ruled tables are found on the whole page: cropped to a region, a table loses the rules on
        // its edge and Tabula falls back to reading it by alignment, which merged columns and split
        // multi-line cells across rows (IRS Publication 15's section 15 table).
        PageArea? area = null;
        List<Table>? ruled = null;
        var used = new HashSet<Table>();
        return Compose(words, regions, region =>
        {
            area ??= ObjectExtractor.ExtractPage(page);
            ruled ??= new SpreadsheetExtractionAlgorithm().Extract(area).Where(PdfTables.IsUsable).ToList();
            return Table(area, ruled, used, crop, region);
        });
    }

    /// <summary>
    /// The page's text from placed words and regions. <paramref name="table"/> writes a table region
    /// as Markdown, or returns null to read it as lines.
    /// </summary>
    internal static string Compose(
        IReadOnlyList<PlacedWord> words, IReadOnlyList<PdfLayout.Region> regions, Func<PdfLayout.Region, string?> table)
    {
        var byRegion = regions.ToDictionary(r => r, _ => new List<PlacedWord>());
        var orphans = new List<PlacedWord>();
        foreach (PlacedWord word in words)
        {
            // The smallest region holding the word's centre: a table inside a larger text box
            // claims its own cells.
            PdfLayout.Region? owner = regions
                .Where(r => word.X >= r.Left && word.X <= r.Right && word.Y >= r.Top && word.Y <= r.Bottom)
                .OrderBy(r => (r.Right - r.Left) * (r.Bottom - r.Top))
                .FirstOrDefault();
            if (owner is null)
                orphans.Add(word);
            else
                byRegion[owner].Add(word);
        }

        var blocks = new List<string>();
        foreach (PdfLayout.Region region in regions.OrderBy(r => r.Order))
        {
            List<PlacedWord> inside = byRegion[region];
            if (inside.Count == 0 || Decorations.Contains(region.Label))
                continue;

            // A table no reader could split into cells is read in content-stream order, which for
            // most generators is cell by cell: a wrapped cell stays together, where reading across
            // the page would interleave the columns' lines.
            blocks.Add(region.Label == "table" ? table(region) ?? ContentOrderLines(inside) : Lines(inside));
        }

        // Text the model put in no region is kept, after the regions: dropping it would lose words
        // the text layer has, and its place on the page is unknown.
        if (orphans.Count > 0)
            blocks.Add(Lines(orphans));

        return string.Join("\n\n", blocks.Where(b => b.Length > 0));
    }

    /// <summary>Words in content-stream order, a new line wherever the baseline changes.</summary>
    internal static string ContentOrderLines(IReadOnlyList<PlacedWord> words)
    {
        var builder = new StringBuilder();
        PlacedWord? previous = null;
        foreach (PlacedWord word in words.OrderBy(w => w.Sequence))
        {
            if (previous is not null)
            {
                bool sameLine = Math.Abs(previous.Baseline - word.Baseline) <= Math.Max(1, 0.5 * Math.Min(previous.Height, word.Height))
                                && word.Left >= previous.Left;
                builder.Append(sameLine ? ' ' : '\n');
            }
            builder.Append(word.Text);
            previous = word;
        }
        return builder.ToString();
    }

    /// <summary>Words grouped into lines by baseline, top to bottom, each line left to right.</summary>
    internal static string Lines(IReadOnlyList<PlacedWord> words)
    {
        var lines = new List<List<PlacedWord>>();
        foreach (PlacedWord word in words.OrderByDescending(w => w.Baseline).ThenBy(w => w.Left))
        {
            List<PlacedWord>? line = lines.LastOrDefault();
            if (line is not null && Math.Abs(line[0].Baseline - word.Baseline) <= Math.Max(1, 0.5 * Math.Min(line[0].Height, word.Height)))
                line.Add(word);
            else
                lines.Add([word]);
        }
        return string.Join("\n", lines.Select(l => string.Join(" ", l.OrderBy(w => w.Left).Select(w => w.Text))));
    }

    /// <summary>
    /// The region's table as Markdown: the page's ruled table that overlaps it most, else Tabula's
    /// alignment-based reading of the region, now that the region says a table is there; null when
    /// neither finds a usable table. A ruled table two regions overlap is written once, for the first;
    /// the second writes nothing, since its words are in that table.
    /// </summary>
    private static string? Table(PageArea page, List<Table> ruled, HashSet<Table> used, PdfRectangle crop, PdfLayout.Region region)
    {
        // Back to PDF points, bottom-left origin, with a little margin for glyphs on the box edge.
        const double Margin = 2;
        double left = crop.Left + region.Left * crop.Width - Margin;
        double right = crop.Left + region.Right * crop.Width + Margin;
        double top = crop.Top - region.Top * crop.Height + Margin;
        double bottom = crop.Top - region.Bottom * crop.Height - Margin;

        static double Overlap(Table t, double l, double b, double r, double tp) =>
            Math.Max(0, Math.Min(t.Right, r) - Math.Max(t.Left, l)) * Math.Max(0, Math.Min(t.Top, tp) - Math.Max(t.Bottom, b));
        Table? ruledTable = ruled
            .Where(t => Overlap(t, left, bottom, right, top) > 0.5 * Math.Min((t.Right - t.Left) * (t.Top - t.Bottom), (right - left) * (top - bottom)))
            .MaxBy(t => Overlap(t, left, bottom, right, top));
        if (ruledTable is not null)
            return used.Add(ruledTable) ? PdfTables.ToMarkdown(ruledTable) : "";

        PageArea area = page.GetArea(new PdfRectangle(left, bottom, right, top));
        Table? found = new BasicExtractionAlgorithm().Extract(area)
            .Where(t => PdfTables.IsUsable(t) && !HasWrappedCells(t))
            .MaxBy(t => t.RowCount * t.ColumnCount);
        return found is null ? null : PdfTables.ToMarkdown(found);
    }

    /// <summary>
    /// Reading by alignment puts each line of a wrapped cell in a row of its own. Past the first few
    /// rows, where multi-line column headers are normal, a row with an empty first cell and text
    /// elsewhere is such a continuation; a table with many is a table of prose cells, which reads
    /// better in content order than split into rows.
    /// </summary>
    internal static bool HasWrappedCells(Table table)
    {
        const int HeaderRows = 3;
        var body = table.Rows.Skip(HeaderRows).ToList();
        if (body.Count == 0)
            return false;
        int continuations = body.Count(row =>
            row.Count > 1 && string.IsNullOrWhiteSpace(row[0].GetText()) && row.Skip(1).Any(c => !string.IsNullOrWhiteSpace(c.GetText())));
        return continuations > 0.1 * body.Count;
    }
}
