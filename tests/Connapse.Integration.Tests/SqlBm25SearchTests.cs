using System.Net.Http.Json;
using Connapse.Core;
using Connapse.Search.Keyword;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Keyword;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Integration.Tests;

/// <summary>
/// Plain-SQL BM25 (#548) on the stock pgvector image. Expected scores are golden values from
/// bm25s 0.3.11 (MIT), method="lucene", k1=1.2, b=0.75, on the same whitespace-tokenised corpus;
/// every word below is its own english lexeme apart from "lazy" (stemmed to "lazi"), so the
/// analysed lengths match bm25s's token counts.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SqlBm25SearchTests(SharedWebAppFixture fixture)
{
    private static readonly string LongChunk = "dog " + string.Concat(Enumerable.Repeat("filler ", 45)) + "quick";

    private static readonly string[] Corpus =
    [
        "quick brown fox",
        "lazy dog",
        "quick brown dog quick",
        LongChunk,
        "brown brown brown brown brown cat",
    ];

    [Theory]
    [InlineData("quick dog", new[] { 0.355131, 0.372966, 0.755084, 0.228811, 0 })]
    [InlineData("brown", new[] { 0.355131, 0, 0.338923, 0, 0.469879 })]
    [InlineData("cat quick zebra", new[] { 0.355131, 0, 0.416162, 0.114405, 0.798794 })]
    [InlineData("dog dog", new[] { 0, 0.745933, 0.677846, 0.228811, 0 })]
    public async Task ScoreChunksAsync_GoldenCorpus_MatchesBm25sLucene(string query, double[] expected)
    {
        (Guid container, Guid[] chunks) = await SeedAsync(Corpus);
        try
        {
            var scores = await ScoreAsync(query, chunks);

            for (int i = 0; i < chunks.Length; i++)
                scores[chunks[i].ToString()].Should().BeApproximately((float)expected[i], 1e-5f, $"chunk {i}");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task SearchAsync_GoldenCorpus_RanksByBm25()
    {
        (Guid container, Guid[] chunks) = await SeedAsync(Corpus);
        try
        {
            var hits = await SearchAsync(container, "quick dog");

            hits.Select(h => h.ChunkId).Should().Equal(
                chunks[2].ToString(), chunks[1].ToString(), chunks[0].ToString(), chunks[3].ToString());
            hits[0].Score.Should().BeApproximately(0.755084f, 1e-5f);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task ScoreChunksAsync_AfterDeleteAndFold_UsesTheRemainingCorpus()
    {
        (Guid container, Guid[] chunks) = await SeedAsync(Corpus);
        try
        {
            await using (var context = await NewContextAsync())
                await context.Chunks.Where(c => c.Id == chunks[2]).ExecuteDeleteAsync();
            Guid[] remaining = [chunks[0], chunks[1], chunks[3], chunks[4]];
            float[] expected = [0.466387f, 0.486714f, 0.328721f, 0f];

            var beforeFold = await ScoreAsync("quick dog", remaining);
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<Bm25StatsFolder>().FoldAsync();
            var afterFold = await ScoreAsync("quick dog", remaining);

            // Unfolded deltas and folded statistics must give the same, exact, answer.
            for (int i = 0; i < remaining.Length; i++)
            {
                beforeFold[remaining[i].ToString()].Should().BeApproximately(expected[i], 1e-5f);
                afterFold[remaining[i].ToString()].Should().BeApproximately(expected[i], 1e-5f);
            }
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Theory]
    [InlineData(2)]  // as many rare-term candidates as asked for: the shortcut answers and proves itself
    [InlineData(10)] // too few: falls back to scoring every match
    public async Task SearchAsync_CommonAndRareTerms_EqualsExhaustiveScoring(int topK)
    {
        // "alpha" is in every chunk (common); "zeta" is in 2 of 40 (5%, so rare).
        string[] corpus = Enumerable.Range(0, 40)
            .Select(i => string.Join(' ', Enumerable.Repeat("alpha", 1 + i % 4))
                + (i < 2 ? " zeta" : "") + $" filler{i}")
            .ToArray();
        (Guid container, Guid[] chunks) = await SeedAsync(corpus);
        try
        {
            var exhaustive = (await ScoreAsync("alpha zeta", chunks))
                .Select(p => p.Value).OrderByDescending(v => v).Take(topK).ToList();

            var hits = await SearchAsync(container, "alpha zeta", topK);

            // Scores, not ids: many alpha-only chunks tie, and which of them fills a slot is arbitrary.
            hits.Select(h => h.Score).Should().Equal(exhaustive, (a, e) => Math.Abs(a - e) < 1e-5f);
            hits.Take(2).Select(h => h.ChunkId).Should().BeEquivalentTo([chunks[0].ToString(), chunks[1].ToString()]);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Theory]
    [InlineData("w1 w2 w3")]                  // common only: pruning must skip on frequency tiers
    [InlineData("w1 w7 w40 w300")]            // mixed
    [InlineData("w900 w1500")]                // rare
    [InlineData("w1 w1 w2 w5 w9 w13 w20 w31")] // long, with a repeated term
    public async Task SearchAsync_PrunedRounds_EqualExhaustiveScoring(string query)
    {
        // Zipf-like vocabulary with repeated words, so terms span frequency tiers and chunk lengths
        // vary; enough chunks that the query's postings exceed the one-pass limit.
        var random = new Random(548);
        string[] corpus = Enumerable.Range(0, 1500)
            .Select(_ => string.Join(' ', Enumerable.Range(0, random.Next(5, 40))
                .Select(_ => "w" + (int)Math.Floor(Math.Exp(random.NextDouble() * Math.Log(2000))))))
            .ToArray();
        (Guid container, Guid[] chunks) = await SeedAsync(corpus);
        try
        {
            var exhaustive = (await ScoreAsync(query, chunks))
                .Select(p => p.Value).Where(v => v > 0).OrderByDescending(v => v).Take(30).ToList();

            var hits = await SearchAsync(container, query, 30);

            hits.Select(h => h.Score).Should().Equal(exhaustive, (a, e) => Math.Abs(a - e) < 1e-4f);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Theory]
    [InlineData("the who", "a concert by the who")]
    [InlineData("calcium -supplements", "calcium intake and bone density")]
    public async Task SearchAsync_StopWordsAndExclusions_BehaveAsWithTsRank(string query, string expected)
    {
        (Guid container, _) = await SeedAsync(
            "calcium intake and bone density", "calcium supplements", "a concert by the who");
        try
        {
            var hits = await SearchAsync(container, query);

            hits.Select(h => h.Content).Should().Equal(expected);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task Statistics_AfterInsertUpdateDelete_MatchARecount()
    {
        (Guid container, Guid[] chunks) = await SeedAsync(Corpus);
        try
        {
            await using (var context = await NewContextAsync())
            {
                await context.Chunks.Where(c => c.Id == chunks[0])
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.Content, "zebra zebra quick"));
                await context.Chunks.Where(c => c.Id == chunks[1]).ExecuteDeleteAsync();
                await context.Chunks.Where(c => c.Id == chunks[2])
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.Metadata, new Dictionary<string, string> { ["k"] = "v" }));
            }

            await using var check = await NewContextAsync();
            var mismatches = await check.Database.SqlQueryRaw<string>("""
                WITH truth AS (
                    SELECT u.lexeme AS term, count(*) AS df
                    FROM chunks c, unnest(ts_filter(c.search_vector, '{{b}}')) u
                    WHERE c.owner_id = {0} GROUP BY u.lexeme),
                kept AS (
                    SELECT term, sum(df) AS df FROM (
                        SELECT term, df FROM bm25_term_stats WHERE owner_id = {0}
                        UNION ALL
                        SELECT term, d_df FROM bm25_delta WHERE owner_id = {0} AND term IS NOT NULL) x
                    GROUP BY term HAVING sum(df) <> 0)
                SELECT coalesce(t.term, k.term) AS "Value"
                FROM truth t FULL JOIN kept k ON k.term = t.term
                WHERE t.df IS DISTINCT FROM k.df
                """, container).ToListAsync();
            var lengths = await check.Database.SqlQueryRaw<long>("""
                SELECT (SELECT coalesce(sum(bm25_doc_length(search_vector)), 0) FROM chunks WHERE owner_id = {0})
                     - (SELECT coalesce(sum(total_len), 0) FROM bm25_owner_stats WHERE owner_id = {0})
                     - (SELECT coalesce(sum(d_len), 0) FROM bm25_delta WHERE owner_id = {0} AND term IS NULL)
                     AS "Value"
                """, container).ToListAsync();

            mismatches.Should().BeEmpty();
            lengths.Should().Equal(0L);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");
        }
    }

    private async Task<IReadOnlyDictionary<string, float>> ScoreAsync(string query, Guid[] chunks)
    {
        await using var context = await NewContextAsync();
        return await Service(context.Context).ScoreChunksAsync(query, chunks.Select(c => c.ToString()).ToList());
    }

    private async Task<List<SearchHit>> SearchAsync(Guid container, string query, int topK = 10)
    {
        await using var context = await NewContextAsync();
        return await Service(context.Context).SearchAsync(query,
            new SearchOptions { TopK = topK, ContainerId = container.ToString() }, SearchScopes.Unrestricted);
    }

    private static KeywordSearchService Service(KnowledgeDbContext context)
    {
        var settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        settings.CurrentValue.Returns(new SearchSettings { KeywordRanker = "Bm25" });
        return new KeywordSearchService(context, NullLogger<KeywordSearchService>.Instance, settings);
    }

    private async Task<ScopedContext> NewContextAsync()
    {
        AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        KnowledgeDbContext context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        return new ScopedContext(scope, context);
    }

    /// <summary>A context and the DI scope it was created in, disposed together.</summary>
    private sealed class ScopedContext(AsyncServiceScope scope, KnowledgeDbContext context) : IAsyncDisposable
    {
        public KnowledgeDbContext Context => context;
        public DbSet<ChunkEntity> Chunks => context.Chunks;
        public DbSet<DocumentEntity> Documents => context.Documents;
        public Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database => context.Database;
        public Task<int> SaveChangesAsync() => context.SaveChangesAsync();

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            await scope.DisposeAsync();
        }
    }

    private async Task<(Guid Container, Guid[] Chunks)> SeedAsync(params string[] contents)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"bm25-{Guid.NewGuid():N}"[..20] });
        response.EnsureSuccessStatusCode();
        Guid container = Guid.Parse((await response.Content.ReadFromJsonAsync<ContainerDto>())!.Id);

        Guid documentId = Guid.NewGuid();
        Guid[] chunkIds = contents.Select(_ => Guid.NewGuid()).ToArray();
        await using var context = await NewContextAsync();
        context.Documents.Add(new DocumentEntity
        {
            Id = documentId,
            ContainerId = container,
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
                Id = chunkIds[i],
                DocumentId = documentId,
                OwnerId = container,
                ChunkIndex = i,
                Content = contents[i],
                TokenCount = 1,
                StartOffset = 0,
                EndOffset = contents[i].Length,
            });
        }
        await context.SaveChangesAsync();
        return (container, chunkIds);
    }

    private record ContainerDto(string Id, string Name);
}
