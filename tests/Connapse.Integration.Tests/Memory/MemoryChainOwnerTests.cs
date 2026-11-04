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
/// Forgetting deletes a whole correction chain and needs the caller to see every version, so a chain must never hold
/// versions that no one user can see all of. A correction is refused when the corrector cannot see every version
/// already in the chain (CON-232).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemoryChainOwnerTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private readonly AsyncServiceScope _scope = fixture.Factory.Services.CreateAsyncScope();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _scope.DisposeAsync();

    private static readonly DateTimeOffset Jan = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeAccess(Dictionary<Guid, HashSet<string>> readable) : IDocumentAccess
    {
        public Task<IReadOnlySet<string>> ReadableAsync(Guid? userId, IReadOnlyCollection<string> documentIds, CancellationToken ct = default)
        {
            IReadOnlySet<string> open = userId is { } u && readable.TryGetValue(u, out HashSet<string>? r)
                ? documentIds.Where(r.Contains).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            return Task.FromResult(open);
        }
    }

    private sealed class NoLinks : ILinkIndex
    {
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<ILookup<string, string>> NodesOfAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ILookup<string, string>> DocumentsOfAsync(IReadOnlyCollection<string> nodeIds, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2));
    }

    private async Task<(Guid Container, MemoryService Service, IDbContextFactory<KnowledgeDbContext> Factory)> ArrangeAsync(
        Dictionary<Guid, HashSet<string>> readable)
    {
        var factory = _scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid container = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Containers.Add(new ContainerEntity { Id = container, Name = $"owners-{Guid.NewGuid():N}" });
            await db.SaveChangesAsync();
        }
        var service = new MemoryService(new PostgresMemoryStore(factory, TimeProvider.System), new FakeAccess(readable),
            new NoLinks(), Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);
        return (container, service, factory);
    }

    private static async Task<int> FactsInAsync(IDbContextFactory<KnowledgeDbContext> factory, Guid container)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.MemoryFacts.CountAsync(f => f.ContainerId == container);
    }

    [Theory]
    [InlineData(MemoryCorrection.Changed)]
    [InlineData(MemoryCorrection.Wrong)]
    public async Task Correct_PrivatelyOfAChainHoldingAnotherUsersPrivateVersion_IsRefusedAndTheOwnerCanStillForget(MemoryCorrection kind)
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        string shared = Guid.NewGuid().ToString();
        var (container, service, factory) = await ArrangeAsync(new() { [a] = [shared], [b] = [shared] });
        MemoryCaller userA = new(a, null, null), userB = new(b, null, null);

        // A writes privately, then corrects it to cite a document both can read: the correction stays personal, so B sees none of it.
        MemoryFact own = (await service.RememberAsync(container, new NewMemoryFact("Halcyon", "database", "MySQL", Jan, null, []), userA)).Fact!;
        MemoryFact cited = (await service.CorrectAsync(own.Id, MemoryCorrection.Changed,
            new NewMemoryFact("", "", "PostgreSQL", Jan.AddMonths(1), null, [new(MemorySourceKind.Document, shared)]), userA, true)).Fact!;

        MemoryWriteResult privateCorrection = await service.CorrectAsync(cited.Id, kind,
            new NewMemoryFact("", "", "PostgreSQL 16", kind == MemoryCorrection.Changed ? Jan.AddMonths(2) : null, null, []), userB, true);

        privateCorrection.Error.Should().NotBeNull("B cannot see A's personal fact, so B cannot correct it");
        privateCorrection.Failure.Should().Be(MemoryWriteFailure.NotFound);
        (await FactsInAsync(factory, container)).Should().Be(2, "a refused correction writes nothing");
        (await service.ForgetAsync(cited.Id, userA, true)).Should().Be((2, MemoryWriteFailure.None), "A sees every version, so A can forget");
        (await FactsInAsync(factory, container)).Should().Be(0, "with its owner's versions forgotten the container holds no memory and can be deleted");
    }

    [Fact]
    public async Task Correct_CitingADocumentTheOtherOwnerCannotRead_IsRefusedToo()
    {
        // Not sourceless, but hidden from A all the same: A's private version and B's restricted one would lock the chain.
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        string shared = Guid.NewGuid().ToString(), onlyB = Guid.NewGuid().ToString();
        var (container, service, factory) = await ArrangeAsync(new() { [a] = [shared], [b] = [shared, onlyB] });
        MemoryCaller userA = new(a, null, null), userB = new(b, null, null);
        MemoryFact own = (await service.RememberAsync(container, new NewMemoryFact("Halcyon", "lead", "Ana", Jan, null, []), userA)).Fact!;
        MemoryFact cited = (await service.CorrectAsync(own.Id, MemoryCorrection.Changed,
            new NewMemoryFact("", "", "Bo", Jan.AddMonths(1), null, [new(MemorySourceKind.Document, shared)]), userA, true)).Fact!;

        MemoryWriteResult result = await service.CorrectAsync(cited.Id, MemoryCorrection.Changed,
            new NewMemoryFact("", "", "Cy", Jan.AddMonths(2), null, [new(MemorySourceKind.Document, onlyB)]), userB, true);

        result.Error.Should().NotBeNull();
        (await FactsInAsync(factory, container)).Should().Be(2);
        (await service.ForgetAsync(own.Id, userA, true)).Deleted.Should().Be(2);
    }

    [Fact]
    public async Task Correct_OfASharedChain_ByAnotherUser_StillWorksAndEitherCanForget()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        string shared = Guid.NewGuid().ToString();
        var (container, service, factory) = await ArrangeAsync(new() { [a] = [shared], [b] = [shared] });
        MemoryCaller userA = new(a, null, null), userB = new(b, null, null);
        MemorySource source = new(MemorySourceKind.Document, shared);
        MemoryFact first = (await service.RememberAsync(container, new NewMemoryFact("Halcyon", "region", "eu-west", Jan, null, [source]), userA)).Fact!;

        MemoryWriteResult byB = await service.CorrectAsync(first.Id, MemoryCorrection.Wrong,
            new NewMemoryFact("", "", "eu-north", null, null, [source]), userB, true);
        MemoryWriteResult byA = await service.CorrectAsync(byB.Fact!.Id, MemoryCorrection.Changed,
            new NewMemoryFact("", "", "us-east", Jan.AddMonths(3), null, [source]), userA, true);

        byB.Error.Should().BeNull("B sees every version of a chain that cites only what B can read");
        byA.Error.Should().BeNull();
        (await service.ForgetAsync(first.Id, userB, true)).Deleted.Should().Be(3);
        (await FactsInAsync(factory, container)).Should().Be(0);
    }

    [Fact]
    public async Task Correct_OfOwnPrivateChain_StillWorks()
    {
        Guid a = Guid.NewGuid();
        var (container, service, _) = await ArrangeAsync([]);
        MemoryCaller userA = new(a, null, null);
        MemoryFact first = (await service.RememberAsync(container, new NewMemoryFact("Halcyon", "owner", "Ana", Jan, null, []), userA)).Fact!;

        MemoryWriteResult wrong = await service.CorrectAsync(first.Id, MemoryCorrection.Wrong,
            new NewMemoryFact("", "", "Ann", null, null, []), userA, true);
        MemoryWriteResult changed = await service.CorrectAsync(wrong.Fact!.Id, MemoryCorrection.Changed,
            new NewMemoryFact("", "", "Bo", Jan.AddMonths(1), null, []), userA, true);

        wrong.Error.Should().BeNull();
        changed.Error.Should().BeNull();
        (await service.ForgetAsync(first.Id, userA, true)).Deleted.Should().Be(3);
    }
}
