using Connapse.Ingestion.Pipeline;
using Connapse.Ingestion.Reindex;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Pipeline;

// Codex on #677: a PDF read without the layout model is read again only once parsers get more memory
// than it had, so reindexing converges while memory stays the same.
[Trait("Category", "Unit")]
public class ReadWithoutLayoutReindexTests
{
    [Theory]
    // stored MB, parsers' MB now -> read again
    [InlineData("512", 1024, true)]
    [InlineData("1024", 1024, false)]
    [InlineData("1024", 512, false)]
    [InlineData(null, 4096, false)]
    [InlineData("512", null, false)]
    [InlineData("memory", 4096, false)]
    public void MoreMemoryThanItWasReadWith_OnlyWhenParsersNowGetMore(string? storedMb, int? parserMb, bool expected)
    {
        var metadata = new Dictionary<string, string>();
        if (storedMb is not null)
            metadata[IngestionPipeline.MetadataKeyReadWithoutLayout] = storedMb;

        ReindexService.MoreMemoryThanItWasReadWith(metadata, parserMb).Should().Be(expected);
    }
}
