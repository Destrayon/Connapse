using Connapse.Storage.Llm;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.Llm;

[Trait("Category", "Unit")]
public class SummaryPromptsTests
{
    [Fact]
    public void PerDocPrompt_ContainsAgentSteeringInstruction()
    {
        SummaryPrompts.PerDocSystemPrompt.Should().Contain("AI agent, not a human");
        SummaryPrompts.PerDocSystemPrompt.Should().Contain("search_knowledge");
        SummaryPrompts.PerDocSystemPrompt.Should().Contain("truncated");
    }

    [Fact]
    public void ContainerRollupPrompt_ContainsStructureSections()
    {
        SummaryPrompts.ContainerRollupSystemPrompt.Should().Contain("Use this container for");
        SummaryPrompts.ContainerRollupSystemPrompt.Should().Contain("Scope boundaries");
        SummaryPrompts.ContainerRollupSystemPrompt.Should().Contain("Query hints");
    }

    [Fact]
    public void RenderPerDocUserMessage_IncludesFilenameAndText()
    {
        string rendered = SummaryPrompts.RenderPerDocUserMessage(
            filename: "earnings.pdf",
            mimeType: "application/pdf",
            firstNTokens: "Apple Q3 2025 earnings...",
            wasTruncated: false);
        rendered.Should().Contain("earnings.pdf");
        rendered.Should().Contain("application/pdf");
        rendered.Should().Contain("Apple Q3 2025 earnings...");
        rendered.Should().Contain("full document text is shown below");
    }

    [Fact]
    public void RenderPerDocUserMessage_WhenTruncated_SignalsHeadOnly()
    {
        string rendered = SummaryPrompts.RenderPerDocUserMessage(
            filename: "long.pdf",
            mimeType: "application/pdf",
            firstNTokens: "opening section...",
            wasTruncated: true);
        rendered.Should().Contain("only the HEAD");
        rendered.Should().NotContain("full document text is shown below");
    }

    [Fact]
    public void RenderContainerRollupUserMessage_StuffRegime_IncludesAllSummaries()
    {
        string rendered = SummaryPrompts.RenderContainerRollupUserMessage(
            containerName: "Apple research",
            totalDocs: 5,
            isClustered: false,
            summaries: new[] { "doc1", "doc2", "doc3", "doc4", "doc5" });
        rendered.Should().Contain("Apple research");
        rendered.Should().Contain("5 documents total");
        rendered.Should().Contain("doc1");
        rendered.Should().Contain("doc5");
        rendered.Should().NotContain("cluster medoids");
    }

    [Fact]
    public void RenderContainerRollupUserMessage_ClusterRegime_IncludesClusterSizes()
    {
        string rendered = SummaryPrompts.RenderContainerRollupUserMessage(
            containerName: "Apple research",
            totalDocs: 100,
            isClustered: true,
            summaries: new[] {
                "(represents 47 similar docs): doc_a",
                "(represents 32 similar docs): doc_b"
            });
        rendered.Should().Contain("100 documents total");
        rendered.Should().Contain("cluster medoids");
        rendered.Should().Contain("represents 47 similar docs");
    }

    [Fact]
    public void RenderContainerRollupUserMessage_FewerSummariesThanTotal_SaysSampleNotAll()
    {
        string rendered = SummaryPrompts.RenderContainerRollupUserMessage(
            containerName: "Big folder",
            totalDocs: 50_000,
            isClustered: false,
            summaries: new[] { "doc1", "doc2" });
        rendered.Should().Contain("50000 documents total");
        rendered.Should().Contain("representative SAMPLE of 2 documents drawn from across the whole container");
        rendered.Should().NotContain("all documents are shown");
    }

    [Fact]
    public void RenderContainerRollupUserMessage_SummariesEqualTotal_SaysAllShown()
    {
        string rendered = SummaryPrompts.RenderContainerRollupUserMessage(
            containerName: "Small folder",
            totalDocs: 2,
            isClustered: false,
            summaries: new[] { "doc1", "doc2" });
        rendered.Should().Contain("2 documents total; all documents are shown below");
        rendered.Should().NotContain("SAMPLE");
    }
}
