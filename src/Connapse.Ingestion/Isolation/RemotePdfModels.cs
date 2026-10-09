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
        Call(InferenceProtocol.Layout, page, threads).Regions ?? [];

    public (IReadOnlyList<string> Tokens, List<float[]> Boxes) TableStructure(PdfImage table, int threads, CancellationToken ct)
    {
        var response = Call(InferenceProtocol.Table, table, threads);
        return (response.Tokens ?? [], response.Boxes ?? []);
    }

    public IReadOnlyList<OcrLayout.Line> Ocr(PdfImage page, int threads, CancellationToken ct)
    {
        // A failed layout or table run costs a page its structure; a failed OCR run costs it its
        // text. OCR's models are small enough to run here when the shared host could not.
        try
        {
            return Call(InferenceProtocol.Ocr, page, threads).Lines ?? [];
        }
        catch (InferenceFailedException)
        {
            return LocalPdfModels.Instance.Ocr(page, threads, ct);
        }
    }

    private InferenceProtocol.InferResponse Call(string model, PdfImage image, int threads)
    {
        WriteFrame(output, Serialize(new ParseResponse(null, null, null, Infer: new InferRequest(model, image.Width, image.Height, threads))));
        WriteFrame(output, image.Bgra);
        output.Flush();

        var response = Deserialize<InferenceProtocol.InferResponse>(ReadFrame(input, InferenceProtocol.MaxJsonFrame));
        if (response.OutOfMemory)
            throw new InferenceFailedException("the shared inference process ran out of memory");
        if (response.Error is { } error)
            throw new InferenceFailedException(error);
        return response;
    }
}

/// <summary>The shared inference host could not run a model; the parser reads the page another way.</summary>
internal sealed class InferenceFailedException(string message) : Exception(message);
