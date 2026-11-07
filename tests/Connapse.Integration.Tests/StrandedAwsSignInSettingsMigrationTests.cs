using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The #448 data migration on a database upgraded from a build that had the AWS SSO device flow and Cognito:
/// their settings, client secrets included, go, and nothing else in the table moves. Owns its PostgreSQL
/// container, like <see cref="KeywordRankerMigrationTests"/>, because it must stop before the migration.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class StrandedAwsSignInSettingsMigrationTests : IAsyncLifetime
{
    private const string Before = "20261106222438_DeleteContainerMemory";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_aws_signin_migration")
        .WithUsername("migration_test")
        .WithPassword("migration_test")
        .WithCreateParameterModifier(TestContainerLimits.Apply)
        .Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private KnowledgeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseVector())
            .Options);

    /// <summary>Upgrades through every migration, from a database holding one row per category.</summary>
    private async Task UpgradeHoldingAsync(params string[] categories)
    {
        await using var context = CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(Before);
        foreach (string category in categories)
        {
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO settings (category, values) VALUES ({0}, jsonb_build_object('clientSecret', {0}))", category);
        }

        await context.GetService<IMigrator>().MigrateAsync();
    }

    private async Task<Dictionary<string, string>> StoredSecretsAsync()
    {
        await using var context = CreateContext();
        var rows = await context.Settings.AsNoTracking().ToListAsync();
        return rows.ToDictionary(r => r.Category, r => r.Values.RootElement.GetProperty("clientSecret").GetString()!);
    }

    [Fact]
    public async Task Migration_StrandedAwsSignInSettings_AreGone()
    {
        // Lower case is how the removed code saved them; the table's lookups are exact, so a hand-edited spelling is its own row.
        await UpgradeHoldingAsync("awssso", "cognito", "AwsSso", "Cognito");

        (await StoredSecretsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Migration_SettingsStillInUse_Survive()
    {
        // samlsignin and identitycenter are today's AWS sign-in, azuread and security hold live secrets, and the
        // last two only look like the removed categories.
        string[] inUse =
        [
            "samlsignin", "identitycenter", "permissionenforcement", "azure", "azuread", "security",
            "embedding", "chunking", "search", "llm", "upload", "awssso2", "cognito_pool",
        ];
        await UpgradeHoldingAsync([.. inUse, "awssso", "cognito"]);

        (await StoredSecretsAsync()).Should().BeEquivalentTo(inUse.ToDictionary(c => c, c => c));
    }
}
