using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Summarization;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Connapse.Ingestion.Tests.Summarization;

[Trait("Category", "Unit")]
public class ContainerSummarizerTests
{
    private static List<DocumentWithSummary> MakeDocs(int n)
    {
        Random rng = new(1);
        return Enumerable.Range(0, n).Select(i =>
            new DocumentWithSummary(Guid.NewGuid(), $"doc {i} summary", RandomVec(rng, 8))).ToList();
    }

    private static float[] RandomVec(Random rng, int dim) =>
        Enumerable.Range(0, dim).Select(_ => (float)rng.NextDouble()).ToArray();

    [Fact]
    public async Task GenerateAsync_StuffRegime_When_N_LessThanOrEqual_30()
    {
        ILlmProvider llm = Substitute.For<ILlmProvider>();
        ITokenCounter tc = Substitute.For<ITokenCounter>();
        llm.ModelId.Returns("claude-haiku-4-5");
        llm.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<LlmCompletionOptions?>(), Arg.Any<CancellationToken>())
            .Returns("container summary");
        tc.CountTokens(Arg.Any<string>()).Returns(3000, 500);

        ContainerSummarizer subject = new(llm, tc);
        ContainerSummarizationResult result =
            await subject.GenerateAsync("Test Container", MakeDocs(30));

        result.Regime.Should().Be("stuff");
        result.NumDocs.Should().Be(30);
        result.KClusters.Should().BeNull();
        result.Summary.Should().Be("container summary");
        result.Model.Should().Be("claude-haiku-4-5");
    }

    [Fact]
    public async Task GenerateAsync_ClusterRegime_When_N_GreaterThan_30()
    {
        ILlmProvider llm = Substitute.For<ILlmProvider>();
        ITokenCounter tc = Substitute.For<ITokenCounter>();
        llm.ModelId.Returns("claude-haiku-4-5");
        llm.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<LlmCompletionOptions?>(), Arg.Any<CancellationToken>())
            .Returns("container summary");
        tc.CountTokens(Arg.Any<string>()).Returns(3000, 500);

        ContainerSummarizer subject = new(llm, tc);
        ContainerSummarizationResult result =
            await subject.GenerateAsync("Big Container", MakeDocs(150));

        result.Regime.Should().Be("cluster");
        result.NumDocs.Should().Be(150);
        result.KClusters.Should().NotBeNull();
        result.KClusters.Should().BeLessThanOrEqualTo(20);
    }

    [Fact]
    public async Task GenerateAsync_NoProvider_ReturnsSkipped()
    {
        ITokenCounter tc = Substitute.For<ITokenCounter>();
        ContainerSummarizer subject = new(llmProvider: null, tc);
        ContainerSummarizationResult result =
            await subject.GenerateAsync("Test", MakeDocs(5));
        result.Skipped.Should().BeTrue();
        result.SkipReason.Should().Be("no_provider_configured");
    }

    [Fact]
    public async Task GenerateAsync_NoDocs_ReturnsSkipped()
    {
        ILlmProvider llm = Substitute.For<ILlmProvider>();
        ITokenCounter tc = Substitute.For<ITokenCounter>();
        ContainerSummarizer subject = new(llm, tc);
        ContainerSummarizationResult result =
            await subject.GenerateAsync("Empty", new List<DocumentWithSummary>());
        result.Skipped.Should().BeTrue();
        result.SkipReason.Should().Be("no_documents");
    }
}
