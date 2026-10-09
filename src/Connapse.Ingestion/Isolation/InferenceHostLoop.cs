using Connapse.Ingestion.Parsers;
using static Connapse.Ingestion.Isolation.ParserProtocol;

namespace Connapse.Ingestion.Isolation;

/// <summary>
/// The wire format of the inference host (#680), over the same length-prefixed frames as
/// <see cref="ParserProtocol"/>: a <see cref="Ready"/> frame once started and confined, then per
/// request a JSON <see cref="InferRequest"/> and the image's BGRA bytes, answered by one JSON
/// <see cref="InferResponse"/>.
/// </summary>
internal static class InferenceProtocol
{
    public const string Layout = "layout";
    public const string Table = "table";
    public const string Ocr = "ocr";

    /// <summary>The largest image a request carries: an OCR render is at most 4,000 pixels a side.</summary>
    public const int MaxPixelFrame = PdfOcr.MaxRenderedSide * PdfOcr.MaxRenderedSide * 4;

    public const int MaxJsonFrame = 64 * 1024 * 1024;

    /// <summary>One model run: which model, the threads it may use, and the size of the pixels that follow.</summary>
    public sealed record InferRequest(string Model, int Width, int Height, int Threads);

    /// <summary>The model's result, or why there is none. <see cref="OutOfMemory"/> means the host exits after replying.</summary>
    public sealed record InferResponse(
        List<PdfLayout.Region>? Regions = null,
        List<string>? Tokens = null,
        List<float[]>? Boxes = null,
        List<OcrLayout.Line>? Lines = null,
        string? Error = null,
        bool OutOfMemory = false);
}

/// <summary>
/// The inference host's loop (#680): loads the layout, table and OCR models once and runs them on
/// the pixels parser hosts rendered, so the hosts need not each hold a copy. It never sees a
/// document, only images, and is confined like a parser host all the same.
/// </summary>
public static class InferenceHostLoop
{
    /// <summary>The argument after the host's path that starts it as the inference host.</summary>
    public const string Argument = "--inference";

    public static async Task RunAsync(Stream input, Stream output)
    {
        var (refusal, sandbox) = ParserHostLoop.Confine();

        bool outOfMemory = false;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is OutOfMemoryException)
                outOfMemory = true;
        };

        await WriteJsonAsync(output, new Ready(sandbox), CancellationToken.None);
        await output.FlushAsync();

        while (true)
        {
            byte[]? header = await ReadFrameAsync(input, InferenceProtocol.MaxJsonFrame, CancellationToken.None);
            if (header is null)
                return;
            byte[] pixels = await ReadFrameAsync(input, InferenceProtocol.MaxPixelFrame, CancellationToken.None)
                ?? throw new EndOfStreamException("A request header arrived without its pixels.");

            outOfMemory = false;
            InferenceProtocol.InferResponse response;
            try
            {
                response = refusal is not null
                    ? new InferenceProtocol.InferResponse(Error: refusal)
                    : Run(Deserialize<InferenceProtocol.InferRequest>(header), pixels);
            }
            catch (OutOfMemoryException)
            {
                response = new InferenceProtocol.InferResponse(OutOfMemory: true);
            }
            catch (Exception ex)
            {
                response = new InferenceProtocol.InferResponse(Error: ex.Message);
            }

            if (outOfMemory)
                response = new InferenceProtocol.InferResponse(OutOfMemory: true);

            pixels = [];
            await WriteJsonAsync(output, response, CancellationToken.None);
            await output.FlushAsync();

            if (response.OutOfMemory)
                return;
        }
    }

    internal static InferenceProtocol.InferResponse Run(InferenceProtocol.InferRequest request, byte[] pixels)
    {
        if (request.Width <= 0 || request.Height <= 0 || (long)request.Width * request.Height * 4 != pixels.Length)
            return new InferenceProtocol.InferResponse(Error: $"{pixels.Length:N0} bytes are not a {request.Width}x{request.Height} BGRA image.");

        var image = new PdfImage(request.Width, request.Height, pixels);
        var models = LocalPdfModels.Instance;
        switch (request.Model)
        {
            case InferenceProtocol.Layout:
                return new InferenceProtocol.InferResponse(Regions: [.. models.Layout(image, request.Threads, CancellationToken.None)]);
            case InferenceProtocol.Table:
                var (tokens, boxes) = models.TableStructure(image, request.Threads, CancellationToken.None);
                return new InferenceProtocol.InferResponse(Tokens: [.. tokens], Boxes: boxes);
            case InferenceProtocol.Ocr:
                return new InferenceProtocol.InferResponse(Lines: [.. models.Ocr(image, request.Threads, CancellationToken.None)]);
            default:
                return new InferenceProtocol.InferResponse(Error: $"The inference host has no model named {request.Model}.");
        }
    }
}
