using System.IO.Compression;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Parsers;
using Connapse.Ingestion.Pipeline;
using Connapse.Ingestion.Validation;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Validation;

[Trait("Category", "Unit")]
public class ParseLimitsTests
{
    private static byte[] Zip(params (string Name, byte[] Body, CompressionLevel Level)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body, level) in entries)
            {
                using var entry = zip.CreateEntry(name, level).Open();
                entry.Write(body);
            }
        }
        return stream.ToArray();
    }

    [Fact]
    public void CheckInput_FileOverTheSizeLimit_IsRefused()
    {
        using var content = new MemoryStream(new byte[2048]);

        ParseLimits.CheckInput(content, ".txt", new UploadSettings { MaxFileBytes = 1024 })
            .Should().EndWith("[file_too_large]");
    }

    [Fact]
    public void CheckInput_OrdinaryDocx_IsAccepted()
    {
        using var content = new MemoryStream(Zip(("word/document.xml", Encoding.UTF8.GetBytes("<w:document/>"), CompressionLevel.Optimal)));

        ParseLimits.CheckInput(content, ".docx", new UploadSettings()).Should().BeNull();
        content.Position.Should().Be(0, "the parser reads the stream next");
    }

    [Fact]
    public void CheckInput_ZipBomb_IsRefusedByItsRatio()
    {
        // 20 MiB of one repeated byte compresses to a few kilobytes: far past 100:1.
        using var content = new MemoryStream(Zip(("word/document.xml", new byte[20 * 1024 * 1024], CompressionLevel.SmallestSize)));

        ParseLimits.CheckInput(content, ".docx", new UploadSettings()).Should().EndWith("[decompressed_too_large]");
    }

    [Fact]
    public void CheckInput_PackageOverTheInflatedLimit_IsRefused()
    {
        using var content = new MemoryStream(Zip(("ppt/media/clip.bin", new byte[4096], CompressionLevel.NoCompression)));

        ParseLimits.CheckInput(content, ".pptx", new UploadSettings { MaxDecompressedBytes = 1024 })
            .Should().EndWith("[decompressed_too_large]");
    }

    [Fact]
    public void CheckInput_DocxThatIsNotAZip_IsLeftToTheParser()
    {
        using var content = new MemoryStream("plain text"u8.ToArray());

        ParseLimits.CheckInput(content, ".docx", new UploadSettings()).Should().BeNull();
    }

    [Fact]
    public async Task ParseWithDeadline_ParserThatNeverReturns_FailsWithParseTimeout()
    {
        // A parser stuck in a synchronous loop that ignores its token, like PdfPig on a
        // malformed content stream.
        var release = new ManualResetEventSlim();
        var parser = Substitute.For<IDocumentParser>();
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { release.Wait(TimeSpan.FromSeconds(30)); return new ParsedDocument("", [], []); });

        var act = () => IngestionPipeline.ParseWithDeadlineAsync(
            parser, new MemoryStream(), "stuck.pdf", TimeSpan.FromMilliseconds(200), CancellationToken.None);

        (await act.Should().ThrowAsync<PermanentIngestionException>()).Which.Message.Should().EndWith("[parse_timeout]");
        IngestionPipeline.AbandonedParses.Should().BeGreaterThan(0, "the stuck parse is still running and must be visible");

        release.Set();
        await WaitUntilAsync(() => IngestionPipeline.AbandonedParses == 0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        condition().Should().BeTrue();
    }

    [Fact]
    public void CheckInput_PackageListingTooManyEntries_IsRefusedBeforeItsDirectoryIsRead()
    {
        var entries = Enumerable.Range(0, 12).Select(i => ($"part{i}.xml", new byte[1], CompressionLevel.NoCompression)).ToArray();
        using var content = new MemoryStream(Zip(entries));

        ParseLimits.ReadDeclaredEntryCount(content).Should().Be(12);
        ParseLimits.CheckInput(content, ".docx", new UploadSettings { MaxZipEntries = 10 })
            .Should().EndWith("[too_many_entries]");
    }

    [Fact]
    public async Task CopyBounded_StreamOverTheLimit_FailsWhileCopying()
    {
        // A connector stream of unknown length: refused once it passes the cap, not after buffering.
        using var source = new NonSeekable(new byte[3000]);
        using var destination = new MemoryStream();

        var act = () => IngestionPipeline.CopyBoundedAsync(source, destination, 1024, "big.pdf", CancellationToken.None);

        (await act.Should().ThrowAsync<PermanentIngestionException>()).Which.Message.Should().EndWith("[file_too_large]");
        destination.Length.Should().BeLessThanOrEqualTo(1024);
    }

    [Fact]
    public async Task CopyBounded_StreamWithinTheLimit_IsCopiedWhole()
    {
        using var source = new NonSeekable(new byte[1000]);
        using var destination = new MemoryStream();

        await IngestionPipeline.CopyBoundedAsync(source, destination, 1024, "small.pdf", CancellationToken.None);

        destination.Length.Should().Be(1000);
    }

    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task ParseWithDeadline_CallerCancels_IsCancellationNotATimeout()
    {
        var parser = Substitute.For<IDocumentParser>();
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()).ContinueWith(_ => new ParsedDocument("", [], [])));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => IngestionPipeline.ParseWithDeadlineAsync(
            parser, new MemoryStream(), "a.txt", TimeSpan.FromSeconds(30), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PdfParser_OverThePageLimit_FailsPermanently()
    {
        var limits = Substitute.For<IOptionsMonitor<UploadSettings>>();
        limits.CurrentValue.Returns(new UploadSettings { MaxPdfPages = 1 });
        using var pdf = new MemoryStream(TwoPagePdf());

        var act = () => new PdfParser(limits).ParseAsync(pdf, "two.pdf");

        (await act.Should().ThrowAsync<PermanentIngestionException>()).Which.Message.Should().EndWith("[too_many_pages]");
    }

    /// <summary>A minimal two-page PDF with no content streams.</summary>
    private static byte[] TwoPagePdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        };
        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(body.ToString()));
            body.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(body.ToString());
        body.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) body.Append($"{offset:D10} 00000 n \n");
        body.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(body.ToString());
    }
}
