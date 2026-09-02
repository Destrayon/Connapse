using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The DropProviderCredentialAccessKeyColumns migration, exercised against a database that still holds
/// a legacy access-key row — the state its own project claims is impossible, and the one that would
/// turn a retired credential into a silent ambient-identity fallback if the migration left it behind.
/// </summary>
/// <remarks>
/// Spins its own Postgres so it can replay the schema from the migration before this one: the shared
/// fixture is already at head with the columns gone, so it cannot seed a pre-drop row.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ProviderCredentialMigrationTests : IAsyncLifetime
{
    private const string PriorMigration = "20260901225414_AddProviderCredentialShapeConstraint";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .Build();

    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var builder = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString());
        builder.EnableDynamicJson();
        builder.UseVector();
        _dataSource = builder.Build();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private KnowledgeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseNpgsql(_dataSource, npgsql => npgsql.UseVector())
            .Options);

    [Fact]
    public async Task Drop_RemovesLegacyAccessKeyRows_KeepsRolesAnywhere_AndForbidsIncompleteRows()
    {
        // 1. Migrate up to the migration BEFORE the drop, where public_id/secret_protected still exist.
        await using (var ctx = NewContext())
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PriorMigration);

            // A legacy access-key row (valid under the old single-shape CHECK: RA fields all null)…
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO provider_credentials (provider, public_id, secret_protected, created_at) " +
                "VALUES ('aws-legacy', 'AKIAOLD', 'cipher', now());");

            // …alongside a real Roles Anywhere row.
            await ctx.Database.ExecuteSqlRawAsync("""
                INSERT INTO provider_credentials (
                    provider, public_id, secret_protected, created_at,
                    trust_anchor_arn, profile_arn, role_arn, region, certificate_pem, private_key_protected)
                VALUES ('aws-ra', '', '', now(),
                    'arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta',
                    'arn:aws:rolesanywhere:us-east-1:111:profile/pf',
                    'arn:aws:iam::111:role/connapse', 'us-east-1', 'cert', 'keycipher');
                """);
        }

        // 2. Apply the drop migration.
        await using (var ctx = NewContext())
        {
            await ctx.GetService<IMigrator>().MigrateAsync();

            // The access-key row is deleted — so it can never be misread as "nothing configured" and
            // become a silent ambient-credential fallback. The Roles Anywhere row survives untouched.
            (await ctx.ProviderCredentials.CountAsync(c => c.Provider == "aws-legacy")).Should().Be(0);
            (await ctx.ProviderCredentials.CountAsync(c => c.Provider == "aws-ra")).Should().Be(1);

            // The completeness constraint now forbids a half-written row (trust anchor, no profile),
            // so no incomplete row can exist to be misread later.
            Func<Task> incomplete = async () => await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO provider_credentials (provider, created_at, trust_anchor_arn) " +
                "VALUES ('aws-incomplete', now(), 'arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta');");

            (await incomplete.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        }
    }
}
