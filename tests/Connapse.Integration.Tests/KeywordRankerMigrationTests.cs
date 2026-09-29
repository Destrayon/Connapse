using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The #550 data migration on a database that already saved Search settings: a stored "TsRank" (the
/// old default, carried along by any save of the Search tab) is dropped so BM25 applies; a stored
/// "Bm25" is kept. Owns its PostgreSQL container, like <see cref="FusionAlphaMigrationTests"/>, because
/// it must stop before the migration.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class KeywordRankerMigrationTests : IAsyncLifetime
{
    private const string Before = "20260926232555_AddBm25Statistics";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_ranker_migration")
        .WithUsername("migration_test")
        .WithPassword("migration_test")
        .Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private KnowledgeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseVector())
            .Options);

    [Theory]
    [InlineData("TsRank", null)]
    [InlineData("tsrank", null)]
    [InlineData("Bm25", "Bm25")]
    public async Task Migration_SavedSearchSettings_DropsOnlyTheOldDefault(string stored, string? expected)
    {
        await using (var context = CreateContext())
        {
            await context.GetService<IMigrator>().MigrateAsync(Before);
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO settings (category, values) VALUES ('search', jsonb_build_object('keywordRanker', {0}::text, 'fusionAlpha', 0.4))",
                stored);

            await context.GetService<IMigrator>().MigrateAsync();
        }

        await using (var context = CreateContext())
        {
            string? ranker = await context.Database
                .SqlQueryRaw<string?>("SELECT values ->> 'keywordRanker' AS \"Value\" FROM settings WHERE category = 'search'")
                .SingleAsync();
            double alpha = await context.Database
                .SqlQueryRaw<double>("SELECT (values ->> 'fusionAlpha')::float8 AS \"Value\" FROM settings WHERE category = 'search'")
                .SingleAsync();

            ranker.Should().Be(expected);
            alpha.Should().BeApproximately(0.4, 1e-9, "the rest of the saved settings are kept");
        }
    }
}
