using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PDFtoImage;
using SkiaSharp;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Finds the regions of a PDF page -- text, titles, tables, figures, running headers and footers,
/// page numbers -- and the order they are read in, from its pixels (#642). The page is rendered with
/// PDFium and labelled by PaddlePaddle's PP-DocLayout v3 (Apache-2.0), an RT-DETR detector, on ONNX
/// Runtime on the CPU.
/// </summary>
internal static class PdfLayout
{
    /// <summary>A labelled region in page coordinates from 0 to 1, origin at the top left, as displayed.</summary>
    internal sealed record Region(string Label, float Score, float Left, float Top, float Right, float Bottom, int Order);

    /// <summary>The model's input side; the page is stretched to a square of this many pixels.</summary>
    internal const int InputSide = 800;

    internal const string ModelFile = "pp_doc_layoutv3.onnx";

    private static (InferenceSession Session, string[] Labels, int Threads)? s_model;

    // One page at a time per process, like OCR: a session holds its buffers per run.
    private static readonly Lock Gate = new();

    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "models", "layout", ModelFile);

    /// <summary>The regions on a page in reading order, unordered regions (headers, figures, tables) placed by position.</summary>
    public static IReadOnlyList<Region> Detect(byte[] pdf, int pageIndex, int threads, CancellationToken ct)
    {
        var input = new DenseTensor<float>([1, 3, InputSide, InputSide]);
        using (SKBitmap bitmap = Conversion.ToImage(pdf, pageIndex,
                   options: new RenderOptions(Width: InputSide, Height: InputSide, WithAspectRatio: false)))
        {
            ct.ThrowIfCancellationRequested();
            Fill(input, bitmap);
        }

        lock (Gate)
        {
            (InferenceSession session, string[] labels) = ModelFor(Math.Max(1, threads));
            var feeds = new[]
            {
                NamedOnnxValue.CreateFromTensor("im_shape", new DenseTensor<float>(new float[] { InputSide, InputSide }, [1, 2])),
                NamedOnnxValue.CreateFromTensor("image", input),
                NamedOnnxValue.CreateFromTensor("scale_factor", new DenseTensor<float>(new float[] { 1, 1 }, [1, 2])),
            };
            using var results = session.Run(feeds);
            var boxes = results.First(r => r.Name == "fetch_name_0").AsTensor<float>();
            int count = results.First(r => r.Name == "fetch_name_1").AsTensor<int>().GetValue(0);
            var raw = new List<RawBox>(count);
            for (int i = 0; i < Math.Min(count, boxes.Dimensions[0]); i++)
            {
                raw.Add(new RawBox((int)boxes[i, 0], boxes[i, 1], boxes[i, 2], boxes[i, 3], boxes[i, 4], boxes[i, 5], boxes[i, 6]));
            }
            return PostProcess(raw, labels, InputSide, InputSide);
        }
    }

    /// <summary>BGR, scaled to 0..1, channel-first: what RapidLayout feeds the model.</summary>
    private static void Fill(DenseTensor<float> input, SKBitmap bitmap)
    {
        using SKBitmap bgra = bitmap.ColorType == SKColorType.Bgra8888 ? bitmap : bitmap.Copy(SKColorType.Bgra8888);
        ReadOnlySpan<byte> pixels = bgra.GetPixelSpan();
        int stride = bgra.RowBytes;
        Span<float> buffer = input.Buffer.Span;
        int plane = InputSide * InputSide;
        for (int y = 0; y < InputSide; y++)
        {
            for (int x = 0; x < InputSide; x++)
            {
                int p = y * stride + x * 4;
                int o = y * InputSide + x;
                buffer[o] = pixels[p] / 255f;
                buffer[plane + o] = pixels[p + 1] / 255f;
                buffer[2 * plane + o] = pixels[p + 2] / 255f;
            }
        }
    }

    /// <summary>A detection as the model emits it: class, score, box in input pixels, order key.</summary>
    internal readonly record struct RawBox(int Class, float Score, float X0, float Y0, float X1, float Y1, float Order);

    internal const float ScoreThreshold = 0.5f;

    /// <summary>
    /// RapidLayout's post-processing for PP-DocLayout v3 (rapid_layout 1.2.1,
    /// model_handler/pp_doc_layout/post_process.py, Apache-2.0, Copyright (c) 2024 PaddlePaddle
    /// Authors), ported with its defaults: a score threshold, class-aware NMS, no page-sized image,
    /// sort by the model's order key, then drop tiny boxes and the smaller of two that mostly
    /// overlap. Coordinates are returned as fractions of the page.
    /// </summary>
    internal static IReadOnlyList<Region> PostProcess(IReadOnlyList<RawBox> raw, IReadOnlyList<string> labels, int width, int height)
    {
        List<RawBox> boxes = raw.Where(b => b.Score > ScoreThreshold && b.Class > -1 && b.Class < labels.Count).ToList();
        boxes = Nms(boxes, iouSame: 0.6f, iouDifferent: 0.98f);

        // A picture covering the page is the page, not a figure on it.
        if (boxes.Count > 1)
        {
            float areaThreshold = width > height ? 0.82f : 0.93f;
            List<RawBox> sized = boxes.Where(b =>
            {
                if (labels[b.Class] != "image")
                    return true;
                float w = Math.Min(width, b.X1) - Math.Max(0, b.X0);
                float h = Math.Min(height, b.Y1) - Math.Max(0, b.Y0);
                return w * h <= areaThreshold * width * height;
            }).ToList();
            if (sized.Count > 0)
                boxes = sized;
        }

        boxes = boxes.OrderBy(b => b.Order).ToList();
        boxes = FilterOverlaps(boxes, labels);

        var regions = new List<Region>(boxes.Count);
        for (int i = 0; i < boxes.Count; i++)
        {
            RawBox b = boxes[i];
            regions.Add(new Region(labels[b.Class], b.Score,
                Math.Clamp(b.X0 / width, 0, 1), Math.Clamp(b.Y0 / height, 0, 1),
                Math.Clamp(b.X1 / width, 0, 1), Math.Clamp(b.Y1 / height, 0, 1), i));
        }
        return regions;
    }

    private static List<RawBox> Nms(List<RawBox> boxes, float iouSame, float iouDifferent)
    {
        var remaining = boxes.OrderByDescending(b => b.Score).ToList();
        var kept = new List<RawBox>();
        while (remaining.Count > 0)
        {
            RawBox current = remaining[0];
            kept.Add(current);
            remaining = remaining.Skip(1)
                .Where(b => Iou(current, b) < (current.Class == b.Class ? iouSame : iouDifferent))
                .ToList();
        }
        return kept;
    }

    // The +1 is the reference's pixel-inclusive convention.
    private static float Iou(RawBox a, RawBox b)
    {
        float iw = Math.Max(0, Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0) + 1);
        float ih = Math.Max(0, Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0) + 1);
        float inter = iw * ih;
        float areaA = (a.X1 - a.X0 + 1) * (a.Y1 - a.Y0 + 1);
        float areaB = (b.X1 - b.X0 + 1) * (b.Y1 - b.Y0 + 1);
        return inter / (areaA + areaB - inter);
    }

    private static List<RawBox> FilterOverlaps(List<RawBox> sorted, IReadOnlyList<string> labels)
    {
        List<RawBox> boxes = sorted.Where(b => labels[b.Class] != "reference").ToList();
        var dropped = new HashSet<int>();
        for (int i = 0; i < boxes.Count; i++)
        {
            if (boxes[i].X1 - boxes[i].X0 < 6 || boxes[i].Y1 - boxes[i].Y0 < 6)
                dropped.Add(i);
            for (int j = i + 1; j < boxes.Count; j++)
            {
                if (dropped.Contains(i) || dropped.Contains(j))
                    continue;
                string li = labels[boxes[i].Class], lj = labels[boxes[j].Class];
                float overlap = OverlapOfSmaller(boxes[i], boxes[j]);
                if (li == "inline_formula" || lj == "inline_formula")
                {
                    if (overlap > 0.5f)
                    {
                        if (li == "inline_formula")
                            dropped.Add(i);
                        if (lj == "inline_formula")
                            dropped.Add(j);
                    }
                    continue;
                }
                if (overlap > 0.7f)
                {
                    if ((li == "image" || lj == "image") && li != lj)
                        continue;
                    if (Area(boxes[i]) >= Area(boxes[j]))
                        dropped.Add(j);
                    else
                        dropped.Add(i);
                }
            }
        }
        return boxes.Where((_, i) => !dropped.Contains(i)).ToList();
    }

    private static float Area(RawBox b) => Math.Max(0, b.X1 - b.X0) * Math.Max(0, b.Y1 - b.Y0);

    private static float OverlapOfSmaller(RawBox a, RawBox b)
    {
        float iw = Math.Max(0, Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0));
        float ih = Math.Max(0, Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0));
        float smaller = Math.Min(Area(a), Area(b));
        return smaller <= 0 ? 0 : iw * ih / smaller;
    }

    /// <summary>Called under <see cref="Gate"/>.</summary>
    private static (InferenceSession Session, string[] Labels) ModelFor(int threads)
    {
        if (s_model is { } loaded && loaded.Threads == threads)
            return (loaded.Session, loaded.Labels);

        s_model?.Session.Dispose();
        PdfOcr.DisableOnnxRuntimeTelemetry();
        using var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableCpuMemArena = false,
            EnableMemoryPattern = false,
        };
        // Same reasons as OCR: a fixed core budget per ingestion worker, no spinning while idle.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        var session = new InferenceSession(ModelPath, options);
        OrtEnv.Instance().DisableTelemetryEvents();
        string[] labels = session.ModelMetadata.CustomMetadataMap["character"].Split('\n');
        s_model = (session, labels, threads);
        return (session, labels);
    }
}
