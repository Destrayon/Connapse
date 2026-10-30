using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// A forget racing a correction of the same chain. The correction locks the old fact and adds its replacement; the
/// forget waits on that lock. The replacement cites a document the forgetting user cannot read, so the forget must
/// not delete it: the user never saw it, and a chain with a hidden version is reported as missing.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemoryForgetRaceTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task ForgetAsync_CorrectionWithPrivateSourceCommitsWhileWaiting_LeavesThePrivateVersion()
    {
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid();
        string open = Guid.NewGuid().ToString(), secret = Guid.NewGuid().ToString();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid container = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Containers.Add(new ContainerEntity { Id = container, Name = $"forget-race-{Guid.NewGuid():N}" });
            await db.SaveChangesAsync();
        }

        var store = new PostgresMemoryStore(factory, TimeProvider.System);
        MemoryFact first = await store.AddAsync(container, new NewMemoryFact("alpha", "region", "us-east", null, null,
            [new MemorySource(MemorySourceKind.Document, open)]), new MemoryCaller(alice, null, null));

        var access = Substitute.For<IDocumentAccess>();
        access.ReadableAsync(Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlySet<string>)ci.ArgAt<IReadOnlyCollection<string>>(1)
                .Where(d => d == open || (ci.ArgAt<Guid?>(0) == bob && d == secret)).ToHashSet(StringComparer.Ordinal));
        var links = Substitute.For<ILinkIndex>();
        links.DocumentsOfAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>().ToLookup(n => n, n => n));
        var service = new MemoryService(store, access, links, Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);

        // Bob's correction, held open: what PostgresMemoryStore.CorrectAsync does, paused before its commit.
        Guid replacement = Guid.CreateVersion7();
        await using KnowledgeDbContext correction = await factory.CreateDbContextAsync();
        await using var transaction = await correction.Database.BeginTransactionAsync();
        await correction.Database.ExecuteSqlAsync(
            $"SELECT id FROM memory_facts WHERE id = {first.Id} FOR UPDATE");
        await correction.Database.ExecuteSqlAsync(
            $"UPDATE memory_facts SET superseded_at = now() WHERE id = {first.Id}");
        correction.MemoryFacts.Add(new MemoryFactEntity
        {
            Id = replacement,
            ChainId = first.ChainId,
            ContainerId = container,
            Subject = "alpha",
            Attribute = "region",
            Value = "eu-west",
            RecordedAt = DateTimeOffset.UtcNow,
            ReplacesId = first.Id,
            AuthorUserId = bob,
            Sources = [new MemoryFactSourceEntity { FactId = replacement, Ordinal = 0, Kind = (short)MemorySourceKind.Document, Ref = secret }],
        });
        await correction.SaveChangesAsync();

        Task<(int Deleted, string? Error)> forget = service.ForgetAsync(container, first.Id, new MemoryCaller(alice, null, null));
        await WaitUntilBlockedOnLockAsync(factory);
        await transaction.CommitAsync();
        (int deleted, string? error) = await forget.WaitAsync(TimeSpan.FromSeconds(30));

        await using var check = await factory.CreateDbContextAsync();
        (await check.MemoryFacts.AnyAsync(f => f.Id == replacement)).Should().BeTrue("Alice cannot read the correction's source");
        deleted.Should().Be(0);
        error.Should().Contain("not found");
    }

    [Fact]
    public async Task ForgetAsync_VisibleCorrectionCommitsWhileWaiting_DeletesTheWholeChain()
    {
        Guid alice = Guid.NewGuid();
        string open = Guid.NewGuid().ToString();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid container = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Containers.Add(new ContainerEntity { Id = container, Name = $"forget-race-{Guid.NewGuid():N}" });
            await db.SaveChangesAsync();
        }

        var store = new PostgresMemoryStore(factory, TimeProvider.System);
        MemoryFact first = await store.AddAsync(container, new NewMemoryFact("alpha", "region", "us-east", null, null,
            [new MemorySource(MemorySourceKind.Document, open)]), new MemoryCaller(alice, null, null));

        // Locked and extended by another transaction, then committed while the forget waits.
        Guid replacement = Guid.CreateVersion7();
        await using KnowledgeDbContext correction = await factory.CreateDbContextAsync();
        await using var transaction = await correction.Database.BeginTransactionAsync();
        await correction.Database.ExecuteSqlAsync($"SELECT id FROM memory_facts WHERE id = {first.Id} FOR UPDATE");
        await correction.Database.ExecuteSqlAsync($"UPDATE memory_facts SET superseded_at = now() WHERE id = {first.Id}");
        correction.MemoryFacts.Add(new MemoryFactEntity
        {
            Id = replacement,
            ChainId = first.ChainId,
            ContainerId = container,
            Subject = "alpha",
            Attribute = "region",
            Value = "eu-west",
            RecordedAt = DateTimeOffset.UtcNow,
            ReplacesId = first.Id,
            AuthorUserId = alice,
            Sources = [new MemoryFactSourceEntity { FactId = replacement, Ordinal = 0, Kind = (short)MemorySourceKind.Document, Ref = open }],
        });
        await correction.SaveChangesAsync();

        List<int> chainSizes = [];
        Task<int?> forget = store.ForgetAsync(container, first.Id, chain =>
        {
            chainSizes.Add(chain.Count);
            return Task.FromResult(true);
        });
        await WaitUntilBlockedOnLockAsync(factory);
        await transaction.CommitAsync();
        int? deleted = await forget.WaitAsync(TimeSpan.FromSeconds(30));

        deleted.Should().Be(2);
        chainSizes.Should().EndWith(2, "the approved chain must be the one deleted");
        await using var check = await factory.CreateDbContextAsync();
        (await check.MemoryFacts.AnyAsync(f => f.ChainId == first.ChainId)).Should().BeFalse();
    }

    /// <summary>Waits until some session is waiting on a row lock of memory_facts, so the race is set, not hoped for.</summary>
    private static async Task WaitUntilBlockedOnLockAsync(IDbContextFactory<KnowledgeDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        DateTime until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            int waiting = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_stat_activity
                WHERE wait_event_type = 'Lock' AND query LIKE '%memory_facts%' AND pid <> pg_backend_pid()
                """).SingleAsync();
            if (waiting > 0)
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The forget never waited on the correction's lock.");
    }
}
