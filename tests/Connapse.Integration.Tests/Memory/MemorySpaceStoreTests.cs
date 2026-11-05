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
/// The space store against real tables (CON-240): each listed space counts only the current facts the viewer may see,
/// a document container is listed only once it holds one, personal spaces stay with their owner, stars are per person,
/// and a delete's impact counts only the facts the viewer may read.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemorySpaceStoreTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private readonly AsyncServiceScope _scope = fixture.Factory.Services.CreateAsyncScope();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _scope.DisposeAsync();

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

    private sealed class RecordingAccess : IDocumentAccess
    {
        public HashSet<string> Asked { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlySet<string>> ReadableAsync(Guid? userId, IReadOnlyCollection<string> documentIds, CancellationToken ct = default)
        {
            Asked.UnionWith(documentIds);
            return Task.FromResult<IReadOnlySet<string>>(documentIds.ToHashSet(StringComparer.Ordinal));
        }
    }

    private sealed class FailingAccess : IDocumentAccess
    {
        public Task<IReadOnlySet<string>> ReadableAsync(Guid? userId, IReadOnlyCollection<string> documentIds, CancellationToken ct = default) =>
            throw new InvalidOperationException("access store down");
    }

    private sealed class NoLinks : ILinkIndex
    {
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<ILookup<string, string>> NodesOfAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ILookup<string, string>> DocumentsOfAsync(IReadOnlyCollection<string> nodeIds, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<(string, string)>().ToLookup(p => p.Item1, p => p.Item2));
    }

    private IDbContextFactory<KnowledgeDbContext> Factory => _scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();

    private PostgresMemorySpaceStore Store(IDocumentAccess access) =>
        new(Factory, access, new NoLinks(), TimeProvider.System, NullLogger<PostgresMemorySpaceStore>.Instance);

    private MemoryService Memory(IDocumentAccess access) =>
        new(new PostgresMemoryStore(Factory, TimeProvider.System), access, new NoLinks(), Substitute.For<IAuditLogger>(),
            NullLogger<MemoryService>.Instance);

    private async Task<Guid> ContainerAsync(string name, ContainerKind kind)
    {
        Guid id = Guid.NewGuid();
        await using KnowledgeDbContext db = await Factory.CreateDbContextAsync();
        db.Containers.Add(new ContainerEntity
        {
            Id = id,
            Name = name,
            Kind = kind,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static NewMemoryFact Fact(string value, MemoryScope scope, params MemorySource[] sources) =>
        new("Halcyon", value, value, null, null, sources, null, scope);

    [Fact]
    public async Task List_CountsOnlyCurrentFactsTheViewerMaySee()
    {
        string tag = $"spaces-{Guid.NewGuid():N}";
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        string restricted = Guid.NewGuid().ToString();
        FakeAccess access = new(new() { [a] = [restricted] });
        MemoryService memory = Memory(access);
        MemoryCaller userA = new(a, null, null), userB = new(b, null, null);
        Guid team = await ContainerAsync($"{tag}-team", ContainerKind.Memory);
        Guid documents = await ContainerAsync($"{tag}-docs", ContainerKind.Documents);
        Guid emptyDocuments = await ContainerAsync($"{tag}-empty-docs", ContainerKind.Documents);

        await memory.RememberAsync(team, Fact("open", MemoryScope.Team), userA);
        await memory.RememberAsync(team, Fact("hidden", MemoryScope.Team, new MemorySource(MemorySourceKind.Document, restricted)), userA);
        await memory.RememberAsync(team, Fact("mine", MemoryScope.Personal), userA);
        await memory.RememberAsync(documents, Fact("docs", MemoryScope.Team), userA);
        Container personal = (await Store(access).CreatePersonalAsync(userA, $"{tag}-own", "notes"))!;

        IReadOnlyList<MemorySpaceSummary> seenByA = (await Store(access).ListAsync(userA, tag))!;
        IReadOnlyList<MemorySpaceSummary> seenByB = (await Store(access).ListAsync(userB, tag))!;

        seenByA.ToDictionary(s => s.Container.Name, s => s.Facts).Should().Equal(new Dictionary<string, int>
        {
            [$"{tag}-team"] = 3,
            [$"{tag}-docs"] = 1,
            [$"{tag}-own"] = 0,
        });
        seenByB.ToDictionary(s => s.Container.Name, s => s.Facts).Should().Equal(new Dictionary<string, int>
        {
            [$"{tag}-team"] = 1,
            [$"{tag}-docs"] = 1,
        }, "B can't read the restricted source, A's personal fact is A's, and A's space is A's");
        seenByA.Should().NotContain(s => s.Container.Id == emptyDocuments.ToString(), "a document container is listed once it holds memory");
        seenByA.Single(s => s.Container.Id == personal.Id).Container.Audience.Should().Be(MemoryScope.Personal);
        seenByA.Single(s => s.Container.Id == documents.ToString()).Container.Kind.Should().Be(ContainerKind.Documents);
        MemorySpaceSummary teamRow = seenByA.Single(s => s.Container.Id == team.ToString());
        teamRow.UpdatedAt.Should().BeOnOrAfter(teamRow.Container.UpdatedAt, "the newest fact moves the space's updated time");
    }

    [Fact]
    public async Task List_CorrectedFactCountsOnce()
    {
        string tag = $"spaces-{Guid.NewGuid():N}";
        Guid a = Guid.NewGuid();
        FakeAccess access = new([]);
        MemoryService memory = Memory(access);
        MemoryCaller userA = new(a, null, null);
        Guid team = await ContainerAsync($"{tag}-team", ContainerKind.Memory);
        MemoryFact first = (await memory.RememberAsync(team, Fact("v1", MemoryScope.Team), userA)).Fact!;
        MemoryWriteResult corrected = await memory.CorrectAsync(
            team, first.Id, MemoryCorrection.Changed, new NewMemoryFact("", "", "v2", DateTimeOffset.UtcNow.AddMinutes(-1), null, null), userA, true);
        corrected.Fact.Should().NotBeNull(corrected.Error);

        IReadOnlyList<MemorySpaceSummary> seen = (await Store(access).ListAsync(userA, tag))!;
        MemorySpaceImpact? impact = await Store(access).ImpactAsync(team, userA);

        seen.Single().Facts.Should().Be(1);
        impact.Should().Be(new MemorySpaceImpact(Facts: 1, Versions: 2, Authors: 1));
    }

    [Fact]
    public async Task Impact_CountsOnlyFactsTheViewerMayRead()
    {
        string tag = $"spaces-{Guid.NewGuid():N}";
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        string restricted = Guid.NewGuid().ToString();
        FakeAccess access = new(new() { [a] = [restricted] });
        MemoryService memory = Memory(access);
        MemoryCaller userA = new(a, null, null), userB = new(b, null, null);
        Guid team = await ContainerAsync($"{tag}-team", ContainerKind.Memory);
        await memory.RememberAsync(team, Fact("open", MemoryScope.Team), userA);
        await memory.RememberAsync(team, Fact("hidden", MemoryScope.Team, new MemorySource(MemorySourceKind.Document, restricted)), userA);
        await memory.RememberAsync(team, Fact("mine", MemoryScope.Personal), userA);

        MemorySpaceImpact? seenByA = await Store(access).ImpactAsync(team, userA);
        MemorySpaceImpact? seenByB = await Store(access).ImpactAsync(team, userB);
        MemorySpaceImpact? unchecked_ = await Store(new FailingAccess()).ImpactAsync(team, userA);

        seenByA.Should().Be(new MemorySpaceImpact(Facts: 3, Versions: 3, Authors: 1));
        seenByB.Should().Be(new MemorySpaceImpact(Facts: 1, Versions: 1, Authors: 1),
            "B can't read the restricted source and A's personal fact is A's, so a dry run mustn't reveal them");
        unchecked_.Should().BeNull();
    }

    [Fact]
    public async Task List_TextIsMatchedLiterally()
    {
        string tag = $"spaces-{Guid.NewGuid():N}";
        await ContainerAsync($"{tag}-a%b", ContainerKind.Memory);
        await ContainerAsync($"{tag}-axb", ContainerKind.Memory);

        IReadOnlyList<MemorySpaceSummary> seen = (await Store(new FakeAccess([])).ListAsync(new MemoryCaller(Guid.NewGuid(), null, null), $"{tag}-a%b"))!;

        seen.Select(s => s.Container.Name).Should().Equal($"{tag}-a%b");
    }

    [Fact]
    public async Task List_WithText_ChecksOnlyTheMatchingSpacesSources()
    {
        string tag = $"spaces-{Guid.NewGuid():N}";
        string wanted = Guid.NewGuid().ToString(), unrelated = Guid.NewGuid().ToString();
        MemoryCaller user = new(Guid.NewGuid(), null, null);
        MemoryService memory = Memory(new FakeAccess(new() { [user.UserId!.Value] = [wanted, unrelated] }));
        Guid match = await ContainerAsync($"{tag}-match", ContainerKind.Memory);
        Guid other = await ContainerAsync($"{tag}-other", ContainerKind.Memory);
        await memory.RememberAsync(match, Fact("wanted", MemoryScope.Team, new MemorySource(MemorySourceKind.Document, wanted)), user);
        await memory.RememberAsync(other, Fact("unrelated", MemoryScope.Team, new MemorySource(MemorySourceKind.Document, unrelated)), user);
        RecordingAccess access = new();

        IReadOnlyList<MemorySpaceSummary> seen = (await Store(access).ListAsync(user, $"{tag}-match"))!;

        seen.Single().Facts.Should().Be(1);
        access.Asked.Should().Contain(wanted).And.NotContain(unrelated,
            "a narrow search mustn't spend live permission checks on spaces it won't list");
    }

    [Fact]
    public async Task List_AccessCheckFails_ReturnsNull()
    {
        string tag = $"spaces-{Guid.NewGuid():N}";
        Guid team = await ContainerAsync($"{tag}-team", ContainerKind.Memory);
        MemoryCaller user = new(Guid.NewGuid(), null, null);
        await Memory(new FakeAccess([])).RememberAsync(team, Fact("open", MemoryScope.Team), user);

        (await Store(new FailingAccess()).ListAsync(user, tag)).Should().BeNull();
    }

    [Fact]
    public async Task CreatePersonal_SameNameTwice_SecondIsNullButAnotherOwnerMayUseIt()
    {
        string name = $"own-{Guid.NewGuid():N}";
        PostgresMemorySpaceStore store = Store(new FakeAccess([]));
        MemoryCaller a = new(Guid.NewGuid(), null, null), soloAgent = new(null, Guid.NewGuid(), "bot");

        Container? first = await store.CreatePersonalAsync(a, name, null);
        Container? again = await store.CreatePersonalAsync(a, name, "other");
        Container? agents = await store.CreatePersonalAsync(soloAgent, name, null);

        first.Should().NotBeNull();
        first!.OwnerUserId.Should().Be(a.UserId);
        again.Should().BeNull();
        agents.Should().NotBeNull();
        agents!.OwnerAgentId.Should().Be(soloAgent.AgentId);
        agents.OwnerUserId.Should().BeNull();
    }

    [Fact]
    public async Task Star_IsPerPersonAndIdempotent()
    {
        string tag = $"spaces-{Guid.NewGuid():N}";
        Guid team = await ContainerAsync($"{tag}-team", ContainerKind.Memory);
        PostgresMemorySpaceStore store = Store(new FakeAccess([]));
        MemoryCaller a = new(Guid.NewGuid(), null, null), agentOfA = new(a.UserId, Guid.NewGuid(), "pat"), b = new(Guid.NewGuid(), null, null);

        await store.StarAsync(team, a);
        await store.StarAsync(team, agentOfA);

        (await store.ListAsync(a, tag))!.Single().Starred.Should().BeTrue();
        (await store.ListAsync(agentOfA, tag))!.Single().Starred.Should().BeTrue("an agent acting for A shares A's stars");
        (await store.ListAsync(b, tag))!.Single().Starred.Should().BeFalse();

        await store.UnstarAsync(team, agentOfA);
        await store.UnstarAsync(team, a);

        (await store.ListAsync(a, tag))!.Single().Starred.Should().BeFalse();
    }
}
