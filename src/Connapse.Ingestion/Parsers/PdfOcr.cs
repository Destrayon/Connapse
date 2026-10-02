using PDFtoImage;
using RapidOcrNet;
using SkiaSharp;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Reads the text of a PDF page from its pixels (#598): the page is rendered with PDFium and
/// recognised with RapidOcrNet, PaddleOCR's PP-OCRv5 models on ONNX Runtime, on the CPU.
/// <para>
/// Used only for pages whose text layer is missing or garbled -- a scan, or a page drawn with
/// fonts that carry no Unicode mapping. Rendering rather than pulling out embedded images is what
/// covers the second kind, whose glyphs are vector outlines, not an image.
/// </para>
/// </summary>
internal static class PdfOcr
{
    /// <summary>
    /// The longest side a page is rendered to. A poster-sized page at the OCR resolution would be
    /// a bitmap of gigabytes; past this the text is large enough to read at a lower resolution.
    /// </summary>
    internal const int MaxRenderedSide = 4000;

    private static readonly Lazy<RapidOcr> Engine = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    // RapidOcr holds ONNX sessions and buffers per call; one page at a time per process. In the
    // parser host there is only one parse at a time anyway.
    private static readonly Lock Gate = new();

    /// <summary>The page's text, one line per recognised text line, or empty when none was found.</summary>
    public static string ReadPage(byte[] pdf, int pageIndex, int dpi, CancellationToken ct)
    {
        using SKBitmap bitmap = Conversion.ToImage(pdf, pageIndex, options: new RenderOptions(Dpi: RenderDpi(pdf, pageIndex, dpi)));
        ct.ThrowIfCancellationRequested();

        lock (Gate)
        {
            var result = Engine.Value.Detect(bitmap, RapidOcrOptions.Default, ct);
            return OcrLayout.Arrange(result.TextBlocks.Select(ToLine).ToList());
        }
    }

    /// <summary>A recognised block as a line box: the bounds of its (possibly slanted) quadrilateral.</summary>
    internal static OcrLayout.Line ToLine(TextBlock block)
    {
        var points = block.BoxPoints;
        if (points is not { Length: > 0 })
            return new OcrLayout.Line(0, 0, 0, 0, block.Text);

        return new OcrLayout.Line(
            points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y), block.Text);
    }

    /// <summary>The requested resolution, lowered so the page's longer side stays within <see cref="MaxRenderedSide"/>.</summary>
    internal static int RenderDpi(byte[] pdf, int pageIndex, int dpi)
    {
        var size = Conversion.GetPageSize(pdf, pageIndex);
        float longestInches = Math.Max(size.Width, size.Height) / 72f;
        if (longestInches <= 0)
            return dpi;

        int fitting = (int)(MaxRenderedSide / longestInches);
        return Math.Clamp(Math.Min(dpi, fitting), 36, Math.Max(36, dpi));
    }

    private static RapidOcr Load()
    {
        // Absolute paths: RapidOcrNet's defaults are relative to the working directory, which for
        // the web process and the parser host is not where the models are deployed.
        string models = Path.Combine(AppContext.BaseDirectory, "models", "v5");
        var engine = new RapidOcr();
        engine.InitModels(
            Path.Combine(models, "ch_PP-OCRv5_mobile_det.onnx"),
            Path.Combine(models, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            Path.Combine(models, "latin_PP-OCRv5_rec_mobile_infer.onnx"),
            Path.Combine(models, "ppocrv5_latin_dict.txt"),
            Math.Clamp(Environment.ProcessorCount, 1, 4));
        return engine;
    }
}
