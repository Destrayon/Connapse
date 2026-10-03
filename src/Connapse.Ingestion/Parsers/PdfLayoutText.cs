using System.Text;
using Tabula;
using Tabula.Extractors;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Writes a page's text layer in the order and shape its layout regions give (#642): words go to
/// the region they sit in, regions are read in the layout model's order, running headers, footers
/// and page numbers are dropped, and a table region becomes a Markdown table.
/// </summary>
internal static partial class PdfLayoutText
{
    /// <summary>How far outside a region, in page fractions, a word may sit and still belong to it.</summary>
    internal const double NearRegion = 0.03;

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

    /// <summary>A table region written as Markdown, and the box in PDF points that table covers.</summary>
    internal sealed record TableText(string Markdown, double Left, double Bottom, double Right, double Top)
    {
        public bool Covers(PlacedWord word)
        {
            double x = (word.Left + word.Right) / 2, y = word.Baseline + word.Height / 2;
            return x >= Left - 1 && x <= Right + 1 && y >= Bottom - 1 && y <= Top + 1;
        }
    }

    /// <summary>
    /// The page as text, or null when the page has no words, or when layout would leave none of them
    /// -- every region labelled header, footer or page number -- which leaves it to the other
    /// extractors rather than indexing a page with a text layer as empty.
    /// </summary>
    /// <param name="tables">False when table extraction is off: table regions are read in content order.</param>
    /// <param name="recognize">
    /// Reads a table's structure from its pixels, given its box in PDF points (left, bottom, right,
    /// top); null when no table-structure model is available.
    /// </param>
    public static string? Extract(
        Page page, IReadOnlyList<PdfLayout.Region> regions, bool tables = true,
        Func<double, double, double, double, PdfTableStructure.Structure?>? recognize = null)
    {
        var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        if (letters.Count == 0)
            return null;

        PdfRectangle crop = page.CropBox.Bounds;
        double width = crop.Width, height = crop.Height;
        if (width <= 0 || height <= 0)
            return null;

        var words = ContentOrderWords(page.Letters)
            .Select(w => new PlacedWord(w.Text,
                ((w.Left + w.Right) / 2 - crop.Left) / width,
                (crop.Top - (w.Bottom + w.Top) / 2) / height,
                w.Left, w.Right, w.Baseline, w.Top - w.Bottom, w.Sequence))
            .ToList();

        // Ruled tables are found on the whole page: cropped to a region, a table loses the rules on
        // its edge and Tabula falls back to reading it by alignment, which merged columns and split
        // multi-line cells across rows (IRS Publication 15's section 15 table).
        PageArea? area = null;
        List<Table>? ruled = null;
        var used = new HashSet<Table>();
        string text = Compose(words, regions, region =>
        {
            if (!tables)
                return null;
            area ??= ObjectExtractor.ExtractPage(page);
            ruled ??= new SpreadsheetExtractionAlgorithm().Extract(area).Where(PdfTables.IsUsable).ToList();
            return Table(area, ruled, used, crop, region, words, recognize);
        });
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// The page's text from placed words and regions. <paramref name="table"/> writes a table region
    /// as Markdown, or returns null to read it as lines.
    /// </summary>
    internal static string Compose(
        IReadOnlyList<PlacedWord> words, IReadOnlyList<PdfLayout.Region> regions, Func<PdfLayout.Region, TableText?> table)
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
            // A word just outside every box -- the box drawn a little tight on a line's last word --
            // belongs to the nearest region rather than to the end of the page.
            owner ??= regions
                .Select(r => (Region: r, Distance: Math.Max(Math.Max(r.Left - word.X, word.X - r.Right), 0)
                                                   + Math.Max(Math.Max(r.Top - word.Y, word.Y - r.Bottom), 0)))
                .Where(x => x.Distance <= NearRegion)
                .OrderBy(x => x.Distance)
                .Select(x => x.Region)
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
            if (region.Label != "table")
            {
                blocks.Add(Lines(inside));
                continue;
            }
            if (table(region) is not { } written)
            {
                blocks.Add(ContentOrderLines(inside));
                continue;
            }
            blocks.Add(written.Markdown);

            // Words in the region the table does not cover -- a note under it, a cell Tabula
            // missed -- are kept after it rather than lost with the region.
            var uncovered = inside.Where(w => !written.Covers(w)).ToList();
            if (uncovered.Count > 0)
                blocks.Add(ContentOrderLines(uncovered));
        }

