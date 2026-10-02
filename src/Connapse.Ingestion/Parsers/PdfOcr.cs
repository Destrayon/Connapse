using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
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

    /// <summary>The engine and the thread count it was loaded with; replaced when the setting changes.</summary>
    private static (RapidOcr Engine, int Threads)? s_engine;

    // RapidOcr holds ONNX sessions and buffers per call; one page at a time per process. In the
    // parser host there is only one parse at a time anyway.
    private static readonly Lock Gate = new();

    /// <summary>
    /// The page's text in reading order, empty when none was found, or null when the page is too
    /// large to render at a resolution text could be read at.
    /// </summary>
    public static string? ReadPage(byte[] pdf, int pageIndex, int dpi, int threads, CancellationToken ct)
    {
        if (RenderDpi(pdf, pageIndex, dpi) is not int renderDpi)
            return null;

        ct.ThrowIfCancellationRequested();

        using SKBitmap bitmap = Conversion.ToImage(pdf, pageIndex, options: new RenderOptions(Dpi: renderDpi));
        ct.ThrowIfCancellationRequested();

        lock (Gate)
        {
            threads = Math.Max(1, threads);
            var engine = EngineFor(threads);

            // Recognition of the page's lines runs on the same number of threads as inference.
            var result = engine.Detect(bitmap, RapidOcrOptions.Default with { RecMaxDegreeOfParallelism = threads }, ct);
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

    /// <summary>
    /// Below this, rendered text is too small to recognise, so a page that only fits the pixel cap
    /// at a lower resolution is not rendered at all: raising it to this would break the cap.
    /// </summary>
    internal const int MinReadableDpi = 50;

    /// <summary>
    /// The requested resolution, lowered so the page's longer side stays within
    /// <see cref="MaxRenderedSide"/>; or null for a page that fits only below <see cref="MinReadableDpi"/>.
    /// </summary>
    internal static int? RenderDpi(byte[] pdf, int pageIndex, int dpi)
    {
        var size = Conversion.GetPageSize(pdf, pageIndex);
        float longestInches = Math.Max(size.Width, size.Height) / 72f;
        if (!float.IsFinite(longestInches) || longestInches <= 0)
            return null;

        int fitting = (int)Math.Min(int.MaxValue, MaxRenderedSide / longestInches);
        int chosen = Math.Min(Math.Max(1, dpi), fitting);
        return chosen < MinReadableDpi ? null : chosen;
    }

    /// <summary>Called under <see cref="Gate"/>.</summary>
    private static RapidOcr EngineFor(int threads)
    {
        if (s_engine is { } loaded && loaded.Threads == threads)
            return loaded.Engine;

        s_engine?.Engine.Dispose();
        var engine = Load(threads);
        s_engine = (engine, threads);
        return engine;
    }

    /// <summary>
    /// ONNX Runtime's Linux build carries Microsoft's 1DS telemetry, on by default (#641): as it
    /// initialises it reads <c>/etc/machine-id</c> -- or, when that is missing, as in Connapse's
    /// image, runs <c>echo `blkid; hostname`</c> through a shell -- and posts events to
    /// <c>mobile.events.data.microsoft.com</c>. A self-hosted knowledge base has no business
    /// reporting its machine to anyone, and where the shell call fails (no shell, or the parser
    /// sandbox) ONNX Runtime dereferences the null result and crashes.
    /// <para>
    /// <c>ORT_DISABLE_TELEMETRY=1</c> stops it, but only if set before ONNX Runtime first loads: it
    /// reads the variable with getenv as it initialises. Parser hosts get it from the pool. For OCR
    /// in the web process it is set here, natively, since a managed SetEnvironmentVariable is not
    /// seen by getenv on Linux.
    /// </para>
    /// </summary>
    internal static void DisableOnnxRuntimeTelemetry()
    {
        if (OperatingSystem.IsWindows())
            Environment.SetEnvironmentVariable(TelemetryVariable, "1");
        else
            _ = SetEnv(TelemetryVariable, "1", overwrite: 1);
    }

    internal const string TelemetryVariable = "ORT_DISABLE_TELEMETRY";

    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetEnv(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);

    private static RapidOcr Load(int threads)
    {
        // Before the first ONNX Runtime call below creates its environment.
        DisableOnnxRuntimeTelemetry();

        // Absolute paths: RapidOcrNet's defaults are relative to the working directory, which for
        // the web process and the parser host is not where the models are deployed.
        string models = Path.Combine(AppContext.BaseDirectory, "models", "v5");
        var engine = new RapidOcr();
        engine.InitModels(
            Path.Combine(models, "ch_PP-OCRv5_mobile_det.onnx"),
            Path.Combine(models, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            Path.Combine(models, "latin_PP-OCRv5_rec_mobile_infer.onnx"),
            Path.Combine(models, "ppocrv5_latin_dict.txt"),
            SessionOptionsForOcr(threads));

        // And through the API, for a build that reads the setting later than the variable.
        OrtEnv.Instance().DisableTelemetryEvents();
        return engine;
    }

    /// <summary>
    /// A fixed thread count (UploadSettings.PdfOcrThreads, default one), no spinning, no memory
    /// arena. ONNX Runtime otherwise takes every core it can see -- one OCR'd page kept three busy,
    /// measured -- and every ingestion worker runs its own parser process, so a batch of scans would
    /// take the whole machine from search and the web app. At one thread, OCR uses at most one core
    /// per ingestion worker. Its threads also spin between runs by default, burning CPU in a process
    /// that sits idle in the pool, and its arena keeps the memory of the largest page it has seen
    /// for the life of the process.
    /// </summary>
    private static SessionOptions SessionOptionsForOcr(int threads)
    {
        // RapidOcrNet's own options, which set both thread counts explicitly -- only then does ONNX
        // Runtime skip pinning threads to cores -- at the graph optimisation level it uses.
        var options = RapidOcr.GetDefaultSessionOptions(threads);
        options.EnableCpuMemArena = false;
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        return options;
    }
}
