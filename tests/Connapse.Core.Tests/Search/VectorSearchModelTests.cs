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
/// A query is only comparable with vectors made by the same recipe (model plus text preparation).
/// While a container holds vectors under several recipes of one model, each space is searched with
/// a query prepared its own way; hybrid pool scoring must use the same spaces.
/// </summary>
[Trait("Category", "Unit")]
public class VectorSearchModelTests
{
    private const string ContainerId = "6f1c2a44-0000-0000-0000-000000000001";
    private static readonly string Nomic = EmbeddingIdentity.For(new EmbeddingSettings { Model = "nomic-embed-text" });

    private readonly IVectorStore _store = Substitute.For<IVectorStore>();
    private readonly IEmbeddingProvider _provider = Substitute.For<IEmbeddingProvider>();
    private readonly VectorModelDiscovery _discovery = Substitute.For<VectorModelDiscovery>(
        Substitute.For<IDbContextFactory<KnowledgeDbContext>>(), NullLogger<VectorModelDiscovery>.Instance);

    private VectorSearchService CreateService(EmbeddingSettings settings)
    {
        var embedding = Substitute.For<IOptionsMonitor<EmbeddingSettings>>();
        embedding.CurrentValue.Returns(settings);
        _provider.EmbedAsync(Arg.Any<string>(), Arg.Any<EmbeddingInputType>(), Arg.Any<CancellationToken>())
            .Returns([1f, 0f]);
        return new VectorSearchService(
            _store, _provider, embedding, _discovery, new VectorModelCountCache(TimeProvider.System),
            NullLogger<VectorSearchService>.Instance);
    }

    private VectorSearchService CreateService(string model = "nomic-embed-text") => CreateService(new EmbeddingSettings { Model = model });

    private void StoredVectors(params string[] modelIds) =>
        _discovery.GetModelsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(modelIds.Select(m => new EmbeddingModelInfo(m, 768, 10)).ToList());

    private static SearchOptions InContainer() => new(ContainerId: ContainerId);

    [Fact]
    public async Task EmbedQueryAsync_ModelWithoutTextPreparation_OneSpaceAndNoVectorCount()
    {
        QueryEmbedding embedding = await CreateService("text-embedding-3-small").EmbedQueryAsync("q", InContainer());

        embedding.Spaces.Select(s => s.ModelId).Should().Equal("text-embedding-3-small");
        await _provider.Received(1).EmbedAsync("q", EmbeddingInputType.Query, Arg.Any<CancellationToken>());
        await _discovery.DidNotReceiveWithAnyArgs().GetModelsAsync(default, default);
    }

    [Fact]
    public async Task EmbedQueryAsync_ContainerMidReindex_SearchesBothSpacesEachWithItsOwnQuery()
    {
        StoredVectors("nomic-embed-text", Nomic);

        QueryEmbedding embedding = await CreateService().EmbedQueryAsync("Q", InContainer());

        embedding.Spaces.Select(s => s.ModelId).Should().Equal(Nomic, "nomic-embed-text");
        await _provider.Received(1).EmbedAsync("Q", EmbeddingInputType.Query, Arg.Any<CancellationToken>());
        await _provider.Received(1).EmbedAsync("Q", EmbeddingInputType.Unspecified, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedQueryAsync_ContainerFullyReindexed_SearchesOnlyTheCurrentSpace()
    {
        StoredVectors(Nomic);

        QueryEmbedding embedding = await CreateService().EmbedQueryAsync("q", InContainer());

        embedding.Spaces.Select(s => s.ModelId).Should().Equal(Nomic);
    }

    [Fact]
    public async Task EmbedQueryAsync_CustomPrefixes_StillSearchVectorsMadeWithTheModelsOwnPrefixes()
    {
        EmbeddingSettings custom = new()
        {
            Model = "nomic-embed-text", UseModelPrefixes = false, QueryPrefix = "q: ", DocumentPrefix = "d: ",
        };
        StoredVectors(Nomic);

        QueryEmbedding embedding = await CreateService(custom).EmbedQueryAsync("Q", InContainer());

        embedding.Spaces.Select(s => s.ModelId).Should().Equal(EmbeddingIdentity.For(custom), Nomic);
        await _provider.Received(1).EmbedAsync("search_query: q", EmbeddingInputType.Unspecified, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedQueryAsync_ModelIdFilterForTheBareModel_EmbedsTheQueryAsGiven()
    {
        var options = new SearchOptions(Filters: new Dictionary<string, string> { ["modelId"] = "nomic-embed-text" });

        QueryEmbedding embedding = await CreateService().EmbedQueryAsync("Q", options);

        embedding.Spaces.Select(s => s.ModelId).Should().Equal("nomic-embed-text");
        await _provider.Received(1).EmbedAsync("Q", EmbeddingInputType.Unspecified, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedQueryAsync_ModelIdFilterThatCantBeReproduced_SearchesNothing()
    {
        var options = new SearchOptions(Filters: new Dictionary<string, string> { ["modelId"] = "nomic-embed-text-recipe-00000000" });

        QueryEmbedding embedding = await CreateService().EmbedQueryAsync("q", options);

        embedding.Spaces.Should().BeEmpty();
        await _provider.DidNotReceiveWithAnyArgs().EmbedAsync(default!, default, default);
    }

    [Fact]
    public async Task SearchAsync_TwoSpaces_FiltersEachOnItsOwnIdAndMergesByScore()
    {
        _store.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(),
                Arg.Is<Dictionary<string, string>?>(f => f!["modelId"] == "a"), Arg.Any<SearchScopes>(), Arg.Any<CancellationToken>())
            .Returns([new VectorSearchResult("c1", 0.5f, new Dictionary<string, string>())]);
        _store.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(),
                Arg.Is<Dictionary<string, string>?>(f => f!["modelId"] == "b"), Arg.Any<SearchScopes>(), Arg.Any<CancellationToken>())
            .Returns([new VectorSearchResult("c2", 0.9f, new Dictionary<string, string>())]);
        var query = new QueryEmbedding([new QuerySpace([1f], "a"), new QuerySpace([1f], "b")]);

        List<SearchHit> hits = await CreateService().SearchAsync("q", query, new SearchOptions(TopK: 5), SearchScopes.Unrestricted);

        hits.Select(h => h.ChunkId).Should().Equal("c2", "c1");
    }

    [Fact]
    public async Task ScoreChunksAsync_ScoresEachChunkInItsOwnSpace()
    {
        _store.ScoreChunksAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), "a", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, float> { ["c1"] = 0.4f });
        _store.ScoreChunksAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), "b", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, float> { ["c2"] = 0.7f });
        var query = new QueryEmbedding([new QuerySpace([1f], "a"), new QuerySpace([1f], "b")]);

        IReadOnlyDictionary<string, float> scores = await CreateService().ScoreChunksAsync(query, ["c1", "c2"]);

        scores.Should().BeEquivalentTo(new Dictionary<string, float> { ["c1"] = 0.4f, ["c2"] = 0.7f });
    }
}
