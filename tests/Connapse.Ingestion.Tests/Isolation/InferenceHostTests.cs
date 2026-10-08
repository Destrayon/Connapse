using System.Diagnostics;
using Connapse.Core;
using Connapse.Ingestion.Isolation;
using Connapse.Ingestion.Parsers;
using Connapse.Ingestion.Tests.Parsers;
using FluentAssertions;
using PDFtoImage;
using SkiaSharp;

namespace Connapse.Ingestion.Tests.Isolation;

// #680: models run in the shared inference host must read pixels exactly as they do in process.
[Trait("Category", "Integration")]
public sealed class InferenceHostTests : IDisposable
{
    private static readonly string TestHostPath = Path.Combine(AppContext.BaseDirectory, "Connapse.ParserHost.TestHost.dll");
    private static readonly UploadSettings Settings = new();

    private readonly ParserProcessPool _pool = new(hostPath: TestHostPath);

    public void Dispose() => _pool.Dispose();

    private static PdfImage Render(byte[] pdf, RenderOptions options)
    {
        using SKBitmap bitmap = Conversion.ToImage(pdf, 0, options: options);
        return PdfImage.From(bitmap);
    }

    private Task<InferenceProtocol.InferResponse> Infer(string model, PdfImage image) =>
        _pool.InferAsync(new ParserProtocol.InferRequest(model, image.Width, image.Height, Threads: 1), image.Bgra, Settings, CancellationToken.None);

    [Fact]
    public async Task Layout_MatchesTheModelInProcess()
    {
        PdfImage page = Render(TestPdf.Build(PdfLayoutTests.TwoColumnPage()),
            new RenderOptions(Width: PdfLayout.InputSide, Height: PdfLayout.InputSide, WithAspectRatio: false));

        var response = await Infer(InferenceProtocol.Layout, page);

        response.Error.Should().BeNull();
        response.Regions.Should().NotBeEmpty().And.Equal(LocalPdfModels.Instance.Layout(page, 1, CancellationToken.None));
    }

    [Fact]
    public async Task Table_MatchesTheModelInProcess()
    {
        PdfImage region = Render(TestPdf.Build(PdfLayoutTests.TwoColumnPage()), new RenderOptions(Dpi: 72));

        var response = await Infer(InferenceProtocol.Table, region);

        var (tokens, boxes) = LocalPdfModels.Instance.TableStructure(region, 1, CancellationToken.None);
        response.Error.Should().BeNull();
        response.Tokens.Should().Equal(tokens);
        response.Boxes.Should().HaveCount(boxes.Count);
        for (int i = 0; i < boxes.Count; i++)
            response.Boxes![i].Should().Equal(boxes[i]);
    }

    [Fact]
    public async Task Ocr_MatchesTheModelInProcess()
    {
        PdfImage page = Render(TestScanPdf.Build(150, PdfLayoutTests.TwoColumnPage()), new RenderOptions(Dpi: 150));

        var response = await Infer(InferenceProtocol.Ocr, page);

        response.Error.Should().BeNull();
        response.Lines.Should().NotBeEmpty().And.Equal(LocalPdfModels.Instance.Ocr(page, 1, CancellationToken.None));
    }

    [Fact]
    public async Task PixelsThatDoNotFitTheSize_AreAnErrorNotACrash()
    {
        var response = await _pool.InferAsync(new ParserProtocol.InferRequest(InferenceProtocol.Layout, 800, 800, 1),
            new byte[100], Settings, CancellationToken.None);

        response.Error.Should().Contain("800x800");
        _pool.InferenceProcessId.Should().NotBeNull("a bad request leaves the host running");
    }

    [Fact]
    public async Task HostKilledBetweenRequests_IsReplacedOnTheNext()
    {
        PdfImage page = Render(TestPdf.Build(PdfLayoutTests.TwoColumnPage()),
            new RenderOptions(Width: PdfLayout.InputSide, Height: PdfLayout.InputSide, WithAspectRatio: false));
        (await Infer(InferenceProtocol.Layout, page)).Error.Should().BeNull();
        int first = _pool.InferenceProcessId!.Value;

        using (var process = Process.GetProcessById(first))
        {
            process.Kill();
            process.WaitForExit();
        }

        var response = await Infer(InferenceProtocol.Layout, page);

        response.Error.Should().BeNull();
        response.Regions.Should().NotBeEmpty();
        _pool.InferenceProcessId.Should().NotBe(first);
    }
}
