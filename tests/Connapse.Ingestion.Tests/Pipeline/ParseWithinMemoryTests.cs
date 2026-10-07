using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Isolation;
using Connapse.Ingestion.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connapse.Ingestion.Tests.Pipeline;

// #676: a PDF the parser can't read with the layout model in its memory is read without it, not failed.
[Trait("Category", "Unit")]
public class ParseWithinMemoryTests
{
    private static readonly UploadSettings Layout = new() { PdfTextMode = "Layout" };

    private sealed class Parser(Func<UploadSettings, ParsedDocument> parse)
    {
        public List<UploadSettings> Calls { get; } = [];

        public Task<ParsedDocument> ParseAsync(UploadSettings settings)
        {
            Calls.Add(settings);
            return Task.FromResult(parse(settings));
        }
    }

    private static ParsedDocument Doc(string content) => new(content, new Dictionary<string, string>(), []);

    private static Task<ParsedDocument> Run(Parser parser, string fileName, int hostMb, UploadSettings? settings = null) =>
        IngestionPipeline.ParseWithinMemoryAsync(parser.ParseAsync, settings ?? Layout, hostMb, fileName, NullLogger.Instance);

    [Fact]
    public async Task EnoughMemory_ParsesByLayoutOnce()
    {
        var parser = new Parser(_ => Doc("laid out"));

        ParsedDocument result = await Run(parser, "a.pdf", ParserProcessPool.LayoutHostMb);

        result.Content.Should().Be("laid out");
        parser.Calls.Should().ContainSingle().Which.PdfTextMode.Should().Be("Layout");
        result.Warnings.Should().BeEmpty();
        result.Metadata.Should().NotContainKey(IngestionPipeline.MetadataKeyReadWithoutLayout);
    }

    [Fact]
    public async Task HostBelowWhatLayoutNeeds_ReadsWithoutTheModelUpFront()
    {
        var parser = new Parser(s => Doc(s.PdfTextMode));

        ParsedDocument result = await Run(parser, "a.pdf", 512);

        parser.Calls.Should().ContainSingle();
        parser.Calls[0].PdfTextMode.Should().Be("ContentOrder");
        parser.Calls[0].PdfLayoutOnOcrPages.Should().BeFalse();
        result.Warnings.Should().ContainSingle().Which.Should().Contain("512 MB").And.Contain("without the layout model");
        result.Metadata[IngestionPipeline.MetadataKeyReadWithoutLayout].Should().Be("512");
    }

    [Fact]
    public async Task LayoutParseRunsOutOfMemory_IsReadAgainWithoutTheModel()
    {
        var parser = new Parser(s => s.PdfTextMode == "Layout"
            ? throw new PermanentIngestionException("Could not parse a.pdf: it needed more than the 900 MB the parser may use [parse_out_of_memory]")
            : Doc("content order"));

        ParsedDocument result = await Run(parser, "a.pdf", 1024);

        result.Content.Should().Be("content order");
        parser.Calls.Select(c => c.PdfTextMode).Should().Equal("Layout", "ContentOrder");
        result.Warnings.Should().ContainSingle().Which.Should().Contain("ran out");
        result.Metadata[IngestionPipeline.MetadataKeyReadWithoutLayout].Should().Be("1024");
    }

    [Fact]
    public async Task ContentOrderAlsoRunsOutOfMemory_Fails()
    {
        var parser = new Parser(_ => throw new PermanentIngestionException("x [parse_out_of_memory]"));

        Func<Task> act = () => Run(parser, "a.pdf", 1024);

        await act.Should().ThrowAsync<PermanentIngestionException>();
        parser.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task OtherFailure_IsNotRetried()
    {
        var parser = new Parser(_ => throw new PermanentIngestionException("x [parse_timeout]"));

        Func<Task> act = () => Run(parser, "a.pdf", 1024);

        await act.Should().ThrowAsync<PermanentIngestionException>().WithMessage("*parse_timeout*");
        parser.Calls.Should().ContainSingle();
    }

    [Theory]
    [InlineData("a.docx", "Layout")]
    [InlineData("a.pdf", "ContentOrder")]
    public async Task NotALayoutPdf_PassesThroughUnchanged(string fileName, string mode)
    {
        var parser = new Parser(_ => Doc("text"));
        UploadSettings settings = new() { PdfTextMode = mode };

        ParsedDocument result = await Run(parser, fileName, 256, settings);

        parser.Calls.Should().ContainSingle().Which.Should().BeSameAs(settings);
        result.Warnings.Should().BeEmpty();
    }
}
