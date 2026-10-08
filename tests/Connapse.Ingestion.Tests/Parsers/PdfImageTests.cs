using Connapse.Ingestion.Parsers;
using FluentAssertions;
using SkiaSharp;

namespace Connapse.Ingestion.Tests.Parsers;

// #680: the pixels model inference sees cross a process boundary as PdfImage, so the trip has to
// leave them exactly as rendered.
[Trait("Category", "Unit")]
public class PdfImageTests
{
    [Theory]
    [InlineData(SKColorType.Bgra8888)]
    [InlineData(SKColorType.Rgba8888)]
    public void From_ThenToBitmap_KeepsEveryPixel(SKColorType colorType)
    {
        using var source = new SKBitmap(new SKImageInfo(37, 11, colorType, SKAlphaType.Premul));
        var random = new Random(680);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                source.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255));

        PdfImage image = PdfImage.From(source);
        using SKBitmap back = image.ToBitmap();

        image.Bgra.Should().HaveCount(37 * 11 * 4);
        back.ColorType.Should().Be(SKColorType.Bgra8888);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                back.GetPixel(x, y).Should().Be(source.GetPixel(x, y), $"pixel ({x}, {y})");
    }

    [Fact]
    public void From_PacksRowsWithoutPadding()
    {
        using var source = new SKBitmap(new SKImageInfo(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        source.SetPixel(2, 1, new SKColor(10, 20, 30, 255)); // R, G, B

        PdfImage image = PdfImage.From(source);

        int p = (1 * 3 + 2) * 4;
        image.Bgra[p..(p + 4)].Should().Equal(30, 20, 10, 255);
    }
}
