using System.Text;
using PDFtoImage;
using SkiaSharp;

namespace Connapse.Ingestion.Tests;

/// <summary>
/// Builds image-only PDFs, like a scanner writes: each page of a <see cref="TestPdf"/> is rendered
/// with PDFium and embedded as a JPEG, so the result has pictures of text and no text layer (#598).
/// PDFium draws Helvetica from its own built-in fonts, so this works without system fonts.
/// </summary>
internal static class TestScanPdf
{
    public static byte[] Build(int dpi = 150, params TestPdf.Page[] pages)
    {
        byte[] source = TestPdf.Build(pages);
        var images = new List<(byte[] Jpeg, int Width, int Height)>();
        for (int i = 0; i < pages.Length; i++)
        {
            using SKBitmap bitmap = Conversion.ToImage(source, i, options: new RenderOptions(Dpi: dpi));
            using SKData jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
            images.Add((jpeg.ToArray(), bitmap.Width, bitmap.Height));
        }

        return ImagePdf(images);
    }

    /// <summary>A PDF whose pages each show one JPEG filling a US Letter page, and nothing else.</summary>
    public static byte[] ImagePdf(IReadOnlyList<(byte[] Jpeg, int Width, int Height)> images, double pageWidth = 612, double pageHeight = 792)
    {
        using var output = new MemoryStream();
        var offsets = new List<long>();
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        void Object(string body)
        {
            offsets.Add(output.Position);
            Write($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n");
        int pageCount = images.Count;
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        string kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i * 3} 0 R"));
        Object($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");

        for (int i = 0; i < pageCount; i++)
        {
            int page = 3 + i * 3;
            var (jpeg, width, height) = images[i];
            string w = pageWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string h = pageHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Object($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] " +
                   $"/Resources << /XObject << /Im1 {page + 2} 0 R >> >> /Contents {page + 1} 0 R >>");
            string draw = $"q {w} 0 0 {h} 0 0 cm /Im1 Do Q\n";
            Object($"<< /Length {draw.Length} >>\nstream\n{draw}endstream");

            offsets.Add(output.Position);
            Write($"{offsets.Count} 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} " +
                  $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            output.Write(jpeg);
            Write("\nendstream\nendobj\n");
        }

        long xref = output.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets)
            Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}
