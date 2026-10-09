using Connapse.Ingestion.Parsers;
using static Connapse.Ingestion.Isolation.ParserProtocol;

namespace Connapse.Ingestion.Isolation;

/// <summary>
/// The PDF models as a parser host reaches them when the pool shares an inference host (#680):
/// each run goes up the host's stdout, mid-parse, and its result comes back on stdin. The parse
/// loop is not reading stdin while a file is parsed, so the streams are this class's until it ends.
/// </summary>
internal sealed class RemotePdfModels(Stream input, Stream output) : IPdfModels
{
    public IReadOnlyList<PdfLayout.Region> Layout(PdfImage page, int threads, CancellationToken ct) =>
        Succeeded(Call(InferenceProtocol.Layout, page, threads)).Regions ?? [];

    public (IReadOnlyList<string> Tokens, List<float[]> Boxes) TableStructure(PdfImage table, int threads, CancellationToken ct)
    {
        var response = Succeeded(Call(InferenceProtocol.Table, table, threads));
        return (response.Tokens ?? [], response.Boxes ?? []);
    }

    public IReadOnlyList<OcrLayout.Line> Ocr(PdfImage page, int threads, CancellationToken ct)
    {
        // A failed layout or table run costs a page its structure; a failed OCR run costs it its
        // text. OCR's models are small enough to run here when the shared host could not -- out of
        // memory included, which is not raised: it would mark this whole parse as out of memory.
        var response = Call(InferenceProtocol.Ocr, page, threads);
        return response.OutOfMemory || response.Error is not null
            ? LocalPdfModels.Instance.Ocr(page, threads, ct)
            : response.Lines ?? [];
    }

    /// <summary>
    /// The response, if the model ran. Out of memory in the shared host is the document's to answer
    /// for (#676): raised as such, it ends this parse as out of memory, and the pipeline reads the
    /// document again without the layout model and records that it did. Any other failure costs only
    /// the page, which the parser reads another way.
    /// </summary>
    private InferenceProtocol.InferResponse Succeeded(InferenceProtocol.InferResponse response)
    {
        if (response.OutOfMemory)
        {
            _outOfMemory = true;
            throw new OutOfMemoryException("the shared inference process ran out of memory");
        }
        if (response.Error is { } error)
            throw new InferenceFailedException(error);
        return response;
    }

    private bool _outOfMemory;

    private InferenceProtocol.InferResponse Call(string model, PdfImage image, int threads)
    {
        // This file is read again without the layout model once it ends; its other pages need not
        // restart the shared host only to run out again.
        if (_outOfMemory && model != InferenceProtocol.Ocr)
            return new InferenceProtocol.InferResponse(Error: "the shared inference process ran out of memory earlier in this file");

        // Refused here, before a frame is written, so the parent never sees an image it would reject
        // and the parse stays in step.
        if (image.Bgra.Length > InferenceProtocol.MaxPixelFrame)
            return new InferenceProtocol.InferResponse(Error: $"a {image.Width}x{image.Height} image is larger than the inference host takes");

        WriteFrame(output, Serialize(new ParseResponse(null, null, null, Infer: new InferRequest(model, image.Width, image.Height, threads))));
        WriteFrame(output, image.Bgra);
        output.Flush();

        return Deserialize<InferenceProtocol.InferResponse>(ReadFrame(input, InferenceProtocol.MaxJsonFrame));
    }
}

/// <summary>The shared inference host could not run a model; the parser reads the page another way.</summary>
internal sealed class InferenceFailedException(string message) : Exception(message);
