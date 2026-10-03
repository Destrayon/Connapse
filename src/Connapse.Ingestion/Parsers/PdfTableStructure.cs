using System.Drawing;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PDFtoImage;
using SkiaSharp;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Reads the rows, columns and cells of a table from its pixels (#652): the table region is rendered
/// with PDFium and read by PaddleOCR's SLANet+ (Apache-2.0) on ONNX Runtime on the CPU. It predicts
/// the table's HTML structure -- rows, cells, column and row spans -- and a box for each cell; the
/// page's own words then fill the cells.
/// <para>
/// Tabula reads a borderless table from text alignment alone, and loses columns that do not line up
/// cleanly; this reads it the way a person does, from how it looks.
/// </para>
/// </summary>
internal static partial class PdfTableStructure
{
    /// <summary>A cell: its place in the grid, and its box in PDF points (origin bottom left).</summary>
    internal sealed record Cell(int Row, int Column, int RowSpan, int ColumnSpan, double Left, double Bottom, double Right, double Top);

    /// <summary>The cells; <paramref name="HeaderRows"/> rows were marked as the table's head, 0 when none were.</summary>
    internal sealed record Structure(IReadOnlyList<Cell> Cells, int Rows, int Columns, int HeaderRows);

    internal const string ModelFile = "slanet-plus.onnx";

    /// <summary>The model's input side: the table is scaled to fit a square of this many pixels.</summary>
    internal const int InputSide = 488;

    /// <summary>Resolution the region is rendered at before it is scaled to the input.</summary>
    private const int RenderDpi = 144;

    private static (InferenceSession Session, string[] Tokens, int Threads)? s_model;

    // One table at a time per process, like the layout model and OCR.
    private static readonly Lock Gate = new();

    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "models", "layout", ModelFile);

    /// <summary>
    /// The structure of the table in the given box of the page, in PDF points relative to the page's
    /// crop box; null when the model finds no cells.
    /// </summary>
    public static Structure? Recognize(
        byte[] pdf, int pageIndex, double cropLeft, double cropTop, double left, double bottom, double right, double top,
        int threads, CancellationToken ct)
    {
        // PDFtoImage's bounds are in points from the page's top-left corner, as displayed.
        var bounds = new RectangleF((float)(left - cropLeft), (float)(cropTop - top), (float)(right - left), (float)(top - bottom));
        if (bounds.Width <= 1 || bounds.Height <= 1)
            return null;

        var input = new DenseTensor<float>([1, 3, InputSide, InputSide]);
        int longest;
        using (SKBitmap bitmap = Conversion.ToImage(pdf, pageIndex, options: new RenderOptions(Dpi: RenderDpi, Bounds: bounds)))
        {
            ct.ThrowIfCancellationRequested();
            longest = Math.Max(bitmap.Width, bitmap.Height);
            Fill(input, bitmap);
        }

        IReadOnlyList<string> tokens;
        List<float[]> boxes;
        lock (Gate)
        {
            (InferenceSession session, string[] dictionary) = ModelFor(Math.Max(1, threads));
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input)]);
            Tensor<float> first = results[0].AsTensor<float>(), second = results[1].AsTensor<float>();
            (Tensor<float> bboxes, Tensor<float> probabilities) = first.Dimensions[2] == 8 ? (first, second) : (second, first);
            (tokens, boxes) = Decode(bboxes, probabilities, dictionary);
        }

        // Boxes come normalised to the padded input; the scaled table filled its longer side, so a
        // fraction of the input is that fraction of the rendered region's longer side.
        double pointsPerPixel = 72.0 / RenderDpi;
        return Build(tokens, boxes.Select(b =>
        {
            double x0 = Math.Min(Math.Min(b[0], b[2]), Math.Min(b[4], b[6])) * longest * pointsPerPixel;
            double x1 = Math.Max(Math.Max(b[0], b[2]), Math.Max(b[4], b[6])) * longest * pointsPerPixel;
            double y0 = Math.Min(Math.Min(b[1], b[3]), Math.Min(b[5], b[7])) * longest * pointsPerPixel;
            double y1 = Math.Max(Math.Max(b[1], b[3]), Math.Max(b[5], b[7])) * longest * pointsPerPixel;
            return (Left: left + x0, Bottom: top - y1, Right: left + x1, Top: top - y0);
        }).ToList());
    }

    /// <summary>
    /// RapidTable's preprocessing (rapid_table 3.0.2, table_structure/pp_structure/pre_process.py,
    /// Apache-2.0): scale the longer side to 488, normalise with ImageNet's mean and deviation, pad
    /// to a square at the bottom and right, channel-first, BGR as OpenCV loads it.
    /// </summary>
    private static void Fill(DenseTensor<float> input, SKBitmap bitmap)
    {
        double ratio = (double)InputSide / Math.Max(bitmap.Width, bitmap.Height);
        int width = Math.Max(1, (int)(bitmap.Width * ratio)), height = Math.Max(1, (int)(bitmap.Height * ratio));
        using SKBitmap scaled = bitmap.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888), new SKSamplingOptions(SKFilterMode.Linear))
                                ?? throw new InvalidOperationException("The table region could not be scaled.");
        ReadOnlySpan<byte> pixels = scaled.GetPixelSpan();
        int stride = scaled.RowBytes;
        Span<float> buffer = input.Buffer.Span;
        int plane = InputSide * InputSide;
        // Mean and deviation per channel in the B, G, R order the pixels are fed in.
        ReadOnlySpan<float> mean = [0.406f, 0.456f, 0.485f];
        ReadOnlySpan<float> deviation = [0.225f, 0.224f, 0.229f];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = y * stride + x * 4;
                int o = y * InputSide + x;
                for (int c = 0; c < 3; c++)
                    buffer[c * plane + o] = (pixels[p + c] / 255f - mean[c]) / deviation[c];
            }
        }
    }

    /// <summary>
    /// RapidTable's TableLabelDecode: the most likely token at each step until the end token, with
    /// the cell box predicted at each token that opens a cell.
    /// </summary>
    internal static (IReadOnlyList<string> Tokens, List<float[]> Boxes) Decode(
        Tensor<float> boxes, Tensor<float> probabilities, IReadOnlyList<string> dictionary)
    {
        var tokens = new List<string>();
        var cellBoxes = new List<float[]>();
        int steps = probabilities.Dimensions[1], classes = probabilities.Dimensions[2];
        for (int i = 0; i < steps; i++)
        {
            int best = 0;
            for (int c = 1; c < classes; c++)
                if (probabilities[0, i, c] > probabilities[0, i, best])
                    best = c;
            string token = best < dictionary.Count ? dictionary[best] : "eos";
            if (token == "eos" && i > 0)
                break;
            if (token is "sos" or "eos")
                continue;
            tokens.Add(token);
            if (token is "<td>" or "<td" or "<td></td>")
                cellBoxes.Add(Enumerable.Range(0, 8).Select(k => boxes[0, i, k]).ToArray());
        }
        return (tokens, cellBoxes);
    }

    /// <summary>The grid the structure tokens describe, each opening cell token taking the next box.</summary>
    internal static Structure? Build(IReadOnlyList<string> tokens, IReadOnlyList<(double Left, double Bottom, double Right, double Top)> boxes)
    {
        var cells = new List<Cell>();
        var occupied = new HashSet<(int, int)>();
        int row = -1, column = 0, box = 0, headerRows = 0;
        bool inHead = false;
        int rowSpan = 1, columnSpan = 1;
        bool opening = false;

        void Place()
        {
            if (row < 0)
                row = 0;
            while (occupied.Contains((row, column)))
                column++;
            var b = box < boxes.Count ? boxes[box] : default;
            box++;
            cells.Add(new Cell(row, column, rowSpan, columnSpan, b.Left, b.Bottom, b.Right, b.Top));
            for (int r = row; r < row + rowSpan; r++)
                for (int c = column; c < column + columnSpan; c++)
                    occupied.Add((r, c));
            column += columnSpan;
            rowSpan = columnSpan = 1;
        }

        foreach (string token in tokens)
        {
            switch (token)
            {
                case "<thead>": inHead = true; break;
                case "</thead>": inHead = false; break;
                case "<tr>":
                    row++;
                    column = 0;
                    if (inHead)
                        headerRows++;
                    break;
                case "<td></td>" or "<td>": Place(); break;
                case "<td": opening = true; break;
                case ">" when opening: opening = false; Place(); break;
                default:
                    if (opening && Span().Match(token) is { Success: true } span)
                    {
                        int value = Math.Clamp(int.Parse(span.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 1, 100);
                        if (span.Groups[1].Value == "colspan")
                            columnSpan = value;
                        else
                            rowSpan = value;
                    }
                    break;
            }
        }

        if (cells.Count == 0)
            return null;
        int rows = cells.Max(c => c.Row + c.RowSpan);
        int columns = cells.Max(c => c.Column + c.ColumnSpan);
        return new Structure(cells, rows, columns, headerRows);
    }

    [GeneratedRegex("""^\s*(colspan|rowspan)="(\d+)"$""")]
    private static partial Regex Span();

    /// <summary>
    /// The table's cell texts from the words inside it: each word goes to the cell holding its
    /// centre, or the nearest cell, so no word in the table is lost. A spanning cell's text fills
    /// every grid position it spans, so each row and column keeps its label.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<string>> Fill(
        Structure structure, IReadOnlyList<(string Text, double X, double Y, int Sequence)> words)
    {
        var texts = structure.Cells.Select(_ => new List<(string Text, int Sequence)>()).ToList();
        foreach (var word in words)
        {
            int best = 0;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < structure.Cells.Count; i++)
            {
                Cell c = structure.Cells[i];
                double dx = Math.Max(Math.Max(c.Left - word.X, word.X - c.Right), 0);
                double dy = Math.Max(Math.Max(c.Bottom - word.Y, word.Y - c.Top), 0);
                double distance = dx + dy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            texts[best].Add((word.Text, word.Sequence));
        }

        var grid = Enumerable.Range(0, structure.Rows).Select(_ => Enumerable.Repeat("", structure.Columns).ToArray()).ToArray();
        for (int i = 0; i < structure.Cells.Count; i++)
        {
            Cell c = structure.Cells[i];
            string text = string.Join(' ', texts[i].OrderBy(t => t.Sequence).Select(t => t.Text));
            for (int r = c.Row; r < c.Row + c.RowSpan && r < structure.Rows; r++)
                for (int col = c.Column; col < c.Column + c.ColumnSpan && col < structure.Columns; col++)
                    grid[r][col] = text;
        }
        return grid;
    }

    /// <summary>Called under <see cref="Gate"/>.</summary>
    private static (InferenceSession Session, string[] Tokens) ModelFor(int threads)
    {
        if (s_model is { } loaded && loaded.Threads == threads)
            return (loaded.Session, loaded.Tokens);

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
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        var session = new InferenceSession(ModelPath, options);
        OrtEnv.Instance().DisableTelemetryEvents();
        s_model = (session, Dictionary(session.ModelMetadata.CustomMetadataMap["character"]), threads);
        return (s_model.Value.Session, s_model.Value.Tokens);
    }

    /// <summary>
    /// The token list as TableLabelDecode builds it: the model's own list, with the open cell token
    /// "&lt;td&gt;" merged into "&lt;td&gt;&lt;/td&gt;", between a start and an end token.
    /// </summary>
    internal static string[] Dictionary(string character)
    {
        var tokens = character.Split('\n').ToList();
        if (!tokens.Contains("<td></td>"))
            tokens.Add("<td></td>");
        tokens.Remove("<td>");
        return ["sos", .. tokens, "eos"];
    }
}
