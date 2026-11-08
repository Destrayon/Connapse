using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// A fact saved with a date-only start and changed later that same day, also with a date-only start (CON-290): both
/// dates are the same midnight, so the change takes effect when it is made and the old value ends there.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemorySameDayCorrectionTests(SharedWebAppFixture fixture)
{
    private static readonly DateTimeOffset Today = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Afternoon = Today.AddHours(14).AddMinutes(30);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Task<MemoryWriteResult?> AnyChain(IReadOnlyList<MemoryFact> chain) => Task.FromResult<MemoryWriteResult?>(null);

    private static async Task<(PostgresMemoryStore Store, IDbContextFactory<KnowledgeDbContext> Factory, Guid Container)> StoreAsync(
        AsyncServiceScope scope, DateTimeOffset now)
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid container = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Containers.Add(new ContainerEntity { Id = container, Name = $"same-day-{Guid.NewGuid():N}" });
            await db.SaveChangesAsync();
        }
        return (new PostgresMemoryStore(factory, new FixedClock(now)), factory, container);
    }

    [Fact]
    public async Task CorrectAsync_ChangedWithSameDateOnlyStartOnTheDayItWasSaved_EndsOldValueNow()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (PostgresMemoryStore store, var factory, Guid container) = await StoreAsync(scope, Afternoon);
        var author = new MemoryCaller(Guid.NewGuid(), null, null);
        MemoryFact saved = await store.AddAsync(container, new NewMemoryFact("alpha", "database", "PostgreSQL 15", Today, null, []), author);

        MemoryWriteResult result = await store.CorrectAsync(saved.Id, MemoryCorrection.Changed,
            new NewMemoryFact("alpha", "database", "PostgreSQL 16", Today, null, []), author, AnyChain);

        result.Error.Should().BeNull();
        result.Replaced!.ValidTo.Should().Be(Afternoon);
        result.Replaced.SupersededAt.Should().BeNull();
        result.Fact!.ValidFrom.Should().Be(Afternoon);
        result.Fact.Value.Should().Be("PostgreSQL 16");
        await using var db = await factory.CreateDbContextAsync();
        MemoryFactEntity old = await db.MemoryFacts.SingleAsync(f => f.Id == saved.Id);
        old.ValidTo.Should().Be(Afternoon);
    }

    [Fact]
    public async Task CorrectAsync_ChangedWithSameDateOnlyStartOnALaterDay_IsRefused()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (PostgresMemoryStore store, _, Guid container) = await StoreAsync(scope, Afternoon.AddDays(1));
        var author = new MemoryCaller(Guid.NewGuid(), null, null);
        MemoryFact saved = await store.AddAsync(container, new NewMemoryFact("alpha", "database", "PostgreSQL 15", Today, null, []), author);

        MemoryWriteResult result = await store.CorrectAsync(saved.Id, MemoryCorrection.Changed,
            new NewMemoryFact("alpha", "database", "PostgreSQL 16", Today, null, []), author, AnyChain);

        result.Error.Should().Contain("must be after the old fact's start");
        result.Fact.Should().BeNull();
    }

    [Fact]
    public async Task CorrectAsync_ChangedWithEarlierDateOnTheDayItWasSaved_IsRefused()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        (PostgresMemoryStore store, _, Guid container) = await StoreAsync(scope, Afternoon);
        var author = new MemoryCaller(Guid.NewGuid(), null, null);
        MemoryFact saved = await store.AddAsync(container, new NewMemoryFact("alpha", "database", "PostgreSQL 15", Today, null, []), author);

        MemoryWriteResult result = await store.CorrectAsync(saved.Id, MemoryCorrection.Changed,
            new NewMemoryFact("alpha", "database", "PostgreSQL 16", Today.AddDays(-1), null, []), author, AnyChain);

        result.Error.Should().Contain("must be after the old fact's start");
    }
}
