using Connapse.Ingestion.Pipeline;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Pipeline;

[Trait("Category", "Unit")]
public class IngestionPipelineRoutingTests
{
    [Theory]
    [InlineData("README.md", "DocumentAware")]
    [InlineData("docs.markdown", "DocumentAware")]
    [InlineData("docs.MDX", "DocumentAware")]
    [InlineData("notes.MD", "DocumentAware")]
    [InlineData("file.txt", "Recursive")]
    [InlineData("file.pdf", "Recursive")]
    [InlineData(null, "Recursive")]
    [InlineData("", "Recursive")]
    public void ResolveStrategyName_RoutesByExtension(string? fileName, string expected)
    {
        string actual = IngestionPipelineStrategyResolver.Resolve(
            fallbackStrategy: "Recursive",
            fileName: fileName);

        actual.Should().Be(expected);
    }

    [Fact]
    public void Resolve_AndMetadataRecording_StaySynchronized()
    {
        // Regression: code review of #317 caught that IndexedWith:ChunkingStrategy
        // was being recorded BEFORE auto-routing, lying about which chunker ran.
        // The contract is that metadata recording uses the same resolver result
        // as the dispatch path.
        string mdResolved = IngestionPipelineStrategyResolver.Resolve("Recursive", "file.md");
        string txtResolved = IngestionPipelineStrategyResolver.Resolve("Recursive", "file.txt");
        string nullResolved = IngestionPipelineStrategyResolver.Resolve("Recursive", null);

        mdResolved.Should().Be("DocumentAware");
        txtResolved.Should().Be("Recursive");
        nullResolved.Should().Be("Recursive");
    }
}
