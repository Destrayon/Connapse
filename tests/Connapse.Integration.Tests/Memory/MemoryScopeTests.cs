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

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// Personal and team memory against the real store (CON-234): a personal fact is seen only by its author and their
/// agents whatever it cites, a correction of a team fact keeps every restriction it had, sharing needs the sole
/// author, and someone with read access only can keep personal facts but not write team ones.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemoryScopeTests(SharedWebAppFixture fixture) : IAsyncLifetime
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

    private async Task<Guid> ContainerAsync()
    {
        var factory = _scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid container = Guid.NewGuid();
        await using var db = await factory.CreateDbContextAsync();
        db.Containers.Add(new ContainerEntity { Id = container, Name = $"scope-{Guid.NewGuid():N}", Kind = ContainerKind.Memory });
        await db.SaveChangesAsync();
        return container;
    }

    private async Task<(Guid Container, MemoryService Service)> ArrangeAsync(Dictionary<Guid, HashSet<string>> readable)
    {
        var factory = _scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var service = new MemoryService(new PostgresMemoryStore(factory, TimeProvider.System), new FakeAccess(readable),
            new NoLinks(), Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);
        return (await ContainerAsync(), service);
    }

    private static async Task<IReadOnlyList<string>> SeenAsync(MemoryService service, Guid container, MemoryCaller caller) =>
        [.. (await service.RecallAsync(new MemoryQuery(container, History: true, Limit: 100), caller)).Facts.Select(f => f.Fact.Value).Order()];

    [Fact]
    public async Task Remember_PersonalFactCitingAPublicDocument_IsSeenOnlyByItsAuthorAndTheirAgents()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        string shared = Guid.NewGuid().ToString();
        var (container, service) = await ArrangeAsync(new() { [a] = [shared], [b] = [shared] });
        MemoryCaller userA = new(a, null, null), agentOfA = new(a, Guid.NewGuid(), "pat"), userB = new(b, null, null), agentOfB = new(b, Guid.NewGuid(), "pat");

        MemoryWriteResult written = await service.RememberAsync(container,
            new NewMemoryFact("Halcyon", "database", "PostgreSQL", Jan, null, [new(MemorySourceKind.Document, shared)], null, MemoryScope.Personal), userA);

        written.Fact!.Scope.Should().Be(MemoryScope.Personal);
        (await SeenAsync(service, container, userA)).Should().Equal("PostgreSQL");
        (await SeenAsync(service, container, agentOfA)).Should().Equal("PostgreSQL");
        (await SeenAsync(service, container, userB)).Should().BeEmpty("B can read the document, but the fact is A's own");
        (await SeenAsync(service, container, agentOfB)).Should().BeEmpty();
    }

    [Fact]
    public async Task Correct_TeamFactCitingARestrictedDocument_StaysHiddenFromPeopleWhoCannotReadIt()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        string shared = Guid.NewGuid().ToString(), restricted = Guid.NewGuid().ToString();
        var (container, service) = await ArrangeAsync(new() { [a] = [shared, restricted], [b] = [shared] });
        MemoryCaller userA = new(a, null, null), userB = new(b, null, null);
        MemorySource sharedSource = new(MemorySourceKind.Document, shared), restrictedSource = new(MemorySourceKind.Document, restricted);
        MemoryFact first = (await service.RememberAsync(container,
            new NewMemoryFact("Halcyon", "budget", "$1M", Jan, null, [sharedSource, restrictedSource]), userA)).Fact!;
        first.Scope.Should().Be(MemoryScope.Team);

        // Naming no sources keeps the old ones; naming only the shared one would show the new value to B, so it is refused.
        MemoryWriteResult kept = await service.CorrectAsync(first.Id, MemoryCorrection.Changed,
            new NewMemoryFact("", "", "$2M", Jan.AddMonths(1), null, null), userA, true);
        MemoryWriteResult widened = await service.CorrectAsync(kept.Fact!.Id, MemoryCorrection.Changed,
            new NewMemoryFact("", "", "$3M", Jan.AddMonths(2), null, [sharedSource]), userA, true);

        kept.Fact.Scope.Should().Be(MemoryScope.Team);
        kept.Fact.Sources.Should().BeEquivalentTo([sharedSource, restrictedSource]);
        widened.Fact.Should().BeNull();
        widened.Error.Should().Contain(restricted);
        (await SeenAsync(service, container, userA)).Should().Equal("$1M", "$2M");
        (await SeenAsync(service, container, userB)).Should().BeEmpty();
    }

    [Fact]
    public async Task Operations_ReadOnlyUser_SavesNoMemoryButRecallsTeamFacts()
    {
        Guid container = await ContainerAsync();
        var sp = _scope.ServiceProvider;
        static OperationContext As(Guid user, string role) => new(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.ToString()), new Claim(ClaimTypes.Role, role)], "test")), OperationSurface.Rest);
        var viewer = As(Guid.NewGuid(), "Viewer");
        var editor = As(Guid.NewGuid(), "Editor");
        var memory = sp.GetRequiredService<MemoryOperations>();
        // CON-235: a team fact citing nothing names its space; the old containerId form would make it personal.
        var shared = await memory.Remember.RunAsync(editor, MemoryTarget.Space(container.ToString()),
            new NewMemoryFact("Halcyon", "database", "PostgreSQL 15", null, null, [], null, MemoryScope.Team));
        var own = await memory.Remember.RunAsync(editor, container.ToString(),
            new NewMemoryFact("me", "editor", "vim", null, null, [], null, MemoryScope.Personal));
        string fact = shared.Result!.Fact.Id.ToString();

        var personal = await memory.Remember.RunAsync(viewer, container.ToString(),
            new NewMemoryFact("me", "editor", "vim", null, null, [], null, MemoryScope.Personal));
        var team = await memory.Remember.RunAsync(viewer, container.ToString(),
            new NewMemoryFact("Halcyon", "editor", "vim", null, null, [], null, MemoryScope.Team));
        var corrected = await memory.Correct.RunAsync(viewer, container.ToString(), fact, MemoryCorrection.Wrong,
            new NewMemoryFact("", "", "MySQL 8", null, null, null));
        var forgotten = await memory.Forget.RunAsync(viewer, container.ToString(), fact);
        var recalled = await memory.Recall.RunAsync(viewer, container.ToString(), new MemoryQuery(container));

        personal.Status.Should().Be(MemoryStatus.Forbidden, "read access alone saves no memory, personal or team");
        team.Status.Should().Be(MemoryStatus.Forbidden);
        corrected.Status.Should().Be(MemoryStatus.Forbidden);
        forgotten.Status.Should().Be(MemoryStatus.Forbidden);
        own.Status.Should().Be(MemoryStatus.Success);
        recalled.Status.Should().Be(MemoryStatus.Success);
        recalled.Result!.Select(f => f.Fact.Value).Should().Equal(["PostgreSQL 15"], "a viewer recalls the team fact, not the editor's personal one");
    }
}
