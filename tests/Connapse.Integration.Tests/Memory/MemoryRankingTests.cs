using Connapse.Core;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests.Memory;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemoryRankingTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Query_AWordInMostFacts_DoesNotOutrankTheWordThatPicksTheFact()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid container = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Containers.Add(new ContainerEntity { Id = container, Name = $"rank-{Guid.NewGuid():N}" });
            await db.SaveChangesAsync();
        }
        var store = new PostgresMemoryStore(factory, TimeProvider.System);
        string[] attributes = ["database", "cache", "region", "lead", "ci", "alerts", "release", "rate limit", "queue", "cdn"];
        for (int i = 0; i < attributes.Length; i++)
            await store.AddAsync(container, new NewMemoryFact("Halcyon", attributes[i], $"value {i}", null, null, [], $"HAL-{i + 10} done"),
                new MemoryCaller(null, null, null));

        IReadOnlyList<MemoryFact> found = await store.QueryAsync(
            new MemoryQuery(container, Text: "What did HAL-17 change in Halcyon?", History: true), 0, 5);

        found[0].Reason.Should().Be("HAL-17 done", "the project name and ticket prefix are in every fact; the number picks one");
    }
}
