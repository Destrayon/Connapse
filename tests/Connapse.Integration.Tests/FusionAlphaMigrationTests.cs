using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The #521 data migration on a database that already saved Search settings: a stored 0.5 (the old
/// default) moves to 0.3, and any other stored weight is left alone. Owns its PostgreSQL container
/// for the same reason as <see cref="ChunkOwnerMigrationTests"/>: it must stop before the migration.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FusionAlphaMigrationTests : IAsyncLifetime
{
    private const string Before = "20260923155352_AddGitHubAppProviderCredential";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_alpha_migration")
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
    [InlineData("0.5", 0.3)]
    [InlineData("0.7", 0.7)]
    [InlineData("0.3", 0.3)]
    public async Task Migration_SavedSearchSettings_ShiftsOnlyTheOldDefault(string stored, double expected)
    {
        await using (var context = CreateContext())
        {
            await context.GetService<IMigrator>().MigrateAsync(Before);
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO settings (category, values) VALUES ('search', jsonb_build_object('fusionAlpha', {0}::numeric, 'fusionMethod', 'ConvexCombination'))",
                stored);
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO settings (category, values) VALUES ('embedding', jsonb_build_object('fusionAlpha', 0.5))");

            await context.GetService<IMigrator>().MigrateAsync();
        }

        await using (var context = CreateContext())
        {
            double search = await context.Database
                .SqlQueryRaw<double>("SELECT (values ->> 'fusionAlpha')::float8 AS \"Value\" FROM settings WHERE category = 'search'")
                .SingleAsync();
            double other = await context.Database
                .SqlQueryRaw<double>("SELECT (values ->> 'fusionAlpha')::float8 AS \"Value\" FROM settings WHERE category = 'embedding'")
                .SingleAsync();
            string method = await context.Database
                .SqlQueryRaw<string>("SELECT values ->> 'fusionMethod' AS \"Value\" FROM settings WHERE category = 'search'")
                .SingleAsync();

            search.Should().BeApproximately(expected, 1e-9);
            other.Should().BeApproximately(0.5, 1e-9, "only the search category is touched");
            method.Should().Be("ConvexCombination", "the rest of the saved settings are kept");
        }
    }
}