        // Text the model put in no region is kept, after the regions: dropping it would lose words
        // the text layer has, and its place on the page is unknown.
        if (orphans.Count > 0)
            blocks.Add(Lines(orphans));

        return string.Join("\n\n", blocks.Where(b => b.Length > 0));
    }

    /// <summary>A word read from the content stream, with its box in PDF points.</summary>
    internal sealed record StreamWord(string Text, double Left, double Right, double Bottom, double Top, double Baseline, int Sequence);

    /// <summary>
    /// Words in content-stream order, split where PdfPig's ContentOrderTextExtractor puts a space or
    /// a line break: a space glyph, a change of baseline, or a gap PdfPig judges whitespace for the
    /// letter's size. Its nearest-neighbour word extractor instead ran whole lines together in PDFs
    /// that space words by position alone -- "informationspecifiedonthedialogact" on olmOCR's
    /// multi-column pages -- so the layout mode now splits words exactly where content order does.
    /// </summary>
    internal static List<StreamWord> ContentOrderWords(IReadOnlyList<Letter> letters)
    {
        var words = new List<StreamWord>();
        var current = new List<Letter>();
        int start = 0;
        void Flush()
        {
            if (current.Count == 0)
                return;
            words.Add(new StreamWord(string.Concat(current.Select(l => l.Value)),
                current.Min(l => l.GlyphRectangle.Left), current.Max(l => l.GlyphRectangle.Right),
                current.Min(l => l.GlyphRectangle.Bottom), current.Max(l => l.GlyphRectangle.Top),
                current[0].StartBaseLine.Y, start));
            current.Clear();
        }

        for (int i = 0; i < letters.Count; i++)
        {
            Letter letter = letters[i];
            if (string.IsNullOrWhiteSpace(letter.Value))
            {
                Flush();
                continue;
            }
            if (current.Count > 0)
            {
                Letter previous = current[^1];
                double size = Math.Max(1, Math.Max(previous.PointSize, letter.PointSize));
                bool newLine = Math.Abs(letter.StartBaseLine.Y - previous.StartBaseLine.Y) > 0.5 * size;
                double gap = letter.StartBaseLine.X - previous.EndBaseLine.X;
                if (newLine || gap < -0.5 * size || UglyToad.PdfPig.Util.WhitespaceSizeStatistics.IsProbablyWhitespace(gap, previous))
                    Flush();
            }
            if (current.Count == 0)
                start = i;
            current.Add(letter);
        }
        Flush();
        return words;
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
    /// The region's table as Markdown: the page's ruled table that overlaps it most, else the
    /// table-structure model's reading of the region (#652), else Tabula's alignment-based reading
    /// of the region, now that the region says a table is there (in the
    /// Ruled table mode too: the model's region, not the page's text alignment, decides there is a
    /// table, which is what made alignment reading misfire on prose); null when
    /// neither finds a usable table. A ruled table two regions overlap is written once, for the first;
    /// the second writes nothing, since its words are in that table.
    /// </summary>
    private static TableText? Table(
        PageArea page, List<Table> ruled, HashSet<Table> used, PdfRectangle crop, PdfLayout.Region region,
        IReadOnlyList<PlacedWord> words, Func<double, double, double, double, PdfTableStructure.Structure?>? recognize)
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
        if (ruledTable is not null && !HasStackedRows(ruledTable.Rows.SelectMany(r => r).Select(c => (c.Left, c.Bottom, c.Right, c.Top)), words))
            return Written(used.Add(ruledTable) ? PdfTables.ToMarkdown(ruledTable) : "", ruledTable);

        if (recognize?.Invoke(left, bottom, right, top) is { } structure && FromStructure(structure, words, left, bottom, right, top) is { } read)
            return read;

        PageArea area = page.GetArea(new PdfRectangle(left, bottom, right, top));
        Table? found = new BasicExtractionAlgorithm().Extract(area)
            .Where(t => PdfTables.IsUsable(t) && !HasWrappedCells(t)
                        && !HasStackedRows(t.Rows.SelectMany(r => r).Select(c => (c.Left, c.Bottom, c.Right, c.Top)), words))
            .MaxBy(t => t.RowCount * t.ColumnCount);
        return found is null ? null : Written(PdfTables.ToMarkdown(found), found);
    }

    /// <summary>
    /// A table read by the structure model, filled with the words inside the region. Null when the
    /// structure is too thin to be a table -- one row or column -- when most of its cells are empty,
    /// or when its rows ran together (<see cref="HasStackedRows"/>): the model failing rather than the
    /// table being sparse. The region is then read the other ways.
    /// </summary>
    internal static TableText? FromStructure(
        PdfTableStructure.Structure structure, IReadOnlyList<PlacedWord> words, double left, double bottom, double right, double top)
    {
        if (structure.Rows < 2 || structure.Columns < 2)
            return null;

        var inside = words
            .Select(w => (w.Text, X: (w.Left + w.Right) / 2, Y: w.Baseline + w.Height / 2, w.Sequence))
            .Where(w => w.X >= left && w.X <= right && w.Y >= bottom && w.Y <= top)
            .ToList();
        IReadOnlyList<IReadOnlyList<string>> grid = PdfTableStructure.Fill(structure, inside);
        int filled = structure.Cells.Count(c => grid[c.Row][c.Column].Length > 0);
        if (filled < 0.4 * structure.Cells.Count)
            return null;

        if (HasStackedRows(structure.Cells.Select(c => (c.Left, c.Bottom, c.Right, c.Top)), words))
            return null;

        // A table of prose cells -- several sentences each, IRS Publication 15's section 15 grid --
        // is beyond the model at 488 pixels, which scattered its words; content order reads it
        // cell by cell instead.
        var texts = structure.Cells.Select(c => grid[c.Row][c.Column]).Where(s => s.Length > 0).ToList();
        if (texts.Count > 0 && texts.Average(s => s.Split(' ').Count(w => w.Any(char.IsLetter))) > MaxWordsPerCell)
            return null;

        return new TableText(PdfTables.ToMarkdown(grid, structure.HeaderRows > 0 ? structure.HeaderRows : null), left, bottom, right, top);
    }

    /// <summary>
    /// True when the cells hold stacks of numbers: rows that ran together, so one "cell" holds a
    /// column's values from several rows, one per line. Census tables rule only between sections, so
    /// Tabula's ruled reading made each section one row (P60 Table A-3, and A-6); a long table scaled
    /// to the structure model's input does the same. A wrapped prose cell -- several lines of words --
    /// is a real cell and does not count.
    /// </summary>
    internal static bool HasStackedRows(IEnumerable<(double Left, double Bottom, double Right, double Top)> cells, IReadOnlyList<PlacedWord> words)
    {
        int nonEmpty = 0, stacked = 0;
        foreach (var cell in cells)
        {
            var inside = words.Where(w =>
            {
                double x = (w.Left + w.Right) / 2, y = w.Baseline + w.Height / 2;
                return x >= cell.Left && x <= cell.Right && y >= cell.Bottom && y <= cell.Top;
            }).OrderByDescending(w => w.Baseline).ThenBy(w => w.Left).ToList();
            if (inside.Count == 0)
                continue;
            nonEmpty++;

            var lines = new List<List<PlacedWord>>();
            foreach (PlacedWord word in inside)
            {
                if (lines.Count > 0 && Math.Abs(lines[^1][0].Baseline - word.Baseline) <= Math.Max(1, 0.5 * word.Height))
                    lines[^1].Add(word);
                else
                    lines.Add([word]);
            }
            int numeric = lines.Count(l => NumericLine().IsMatch(string.Join(' ', l.Select(w => w.Text))));
            if (lines.Count >= 3 && numeric * 3 >= lines.Count * 2)
                stacked++;
        }
        return nonEmpty > 0 && stacked > 0.2 * nonEmpty;
    }

    /// <summary>Average words per cell past which a table is prose, read in content order rather than by the model.</summary>
    internal const double MaxWordsPerCell = 8;

    /// <summary>A line that is one number, as tables print them: signs, stars, currency, percent, parentheses.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[\s*±+\-\u2013\u2212$(]*[\d.,]+[%)]?\s*$")]
    private static partial System.Text.RegularExpressions.Regex NumericLine();

    private static TableText Written(string markdown, Table table) =>
        new(markdown, table.Left, table.Bottom, table.Right, table.Top);

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
