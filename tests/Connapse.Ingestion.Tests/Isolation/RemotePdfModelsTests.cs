using Connapse.Ingestion.Isolation;
using Connapse.Ingestion.Parsers;
using FluentAssertions;
using static Connapse.Ingestion.Isolation.ParserProtocol;

namespace Connapse.Ingestion.Tests.Isolation;

// #680 review: how a parser host's model calls fail when the shared inference host can't run them.
[Trait("Category", "Unit")]
public class RemotePdfModelsTests
{
    private static readonly PdfImage Page = new(PdfLayout.InputSide, PdfLayout.InputSide, new byte[PdfLayout.InputSide * PdfLayout.InputSide * 4]);

    private static MemoryStream Replies(params InferenceProtocol.InferResponse[] responses)
    {
        var stream = new MemoryStream();
        foreach (var response in responses)
            WriteFrame(stream, Serialize(response));
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void LayoutOutOfMemory_EndsTheParseAsOutOfMemory_AndLaterPagesDoNotAskAgain()
    {
        var output = new MemoryStream();
        var models = new RemotePdfModels(Replies(new InferenceProtocol.InferResponse(OutOfMemory: true)), output);

        Action first = () => models.Layout(Page, 1, CancellationToken.None);
        first.Should().Throw<OutOfMemoryException>("the pipeline reads the document again without the layout model (#676)");

        long written = output.Length;
        Action next = () => models.TableStructure(Page, 1, CancellationToken.None);
        next.Should().Throw<InferenceFailedException>();
        output.Length.Should().Be(written, "nothing more is sent to a host that ran out on this file");
    }

    [Fact]
    public void OcrThatFailsRemotely_RunsInTheParserHost()
    {
        var models = new RemotePdfModels(Replies(
            new InferenceProtocol.InferResponse(OutOfMemory: true),
            new InferenceProtocol.InferResponse(Error: "crashed")), new MemoryStream());

        models.Ocr(Page, 1, CancellationToken.None).Should().BeEmpty("a blank page has no text, but it was read");
        models.Ocr(Page, 1, CancellationToken.None).Should().BeEmpty();
    }

    [Fact]
    public void ImageLargerThanTheHostTakes_FailsThePageWithoutSendingIt()
    {
        var output = new MemoryStream();
        var models = new RemotePdfModels(new MemoryStream(), output);
        int side = PdfOcr.MaxRenderedSide + 1;
        var huge = new PdfImage(side, side, new byte[side * side * 4]);

        Action act = () => models.TableStructure(huge, 1, CancellationToken.None);

        act.Should().Throw<InferenceFailedException>();
        output.Length.Should().Be(0);
    }
}
