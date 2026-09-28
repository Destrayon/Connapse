using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Vector;
using Connapse.Storage.Data;
using Connapse.Storage.Vectors;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// A query is only comparable with vectors made by the same recipe (model plus prompts). Hybrid
/// search scores keyword-only candidates with the vector store, and that scoring has to use the
/// space the vector search used, or it fuses similarities from two different spaces.
/// </summary>
[Trait("Category", "Unit")]
public class VectorSearchModelTests
{
    private const string ContainerId = "6f1c2a44-0000-0000-0000-000000000001";

    private readonly IVectorStore _store = Substitute.For<IVectorStore>();
    private readonly IEmbeddingProvider _provider = Substitute.For<IEmbeddingProvider>();
    private readonly VectorModelDiscovery _discovery = Substitute.For<VectorModelDiscovery>(
        Substitute.For<IDbContextFactory<KnowledgeDbContext>>(), NullLogger<VectorModelDiscovery>.Instance);

    private VectorSearchService CreateService(string model = "current-model")
    {
        var embedding = Substitute.For<IOptionsMonitor<EmbeddingSettings>>();
        embedding.CurrentValue.Returns(new EmbeddingSettings { Model = model });
        _provider.EmbedAsync(Arg.Any<string>(), Arg.Any<EmbeddingInputType>(), Arg.Any<CancellationToken>())
            .Returns([1f, 0f]);
        return new VectorSearchService(
            _store, _provider, embedding, _discovery, new VectorModelCountCache(TimeProvider.System),
            NullLogger<VectorSearchService>.Instance);
    }

    private void StoredVectors(params (string ModelId, long Count)[] models) =>
        _discovery.GetModelsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(models.Select(m => new EmbeddingModelInfo(m.ModelId, 768, m.Count)).ToList());

    [Fact]
    public async Task ScoreChunksAsync_ScoresInTheQuerysSpace()
    {
        await CreateService().ScoreChunksAsync(new QueryEmbedding([1f, 0f], "legacy-model"), ["c1"]);

        await _store.Received(1).ScoreChunksAsync(
            Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), "legacy-model", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_SearchesTheQuerysSpace()
    {
        await CreateService().SearchAsync(
            "q", new QueryEmbedding([1f, 0f], "legacy-model"), new SearchOptions(), SearchScopes.Unrestricted);

        await _store.Received(1).SearchAsync(
            Arg.Any<float[]>(),
            Arg.Any<int>(),
            Arg.Is<Dictionary<string, string>?>(f => f != null && f["modelId"] == "legacy-model"),
            Arg.Any<SearchScopes>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedQueryAsync_ModelWithoutPrompts_UsesTheBareModelAndNeverCountsVectors()
    {
        QueryEmbedding embedding = await CreateService("text-embedding-3-small").EmbedQueryAsync("q", new SearchOptions());

        embedding.ModelId.Should().Be("text-embedding-3-small");
        await _provider.Received(1).EmbedAsync("q", EmbeddingInputType.Query, Arg.Any<CancellationToken>());
        await _discovery.DidNotReceiveWithAnyArgs().GetModelsAsync(default, default);
    }

    [Fact]
    public async Task EmbedQueryAsync_ModelIdFilter_EmbedsForThatSpace()
    {
        var options = new SearchOptions(Filters: new Dictionary<string, string> { ["modelId"] = "legacy-model" });

        QueryEmbedding embedding = await CreateService().EmbedQueryAsync("q", options);

        embedding.ModelId.Should().Be("legacy-model");
        await _provider.Received(1).EmbedAsync("q", EmbeddingInputType.Unspecified, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedQueryAsync_PromptedModelOverMostlyUnpromptedVectors_QueriesThemWithoutAPrompt()
    {
        string prompted = EmbeddingIdentity.For(new EmbeddingSettings { Model = "nomic-embed-text" });
        StoredVectors(("nomic-embed-text", 900), (prompted, 100));

        QueryEmbedding embedding = await CreateService("nomic-embed-text")
            .EmbedQueryAsync("q", new SearchOptions(ContainerId: ContainerId));

        embedding.ModelId.Should().Be("nomic-embed-text");
        await _provider.Received(1).EmbedAsync("q", EmbeddingInputType.Unspecified, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedQueryAsync_PromptedModelOverReindexedVectors_UsesTheQueryPrompt()
    {
        string prompted = EmbeddingIdentity.For(new EmbeddingSettings { Model = "nomic-embed-text" });
        StoredVectors(("nomic-embed-text", 100), (prompted, 900));

        QueryEmbedding embedding = await CreateService("nomic-embed-text")
            .EmbedQueryAsync("q", new SearchOptions(ContainerId: ContainerId));

        embedding.ModelId.Should().Be(prompted);
        await _provider.Received(1).EmbedAsync("q", EmbeddingInputType.Query, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedQueryAsync_PromptedModelOverEmptyContainer_UsesTheQueryPrompt()
    {
        StoredVectors();

        QueryEmbedding embedding = await CreateService("nomic-embed-text")
            .EmbedQueryAsync("q", new SearchOptions(ContainerId: ContainerId));

        embedding.ModelId.Should().Be(EmbeddingIdentity.For(new EmbeddingSettings { Model = "nomic-embed-text" }));
        await _provider.Received(1).EmbedAsync("q", EmbeddingInputType.Query, Arg.Any<CancellationToken>());
    }
}
