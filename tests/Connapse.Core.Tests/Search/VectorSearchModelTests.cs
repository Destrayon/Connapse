using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Vector;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// Hybrid search scores keyword-only candidates with the vector store; that scoring has to use the
/// same model the vector search used, or it fuses similarities from two different models.
/// </summary>
[Trait("Category", "Unit")]
public class VectorSearchModelTests
{
    private readonly IVectorStore _store = Substitute.For<IVectorStore>();

    private VectorSearchService CreateService()
    {
        var embedding = Substitute.For<IOptionsMonitor<EmbeddingSettings>>();
        embedding.CurrentValue.Returns(new EmbeddingSettings { Model = "current-model" });
        return new VectorSearchService(
            _store, Substitute.For<IEmbeddingProvider>(), embedding, NullLogger<VectorSearchService>.Instance);
    }

    [Fact]
    public async Task ScoreChunksAsync_ModelIdFilter_ScoresWithThatModel()
    {
        var options = new SearchOptions(Filters: new Dictionary<string, string> { ["modelId"] = "legacy-model" });

        await CreateService().ScoreChunksAsync([1f, 0f], ["c1"], options);

        await _store.Received(1).ScoreChunksAsync(
            Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), "legacy-model", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScoreChunksAsync_NoModelFilter_ScoresWithTheCurrentModel()
    {
        await CreateService().ScoreChunksAsync([1f, 0f], ["c1"], new SearchOptions());

        await _store.Received(1).ScoreChunksAsync(
            Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), "current-model", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_ModelIdFilter_SearchesWithThatModel()
    {
        var options = new SearchOptions(Filters: new Dictionary<string, string> { ["modelId"] = "legacy-model" });

        await CreateService().SearchAsync("q", [1f, 0f], options, SearchScopes.Unrestricted);

        await _store.Received(1).SearchAsync(
            Arg.Any<float[]>(),
            Arg.Any<int>(),
            Arg.Is<Dictionary<string, string>?>(f => f != null && f["modelId"] == "legacy-model"),
            Arg.Any<SearchScopes>(),
            Arg.Any<CancellationToken>());
    }
}
