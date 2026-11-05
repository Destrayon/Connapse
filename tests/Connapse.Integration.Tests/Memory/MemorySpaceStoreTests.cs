using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Memory;
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
using IOperationAuthorizer = Connapse.Application.Ports.IOperationAuthorizer;

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

    /// <summary>A space as listed, with its exact values.</summary>
    private sealed record Listed(Container Container, int Facts, DateTimeOffset UpdatedAt, bool Starred);

    /// <summary>
    /// Every space the viewer may see, the way the list settles them: candidates, then a probe for each documents container
    /// in doubt, then an exact tally of all of them. Null when access could not be checked.
    /// </summary>
    private static async Task<IReadOnlyList<Listed>?> ListAsync(PostgresMemorySpaceStore store, MemoryCaller viewer, string? text)
    {
        IReadOnlyList<MemorySpaceCandidate> candidates = await store.CandidatesAsync(viewer, text);
        string[] doubtful = [.. candidates.Where(c => c.Container.Kind == ContainerKind.Documents && c.FactsAtLeast == 0).Select(c => c.Container.Id)];
        if (await store.HoldingReadableAsync(viewer, doubtful) is not { } holding)
            return null;
        List<MemorySpaceCandidate> listed = [.. candidates.Where(c => !doubtful.Contains(c.Container.Id) || holding.Contains(c.Container.Id))];
        if (await store.TallyAsync(viewer, [.. listed.Select(c => c.Container.Id)]) is not { } tallies)
            return null;
        return [.. listed.Select(c => new Listed(c.Container, tallies[c.Container.Id].Facts, tallies[c.Container.Id].UpdatedAt, c.Starred))];
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

        IReadOnlyList<Listed> seenByA = (await ListAsync(Store(access), userA, tag))!;
        IReadOnlyList<Listed> seenByB = (await ListAsync(Store(access), userB, tag))!;

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
        Listed teamRow = seenByA.Single(s => s.Container.Id == team.ToString());
        teamRow.UpdatedAt.Should().BeOnOrAfter(teamRow.Container.UpdatedAt, "the newest fact moves the space's updated time");
    }

    [Fact]
    public async Task List_WithEnded_AddsDocumentContainersWhoseVisibleFactsHaveAllEnded()
    {
        string tag = $"spaces-{Guid.NewGuid():N}";
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        string restricted = Guid.NewGuid().ToString();
        FakeAccess access = new(new() { [a] = [restricted] });
        MemoryService memory = Memory(access);
        MemoryCaller userA = new(a, null, null), userB = new(b, null, null);
        Guid ended = await ContainerAsync($"{tag}-ended", ContainerKind.Documents);
        Guid endedRestricted = await ContainerAsync($"{tag}-ended-restricted", ContainerKind.Documents);
        DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-10), to = DateTimeOffset.UtcNow.AddDays(-5);
        NewMemoryFact old = Fact("old", MemoryScope.Team) with { ValidFrom = from, ValidTo = to };
        NewMemoryFact secret = Fact("secret", MemoryScope.Team, new MemorySource(MemorySourceKind.Document, restricted)) with { ValidFrom = from, ValidTo = to };
        (await memory.RememberAsync(ended, old, userA)).Fact.Should().NotBeNull();
        (await memory.RememberAsync(endedRestricted, secret, userA)).Fact.Should().NotBeNull();

        (await Store(access).ListAsync(userA, tag))!.Should().BeEmpty("neither holds a current fact");
        (await Store(access).ListAsync(userA, tag, withEnded: true))!.Select(s => (s.Container.Name, s.Facts)).Should()
            .BeEquivalentTo([($"{tag}-ended", 0), ($"{tag}-ended-restricted", 0)]);
        (await Store(access).ListAsync(userB, tag, withEnded: true))!.Select(s => s.Container.Name).Should()
            .Equal([$"{tag}-ended"], "B can't read the source the other container's only fact cites");
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
            first.Id, MemoryCorrection.Changed, new NewMemoryFact("", "", "v2", DateTimeOffset.UtcNow.AddMinutes(-1), null, null), userA, true);
        corrected.Fact.Should().NotBeNull(corrected.Error);

        IReadOnlyList<Listed> seen = (await ListAsync(Store(access), userA, tag))!;
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

        IReadOnlyList<Listed> seen = (await ListAsync(Store(new FakeAccess([])), new MemoryCaller(Guid.NewGuid(), null, null), $"{tag}-a%b"))!;

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

        IReadOnlyList<Listed> seen = (await ListAsync(Store(access), user, $"{tag}-match"))!;

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

        (await ListAsync(Store(new FailingAccess()), user, tag)).Should().BeNull();
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

        (await ListAsync(store, a, tag))!.Single().Starred.Should().BeTrue();
        (await ListAsync(store, agentOfA, tag))!.Single().Starred.Should().BeTrue("an agent acting for A shares A's stars");
        (await ListAsync(store, b, tag))!.Single().Starred.Should().BeFalse();

        await store.UnstarAsync(team, agentOfA);
        await store.UnstarAsync(team, a);

        (await ListAsync(store, a, tag))!.Single().Starred.Should().BeFalse();
    }

    private ListMemorySpaces Operation(IDocumentAccess access)
    {
        IOperationAuthorizer authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        return new ListMemorySpaces(Store(access), authorizer);
    }

    private static OperationContext As(Guid user) => new(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test")), OperationSurface.Rest);

    [Fact]
    public async Task List_HiddenDocuments_CountsStayCorrectForEachReader()
    {
        string tag = $"spaces-{Guid.NewGuid():N}";
        Guid writer = Guid.NewGuid(), a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        string forA = Guid.NewGuid().ToString(), forB = Guid.NewGuid().ToString();
        FakeAccess access = new(new() { [writer] = [forA, forB], [a] = [forA], [b] = [forB] });
        MemoryService memory = Memory(access);
        MemoryCaller author = new(writer, null, null);
        Guid team = await ContainerAsync($"{tag}-team", ContainerKind.Memory);
        Guid onlyA = await ContainerAsync($"{tag}-only-a", ContainerKind.Documents);
        Guid mixed = await ContainerAsync($"{tag}-mixed", ContainerKind.Documents);
        MemorySource docA = new(MemorySourceKind.Document, forA), docB = new(MemorySourceKind.Document, forB);
        await memory.RememberAsync(team, Fact("open", MemoryScope.Team), author);
        await memory.RememberAsync(team, Fact("a's", MemoryScope.Team, docA), author);
        await memory.RememberAsync(team, Fact("b's", MemoryScope.Team, docB), author);
        await memory.RememberAsync(onlyA, Fact("a only", MemoryScope.Team, docA), author);
        // The newest fact, so the one checked first, is one B can't read; B still sees the older one.
        await memory.RememberAsync(mixed, Fact("older", MemoryScope.Team, docB), author);
        await memory.RememberAsync(mixed, Fact("newer", MemoryScope.Team, docA), author);

        async Task<Dictionary<string, int>> SeenBy(Guid reader, MemorySpaceCounts counts)
        {
            MemorySpacePage page = (await Operation(access).RunAsync(As(reader), new MemorySpaceQuery(Q: tag))).Result!;
            page.Counts.Should().Be(counts);
            page.Total.Should().Be(counts.All);
            return page.Items.ToDictionary(i => i.Name[(tag.Length + 1)..], i => i.Facts);
        }

        (await SeenBy(a, new MemorySpaceCounts(3, 0, 0, 1, 2))).Should().Equal(
            new Dictionary<string, int> { ["team"] = 2, ["only-a"] = 1, ["mixed"] = 1 });
        (await SeenBy(b, new MemorySpaceCounts(2, 0, 0, 1, 1))).Should().Equal(
            new Dictionary<string, int> { ["team"] = 2, ["mixed"] = 1 },
            "B's checked fact in mixed is hidden, so the whole container is checked and its other fact found");
        (await SeenBy(c, new MemorySpaceCounts(1, 0, 0, 1, 0))).Should().Equal(
            new Dictionary<string, int> { ["team"] = 1 }, "every documents fact cites a document C can't read");
    }

    [Fact]
    public async Task List_ChecksOnlyThePagesSourcesAndOneFactPerDocumentsContainer()
    {
        string tag = $"spaces-{Guid.NewGuid():N}";
        MemoryCaller user = new(Guid.NewGuid(), null, null);
        Dictionary<string, string[]> cites = new()
        {
            ["a"] = [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()],
            ["b"] = [Guid.NewGuid().ToString()],
            ["c"] = [Guid.NewGuid().ToString()],
            ["docs"] = [Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString()],
        };
        MemoryService memory = Memory(new FakeAccess(new() { [user.UserId!.Value] = [.. cites.Values.SelectMany(d => d)] }));
        foreach ((string name, string[] documents) in cites)
        {
            Guid space = await ContainerAsync($"{tag}-{name}", name == "docs" ? ContainerKind.Documents : ContainerKind.Memory);
            foreach (string document in documents)
                await memory.RememberAsync(space, Fact(document, MemoryScope.Team, new MemorySource(MemorySourceKind.Document, document)), user);
        }
        RecordingAccess access = new();

        MemorySpacePage page = (await Operation(access).RunAsync(As(user.UserId!.Value),
            new MemorySpaceQuery(Q: tag, Sort: MemorySpaceSort.Name, Limit: 1))).Result!;

        page.Items.Should().ContainSingle().Which.Facts.Should().Be(2);
        page.Counts.Should().Be(new MemorySpaceCounts(4, 0, 0, 3, 1));
        access.Asked.Should().BeEquivalentTo([.. cites["a"], cites["docs"][^1]],
            "only the page's space is counted, and the documents container is settled by its newest fact");
    }

    [Fact]
    public async Task Candidates_FactsCitingNothing_AreTheLowerBound()
    {
        string tag = $"spaces-{Guid.NewGuid():N}";
        MemoryCaller a = new(Guid.NewGuid(), null, null), b = new(Guid.NewGuid(), null, null);
        string cited = Guid.NewGuid().ToString();
        RecordingAccess access = new();
        MemoryService memory = Memory(new FakeAccess(new() { [a.UserId!.Value] = [cited] }));
        Guid team = await ContainerAsync($"{tag}-team", ContainerKind.Memory);
        await memory.RememberAsync(team, Fact("one", MemoryScope.Team), a);
        await memory.RememberAsync(team, Fact("two", MemoryScope.Team), a);
        await memory.RememberAsync(team, Fact("cited", MemoryScope.Team, new MemorySource(MemorySourceKind.Document, cited)), a);
        await memory.RememberAsync(team, Fact("mine", MemoryScope.Personal), a);

        MemorySpaceCandidate seenByA = (await Store(access).CandidatesAsync(a, tag)).Single();
        MemorySpaceCandidate seenByB = (await Store(access).CandidatesAsync(b, tag)).Single();

        (seenByA.FactsAtLeast, seenByA.FactsAtMost).Should().Be((3, 4));
        (seenByB.FactsAtLeast, seenByB.FactsAtMost).Should().Be((2, 3), "A's personal fact is never a candidate for B");
        seenByA.UpdatedAtLeast.Should().BeOnOrBefore(seenByA.UpdatedAtMost);
        access.Asked.Should().BeEmpty("candidates check no source");
    }
}
