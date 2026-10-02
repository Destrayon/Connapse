using System.Globalization;
using System.Text;

namespace Connapse.Ingestion.Tests;

/// <summary>
/// Builds small PDFs for parser tests: pages of Helvetica text lines drawn at given positions, in
/// the order given, so a test can lay out columns the content stream visits out of reading order.
/// </summary>
internal static class TestPdf
{
    /// <summary>A line of text at a position, in points from the bottom-left corner.</summary>
    public sealed record Line(double X, double Y, string Text);

    /// <summary>One page: its lines, and optional raw content-stream operators drawn first.</summary>
    public sealed record Page(IReadOnlyList<Line> Lines, string? RawPrefix = null, string? ColorSpaces = null);

    public static byte[] Build(params Page[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // pages, filled in below
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var kids = new List<int>();
        foreach (var page in pages)
        {
            var content = new StringBuilder();
            if (page.RawPrefix is not null) content.Append(page.RawPrefix).Append('\n');
            foreach (var line in page.Lines)
            {
                string escaped = line.Text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
                content.Append(CultureInfo.InvariantCulture,
                    $"BT /F1 10 Tf {line.X:0.##} {line.Y:0.##} Td ({escaped}) Tj ET\n");
            }

            string stream = content.ToString();
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
            int contentId = objects.Count;

            string colorSpaces = page.ColorSpaces is null ? "" : $" /ColorSpace << {page.ColorSpaces} >>";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                        $"/Resources << /Font << /F1 3 0 R >>{colorSpaces} >> /Contents {contentId} 0 R >>");
            kids.Add(objects.Count);
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>";

        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(body.ToString()));
            body.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = Encoding.ASCII.GetByteCount(body.ToString());
        body.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) body.Append($"{offset:D10} 00000 n \n");
        body.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(body.ToString());
    }
}
