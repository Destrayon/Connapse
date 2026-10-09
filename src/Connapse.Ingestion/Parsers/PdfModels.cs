using System.Runtime.InteropServices;
using SkiaSharp;

namespace Connapse.Ingestion.Parsers;

/// <summary>A rendered image as BGRA bytes, rows packed with no padding: all that model inference sees (#680).</summary>
internal sealed record PdfImage(int Width, int Height, byte[] Bgra)
{
    public static PdfImage From(SKBitmap bitmap)
    {
        SKBitmap bgra = bitmap.ColorType == SKColorType.Bgra8888 ? bitmap : bitmap.Copy(SKColorType.Bgra8888);
        try
        {
            int row = bgra.Width * 4;
            byte[] bytes = new byte[row * bgra.Height];
            ReadOnlySpan<byte> pixels = bgra.GetPixelSpan();
            for (int y = 0; y < bgra.Height; y++)
                pixels.Slice(y * bgra.RowBytes, row).CopyTo(bytes.AsSpan(y * row, row));
            return new PdfImage(bgra.Width, bgra.Height, bytes);
        }
        finally
        {
            if (!ReferenceEquals(bgra, bitmap))
                bgra.Dispose();
        }
    }

    public SKBitmap ToBitmap()
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        int row = Width * 4;
        IntPtr target = bitmap.GetPixels();
        for (int y = 0; y < Height; y++)
            Marshal.Copy(Bgra, y * row, target + y * bitmap.RowBytes, row);
        return bitmap;
    }
}

/// <summary>
/// The models that read PDFs, behind one seam: rendered pixels in, small results out. The parser
/// renders the untrusted file; only pixels reach inference, which can then run outside the parser
/// host, in one process the hosts share (#680).
/// </summary>
internal interface IPdfModels
{
    /// <summary>The layout regions of a page rendered to <see cref="PdfLayout.InputSide"/> square.</summary>
    IReadOnlyList<PdfLayout.Region> Layout(PdfImage page, int threads, CancellationToken ct);

    /// <summary>The structure tokens of a rendered table region, with a box for each cell token, normalised to the input.</summary>
    (IReadOnlyList<string> Tokens, List<float[]> Boxes) TableStructure(PdfImage table, int threads, CancellationToken ct);

    /// <summary>The text lines recognised on a rendered page, in its pixels.</summary>
    IReadOnlyList<OcrLayout.Line> Ocr(PdfImage page, int threads, CancellationToken ct);
}

internal static class PdfModels
{
    /// <summary>The models this process reads PDFs with: in-process unless the host was given others.</summary>
    public static IPdfModels Current { get; set; } = LocalPdfModels.Instance;
}

/// <summary>The models loaded and run in this process.</summary>
internal sealed class LocalPdfModels : IPdfModels
{
    public static readonly LocalPdfModels Instance = new();

    public IReadOnlyList<PdfLayout.Region> Layout(PdfImage page, int threads, CancellationToken ct) =>
        PdfLayout.Infer(page, threads, ct);

    public (IReadOnlyList<string> Tokens, List<float[]> Boxes) TableStructure(PdfImage table, int threads, CancellationToken ct) =>
        PdfTableStructure.Infer(table, threads, ct);

    public IReadOnlyList<OcrLayout.Line> Ocr(PdfImage page, int threads, CancellationToken ct) =>
        PdfOcr.Infer(page, threads, ct);
}
