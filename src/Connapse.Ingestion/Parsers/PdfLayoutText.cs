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

    /// <summary>A word with its centre in page fractions (origin top left), as the regions are given.</summary>
    internal sealed record PlacedWord(string Text, double X, double Y, double Left, double Right, double Baseline, double Height);

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

        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters)
            .Select(w => new PlacedWord(w.Text,
                (w.BoundingBox.Centroid.X - crop.Left) / width,
                (crop.Top - w.BoundingBox.Centroid.Y) / height,
                w.BoundingBox.Left, w.BoundingBox.Right, w.BoundingBox.Bottom, w.BoundingBox.Height))
            .ToList();

        return Compose(words, regions, region => Table(page, crop, region));
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

            string? text = region.Label == "table" ? table(region) : null;
            blocks.Add(text ?? Lines(inside));
        }

        // Text the model put in no region is kept, after the regions: dropping it would lose words
        // the text layer has, and its place on the page is unknown.
        if (orphans.Count > 0)
            blocks.Add(Lines(orphans));

        return string.Join("\n\n", blocks.Where(b => b.Length > 0));
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
    /// The region's table as Markdown: Tabula's ruled-table reading first, then its alignment-based
    /// one, now that the region says a table is there; null when neither finds a usable table.
    /// </summary>
    private static string? Table(Page page, PdfRectangle crop, PdfLayout.Region region)
    {
        // Back to PDF points, bottom-left origin, with a little margin for glyphs on the box edge.
        const double Margin = 2;
        double left = crop.Left + region.Left * crop.Width - Margin;
        double right = crop.Left + region.Right * crop.Width + Margin;
        double top = crop.Top - region.Top * crop.Height + Margin;
        double bottom = crop.Top - region.Bottom * crop.Height - Margin;

        PageArea area = ObjectExtractor.ExtractPage(page).GetArea(new PdfRectangle(left, bottom, right, top));
        Table? found = new SpreadsheetExtractionAlgorithm().Extract(area).Where(PdfTables.IsUsable).MaxBy(t => t.RowCount * t.ColumnCount)
                       ?? new BasicExtractionAlgorithm().Extract(area).Where(PdfTables.IsUsable).MaxBy(t => t.RowCount * t.ColumnCount);
        return found is null ? null : PdfTables.ToMarkdown(found);
    }
}
