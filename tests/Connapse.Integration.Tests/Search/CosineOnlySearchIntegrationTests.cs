using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Hybrid;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Connapse.Integration.Tests.Search;

/// <summary>
/// A search that asks for plain scores keeps them: a configured reranker, which would replace them, is skipped,
/// so the minimum score is measured against what the search itself scored.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CosineOnlySearchIntegrationTests(SharedWebAppFixture fixture)
{
    private const string Term = "zorbelquint";

    private sealed class FixedSettings(SearchSettings value) : IOptionsMonitor<SearchSettings>
    {
        public SearchSettings CurrentValue => value;
        public SearchSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<SearchSettings, string?> listener) => null;
    }

    /// <summary>Scores every hit 0.99, far above anything the keyword ranker gives.</summary>
    private sealed class LiftingReranker : ISearchReranker
    {
        public string Name => "Lift";

        public Task<List<SearchHit>> RerankAsync(string query, List<SearchHit> hits, CancellationToken cancellationToken = default) =>
            Task.FromResult(hits.Select(h => h with { Score = 0.99f }).ToList());
    }

    private HybridSearchService Search() => new(
        fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
        [new LiftingReranker()],
        new FixedSettings(new SearchSettings { Reranker = "Lift", KeywordRanker = "TsRank", AutoCut = false }),
        NullLogger<HybridSearchService>.Instance);

    private async Task<Guid> SeedAsync(string text)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        await using KnowledgeDbContext db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        ContainerEntity container = new() { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);
        DocumentEntity document = new()
        {
            Id = Guid.NewGuid(),
            ContainerId = container.Id,
            FileName = "one.md",
            Path = "/one.md",
            ContentHash = Guid.NewGuid().ToString("N"),
            IngestionStatus = DocumentStatus.Ready,
            CreatedAt = DateTime.UtcNow,
            Metadata = [],
        };
        db.Documents.Add(document);
        db.Chunks.Add(new ChunkEntity
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            OwnerId = container.Id,
            Content = text,
            ChunkIndex = 0,
            Metadata = [],
        });
        await db.SaveChangesAsync();
        return container.Id;
    }

    private static SearchOptions Options(Guid container, float minScore, bool cosineOnly) =>
        new(TopK: 10, MinScore: minScore, ContainerId: container.ToString(), Mode: SearchMode.Keyword, CosineOnly: cosineOnly);

    [Fact]
    public async Task Search_CosineOnly_SkipsTheConfiguredReranker()
    {
        Guid container = await SeedAsync($"the {Term} engine stalls");

        SearchResult reranked = await Search().SearchAsync(Term, Options(container, 0f, cosineOnly: false));
        SearchResult plain = await Search().SearchAsync(Term, Options(container, 0f, cosineOnly: true));

        reranked.Hits.Should().ContainSingle().Which.Score.Should().Be(0.99f);
        plain.Hits.Should().ContainSingle().Which.Score.Should().BeLessThan(0.99f);
    }

    [Fact]
    public async Task Search_CosineOnly_MinimumScoreMeetsTheSearchScoreNotTheRerankerScore()
    {
        Guid container = await SeedAsync($"the {Term} engine stalls");

        SearchResult reranked = await Search().SearchAsync(Term, Options(container, 0.95f, cosineOnly: false));
        SearchResult plain = await Search().SearchAsync(Term, Options(container, 0.95f, cosineOnly: true));

        reranked.Hits.Should().ContainSingle("the reranker lifted the hit over the minimum");
        plain.Hits.Should().BeEmpty("the keyword score is below the minimum and nothing may lift it");
    }
}
