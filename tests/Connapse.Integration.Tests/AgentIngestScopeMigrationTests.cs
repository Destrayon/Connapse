using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The CON-171 data migration on a database that already holds tokens issued with agent:ingest: the scope,
/// which no check ever required, is dropped from agent keys and personal access tokens, and every other scope
/// stays in its place. Owns its PostgreSQL container, like <see cref="KeywordRankerMigrationTests"/>, because
/// it must stop before the migration.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AgentIngestScopeMigrationTests : IAsyncLifetime
{
    private const string Before = "20261025062143_AddRefreshTokenScope";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_ingest_scope_migration")
        .WithUsername("migration_test")
        .WithPassword("migration_test")
        .Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private ConnapseIdentityDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ConnapseIdentityDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options);

    [Fact]
    public async Task Migration_TokensHoldingAgentIngest_DropsOnlyThatScope()
    {
        // Stored scopes as each form could have written them, plus one with spaces a hand edit could leave.
        string[] stored = ["knowledge:read,agent:ingest", "agent:ingest", "knowledge:read,agent:ingest,knowledge:write",
            "knowledge:read, agent:ingest", "knowledge:read,knowledge:write", ""];
        string[] expected = ["knowledge:read", "", "knowledge:read,knowledge:write",
            "knowledge:read", "knowledge:read,knowledge:write", ""];

        Guid[] keyIds = stored.Select(_ => Guid.NewGuid()).ToArray();
        Guid[] patIds = stored.Select(_ => Guid.NewGuid()).ToArray();
        await using (var context = CreateContext())
        {
            await context.GetService<IMigrator>().MigrateAsync(Before);
            var user = new ConnapseUser { Id = Guid.NewGuid(), UserName = "owner", NormalizedUserName = "OWNER", CreatedAt = DateTime.UtcNow };
            var agent = new AgentEntity { Id = Guid.NewGuid(), Name = "agent", CreatedByUserId = user.Id };
            context.Users.Add(user);
            context.Agents.Add(agent);
            for (int i = 0; i < stored.Length; i++)
            {
                context.AgentApiKeys.Add(new AgentApiKeyEntity
                {
                    Id = keyIds[i],
                    AgentId = agent.Id,
                    Name = $"key-{i}",
                    TokenHash = $"k{i}",
                    TokenPrefix = "k",
                    Scopes = stored[i],
                    CreatedAt = DateTime.UtcNow,
                });
                context.PersonalAccessTokens.Add(new PersonalAccessTokenEntity
                {
                    Id = patIds[i],
                    UserId = user.Id,
                    Name = $"pat-{i}",
                    TokenHash = $"p{i}",
                    TokenPrefix = "p",
                    Scopes = stored[i],
                    CreatedAt = DateTime.UtcNow,
                });
            }
            await context.SaveChangesAsync();

            await context.GetService<IMigrator>().MigrateAsync();
        }

        await using (var context = CreateContext())
        {
            var keys = await context.AgentApiKeys.AsNoTracking().ToDictionaryAsync(k => k.Id, k => k.Scopes);
            var pats = await context.PersonalAccessTokens.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Scopes);

            keyIds.Select(id => keys[id]).Should().Equal(expected);
            patIds.Select(id => pats[id]).Should().Equal(expected);
        }
    }
}
