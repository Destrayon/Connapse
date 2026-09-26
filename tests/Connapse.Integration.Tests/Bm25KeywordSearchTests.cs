using Connapse.Core;
using Connapse.Search.Keyword;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Keyword;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Keyword search ranked by pg_textsearch BM25 (#545), on the Postgres image built from
/// docker/postgres. Owns its container because the shared fixture runs the stock pgvector image.
/// </summary>
[Trait("Category", "Integration")]
public class Bm25KeywordSearchTests : IAsyncLifetime
{
    private readonly IFutureDockerImage _image = new ImageFromDockerfileBuilder()
        .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "docker/postgres")
        .WithName("connapse-postgres:pg17-bm25-test")
        .WithCleanUp(false)
        .Build();

    private PostgreSqlContainer _postgres = null!;
    private NpgsqlDataSource _dataSource = null!;
    private Bm25IndexManager _bm25 = null!;

    public async Task InitializeAsync()
    {
        await _image.CreateAsync();
        _postgres = new PostgreSqlBuilder()
            .WithImage(_image)
            .WithDatabase("connapse_bm25")
            .WithUsername("bm25_test")
            .WithPassword("bm25_test")
            .Build();
        await _postgres.StartAsync();
        var builder = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString());
        builder.EnableDynamicJson();
        builder.UseVector();
        _dataSource = builder.Build();

        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var factory = Substitute.For<IDbContextFactory<KnowledgeDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => CreateContext());
        _bm25 = new Bm25IndexManager(factory, new Bm25IndexState(), NullLogger<Bm25IndexManager>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task SearchAsync_RareAndCommonTerms_RanksTheRareTermFirst()
    {
        Guid container = await SeedAsync(
            "hyperparathyroidism was observed",
            "patients patients patients enrolled",
            "patients were followed up",
            "patients received the dose",
            "patients and controls");

        var hits = await SearchAsync(container, "patients with hyperparathyroidism");

        // ts_rank has no IDF, so the chunk repeating the common word wins there; BM25 weighs rarity.
        hits.Should().HaveCount(5);
        hits[0].Content.Should().Be("hyperparathyroidism was observed");
    }

    [Theory]
    [InlineData("calcium -supplements", new[] { "calcium intake and bone density" })]
    [InlineData("the who", new[] { "a concert by the who" })]
    public async Task SearchAsync_ExclusionsAndStopWordQueries_BehaveAsWithTsRank(string query, string[] expected)
    {
        Guid container = await SeedAsync(
            "calcium intake and bone density", "calcium supplements", "a concert by the who", "unrelated text");

        var hits = await SearchAsync(container, query);

        hits.Select(h => h.Content).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task ScoreChunksAsync_NamedChunks_ScoresMatchesAndZeroesTheRest()
    {
        Guid container = await SeedAsync("calcium intake", "calcium supplements", "unrelated text");
        await using var context = CreateContext();
        Dictionary<string, Guid> ids = await context.Chunks
            .Where(c => c.OwnerId == container)
            .ToDictionaryAsync(c => c.Content, c => c.Id);

        var scores = await Service(context).ScoreChunksAsync("calcium -supplements",
            ids.Values.Select(id => id.ToString()).ToList());

        scores[ids["calcium intake"].ToString()].Should().BeGreaterThan(0f);
        scores[ids["calcium supplements"].ToString()].Should().Be(0f);
        scores[ids["unrelated text"].ToString()].Should().Be(0f);
    }

    [Fact]
    public async Task GetIndexNameAsync_TwoContainers_KeepsEachContainersTermStatistics()
    {
        Guid rare = await SeedAsync("bone marrow", "calcium", "vitamin d", "serum level");
        Guid common = await SeedAsync("bone marrow", "bone density", "bone loss", "bone health");

        var rareHits = await SearchAsync(rare, "bone");
        var commonHits = await SearchAsync(common, "bone");

        // Same text, but "bone" is rare in one container and everywhere in the other.
        rareHits.Single(h => h.Content == "bone marrow").Score
            .Should().BeGreaterThan(commonHits.Single(h => h.Content == "bone marrow").Score);
    }

    private async Task<List<SearchHit>> SearchAsync(Guid container, string query)
    {
        await using var context = CreateContext();
        return await Service(context).SearchAsync(query,
            new SearchOptions { TopK = 10, ContainerId = container.ToString() }, SearchScopes.Unrestricted);
    }

    private KeywordSearchService Service(KnowledgeDbContext context)
    {
        var settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        settings.CurrentValue.Returns(new SearchSettings { KeywordRanker = "Bm25" });
        return new KeywordSearchService(context, NullLogger<KeywordSearchService>.Instance, settings, _bm25);
    }

    private async Task<Guid> SeedAsync(params string[] contents)
    {
        Guid containerId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        await using var context = CreateContext();
        context.Containers.Add(new ContainerEntity { Id = containerId, Name = $"bm25-{containerId:N}"[..20] });
        context.Documents.Add(new DocumentEntity
        {
            Id = documentId,
            ContainerId = containerId,
            FileName = "doc.txt",
            ContentType = "text/plain",
            Path = "/doc.txt",
            ContentHash = string.Empty,
            SizeBytes = 1,
            ChunkCount = contents.Length,
            Generation = 1,
            Status = "Ready",
            CreatedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>(),
        });
        for (int i = 0; i < contents.Length; i++)
        {
            context.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                OwnerId = containerId,
                ChunkIndex = i,
                Content = contents[i],
                TokenCount = 1,
                StartOffset = 0,
                EndOffset = contents[i].Length,
            });
        }
        await context.SaveChangesAsync();
        return containerId;
    }

    private KnowledgeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseNpgsql(_dataSource, npgsql => npgsql.UseVector())
            .Options);
}
