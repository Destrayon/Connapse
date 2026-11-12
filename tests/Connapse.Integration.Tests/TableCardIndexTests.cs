using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Tables;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-310: each table's card is embedded once into <c>table_card_vectors</c> and scored against a search in SQL. A
/// vector goes with its table, and one made from an older card (before a column was hidden) is never used.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class TableCardIndexTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task IndexAsync_ThenSearch_ScoresTheTableAboutTheSearchHighest()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        var embedder = new WordEmbedder();
        (Guid containerId, TableInfo payroll, TableInfo fruit) = await NewContainerAsync(scope);
        try
        {
            PgTableCardIndex index = Index(scope, embedder);
            TableCard[] cards = [TableCard.For(payroll), TableCard.For(fruit)];

            await index.IndexAsync(containerId, cards);
            int afterIndex = embedder.DocumentTexts;
            TableCardSimilarities found = await index.SimilaritiesAsync(containerId, cards, "staff salary");

            afterIndex.Should().Be(2);
            embedder.DocumentTexts.Should().Be(2, "fresh cards aren't embedded again");
            found.Available.Should().BeTrue();
            found.Unembedded.Should().Be(0);
            found.Scores[payroll.Id].Should().BeGreaterThan(found.Scores[fruit.Id]);
            (await VectorCountAsync(scope, containerId)).Should().Be(2);
        }
        finally
        {
            await DeleteAsync(scope, containerId);
        }
    }

    [Fact]
    public async Task DeleteAsync_Table_TakesItsCardVector()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (Guid containerId, TableInfo payroll, TableInfo fruit) = await NewContainerAsync(scope);
        try
        {
            await Index(scope, new WordEmbedder()).IndexAsync(containerId, [TableCard.For(payroll), TableCard.For(fruit)]);

            await scope.ServiceProvider.GetRequiredService<ITableStore>().DeleteAsync(containerId, payroll.Id);

            (await VectorCountAsync(scope, containerId)).Should().Be(1);
        }
        finally
        {
            await DeleteAsync(scope, containerId);
        }
    }

    [Fact]
    public async Task SimilaritiesAsync_CardChangedSinceEmbedding_NeverScoresTheOldVector()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (Guid containerId, TableInfo payroll, _) = await NewContainerAsync(scope);
        try
        {
            await Index(scope, new WordEmbedder()).IndexAsync(containerId, [TableCard.For(payroll)]);
            TableInfo hidden = await scope.ServiceProvider.GetRequiredService<ITableStore>()
                .SetHiddenColumnsAsync(containerId, payroll.Id, ["salary"]) ?? throw new InvalidOperationException("table gone");

            // The embedding service is down for cards now, so the hidden column's old vector is all there is.
            TableCardSimilarities found = await Index(scope, new WordEmbedder { FailDocuments = true })
                .SimilaritiesAsync(containerId, [TableCard.For(hidden)], "salary");

            found.Scores.Should().BeEmpty();
            found.Available.Should().BeFalse();
        }
        finally
        {
            await DeleteAsync(scope, containerId);
        }
    }

    [Fact]
    public async Task SimilaritiesAsync_StaleCard_IsEmbeddedAgainAndScored()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (Guid containerId, TableInfo payroll, _) = await NewContainerAsync(scope);
        try
        {
            var embedder = new WordEmbedder();
            PgTableCardIndex index = Index(scope, embedder);
            await index.IndexAsync(containerId, [TableCard.For(payroll)]);
            TableInfo described = payroll with { Description = "What each person is paid." };

            TableCardSimilarities found = await index.SimilaritiesAsync(containerId, [TableCard.For(described)], "paid");

            embedder.DocumentTexts.Should().Be(2);
            found.Scores.Should().ContainKey(payroll.Id);
            (await VectorCountAsync(scope, containerId)).Should().Be(1);
        }
        finally
        {
            await DeleteAsync(scope, containerId);
        }
    }

    [Fact]
    public async Task SimilaritiesAsync_EmbeddingServiceDown_SaysUnavailable()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (Guid containerId, TableInfo payroll, TableInfo fruit) = await NewContainerAsync(scope);
        try
        {
            TableCardSimilarities found = await Index(scope, new WordEmbedder { FailDocuments = true, FailQueries = true })
                .SimilaritiesAsync(containerId, [TableCard.For(payroll), TableCard.For(fruit)], "salary");

            found.Should().BeEquivalentTo(TableCardSimilarities.Unavailable(2));
        }
        finally
        {
            await DeleteAsync(scope, containerId);
        }
    }

    [Fact]
    public async Task SimilaritiesAsync_OtherContainersCards_AreNotScored()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (Guid first, TableInfo payroll, _) = await NewContainerAsync(scope);
        (Guid second, _, _) = await NewContainerAsync(scope);
        try
        {
            PgTableCardIndex index = Index(scope, new WordEmbedder());
            await index.IndexAsync(first, [TableCard.For(payroll)]);

            TableCardSimilarities found = await Index(scope, new WordEmbedder { FailDocuments = true })
                .SimilaritiesAsync(second, [TableCard.For(payroll)], "salary");

            found.Scores.Should().BeEmpty();
        }
        finally
        {
            await DeleteAsync(scope, first);
            await DeleteAsync(scope, second);
        }
    }

    private static PgTableCardIndex Index(AsyncServiceScope scope, IEmbeddingProvider embedder) =>
        ActivatorUtilities.CreateInstance<PgTableCardIndex>(scope.ServiceProvider, embedder);

    private static async Task<(Guid ContainerId, TableInfo Payroll, TableInfo Fruit)> NewContainerAsync(AsyncServiceScope scope)
    {
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var container = await containers.CreateAsync(new CreateContainerRequest($"cards-{Guid.NewGuid():N}", null, ContainerKind.Documents));
        Guid containerId = Guid.Parse(container.Id);
        var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
        TableInfo payroll = await tables.CreateAsync(containerId, new TableLoad("staff_payroll",
            [new TableColumn("employee", TableColumnType.Text), new TableColumn("salary", TableColumnType.Number)], Rows(),
            Description: "Staff pay per employee."));
        TableInfo fruit = await tables.CreateAsync(containerId, new TableLoad("fruit_prices",
            [new TableColumn("fruit", TableColumnType.Text), new TableColumn("price", TableColumnType.Number)], Rows(),
            Description: "Market prices of fruit."));
        return (containerId, payroll, fruit);
    }

    private static async Task<int> VectorCountAsync(AsyncServiceScope scope, Guid containerId)
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync();
        return await context.TableCardVectors.CountAsync(v => v.ContainerId == containerId);
    }

    private static async Task DeleteAsync(AsyncServiceScope scope, Guid containerId) =>
        await scope.ServiceProvider.GetRequiredService<IContainerStore>().DeleteAsync(containerId);

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
    {
        await Task.Yield();
        yield break;
    }

    /// <summary>
    /// A stand-in embedding model: each word sets one of 64 dimensions, so texts sharing words are similar. Counts the
    /// card texts it embeds, and can fail for cards, searches or both.
    /// </summary>
    private sealed class WordEmbedder : IEmbeddingProvider
    {
        public int Dimensions => 64;
        public string ModelId => "words";
        public bool FailDocuments { get; init; }
        public bool FailQueries { get; init; }
        public int DocumentTexts { get; private set; }

        public Task<float[]> EmbedAsync(string text, EmbeddingInputType inputType, CancellationToken ct = default) =>
            FailQueries ? throw new HttpRequestException("embedding service down") : Task.FromResult(Embed(text));

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, EmbeddingInputType inputType, CancellationToken ct = default)
        {
            if (FailDocuments)
                throw new HttpRequestException("embedding service down");
            List<float[]> vectors = [.. texts.Select(Embed)];
            DocumentTexts += vectors.Count;
            return Task.FromResult<IReadOnlyList<float[]>>(vectors);
        }

        private static float[] Embed(string text)
        {
            var vector = new float[64];
            vector[0] = 0.01f;
            foreach (string word in text.ToLowerInvariant().Split([' ', '\n', '.', ';', '(', ')', ':', '_'], StringSplitOptions.RemoveEmptyEntries))
                vector[(int)((uint)string.GetHashCode(word, StringComparison.Ordinal) % 63) + 1] += 1;
            return vector;
        }
    }
}
