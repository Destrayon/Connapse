using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using IDocumentAccess = Connapse.Core.Interfaces.IDocumentAccess;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data.Entities;
using Connapse.Integration.Tests.Authorization;
using Connapse.Storage.Data;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// CON-239's Copy to team against the real store: only the current version of a personal fact is copied, with its
/// citations, as a new fact the copier wrote; the personal fact is left as it was; read-only callers and anyone but the
/// space's owner are refused; and a teammate who can't read a cited document doesn't see the copy.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemoryCopyTests(SharedWebAppFixture fixture)
{
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

    /// <summary>One run's people, spaces and documents, and the operations wired to the real store.</summary>
    private sealed class World : IAsyncDisposable
    {
        public required AsyncServiceScope Scope { get; init; }
        public required IContainerStore Containers { get; init; }
        public required IMemoryService Memory { get; init; }
        public required IAuditLogger Audit { get; init; }
        public required HashSet<ClaimsPrincipal> Writers { get; init; }
        public required Container Team { get; init; }
        public List<Guid> Cleanup { get; } = [];

        public CopyMemoryFact Copy(IOperationAuthorizer authorizer) =>
            new(Containers, Memory, new RememberFact(Containers, Memory, authorizer), Audit, authorizer);

        public CopyMemoryFact Copy() => Copy(Authorizer());

        public IOperationAuthorizer Authorizer()
        {
            IOperationAuthorizer authorizer = Substitute.For<IOperationAuthorizer>();
            authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>())
                .Returns(call => Writers.Contains(call.Arg<ClaimsPrincipal>()) || call.ArgAt<string>(1) is Policies.Viewer or Policies.Reader or Policies.KnowledgeRead);
            return authorizer;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (Guid id in Cleanup)
                await Containers.DeleteAsync(id);
            await Scope.DisposeAsync();
        }
    }

    private static readonly Guid Owner = Guid.NewGuid(), Teammate = Guid.NewGuid(), Blind = Guid.NewGuid(), ReadOnly = Guid.NewGuid();
    private static readonly string Runbook = Guid.NewGuid().ToString(), Secret = Guid.NewGuid().ToString();

    private static ClaimsPrincipal Person(Guid id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Name, id.ToString("N")[..8])], "test"));

    private static readonly ClaimsPrincipal OwnerUser = Person(Owner), TeammateUser = Person(Teammate), BlindUser = Person(Blind), ReadOnlyUser = Person(ReadOnly);

    private static OperationContext As(ClaimsPrincipal user) => new(user, OperationSurface.Rest);

    private static MemoryCaller CallerOf(Guid id) => new(id, null, null);

    private async Task<World> WorldAsync()
    {
        AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        IContainerStore containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var memory = new MemoryService(new PostgresMemoryStore(factory, TimeProvider.System),
            new FakeAccess(new()
            {
                [Owner] = [Runbook, Secret],
                [Teammate] = [Runbook, Secret],
                [Blind] = [Runbook],
                [ReadOnly] = [Runbook, Secret],
            }),
            new NoLinks(), Substitute.For<IAuditLogger>(), NullLogger<MemoryService>.Instance);
        Container team = await containers.CreateAsync(new CreateContainerRequest($"copy-team-{Guid.NewGuid():N}"[..32], null, ContainerKind.Memory));
        var world = new World
        {
            Scope = scope,
            Containers = containers,
            Memory = memory,
            Audit = Substitute.For<IAuditLogger>(),
            Writers = [OwnerUser, TeammateUser, BlindUser],
            Team = team,
        };
        world.Cleanup.Add(Guid.Parse(team.Id));
        return world;
    }

    /// <summary>A personal fact citing the runbook and the secret, corrected once, in a new personal space of <paramref name="owner"/>.</summary>
    private static async Task<(Container Space, MemoryFact First, MemoryFact Current)> CorrectedFactAsync(World world, Guid owner)
    {
        Container space = await world.Containers.EnsurePersonalSpaceAsync(CallerOf(owner), $"copy-mine-{Guid.NewGuid():N}"[..32], null);
        world.Cleanup.Add(Guid.Parse(space.Id));
        MemorySource[] cites = [new(MemorySourceKind.Document, Runbook), new(MemorySourceKind.Document, Secret)];
        MemoryWriteResult first = await world.Memory.RememberAsync(Guid.Parse(space.Id),
            new NewMemoryFact("importer", "retry limit", "3 (my guess)", null, null, cites, "private hunch", MemoryScope.Personal), CallerOf(owner));
        first.Error.Should().BeNull();
        MemoryWriteResult corrected = await world.Memory.CorrectAsync(first.Fact!.Id, MemoryCorrection.Wrong,
            new NewMemoryFact("importer", "retry limit", "5", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), null, null, "Set in the runbook"),
            CallerOf(owner), teamWriter: true);
        corrected.Error.Should().BeNull();
        return (space, first.Fact, corrected.Fact!);
    }

    private static async Task<IReadOnlyList<RecalledFact>> CurrentAsync(World world, string space, Guid reader) =>
        (await world.Memory.ListAsync(Guid.Parse(space), new MemoryFactFilter(), MemoryFactSort.Recorded, null, 100, CallerOf(reader))).Facts;

    [Fact]
    public async Task RunAsync_CorrectedPersonalFact_CopiesOnlyTheCurrentVersionWithItsCitations()
    {
        await using World world = await WorldAsync();
        (Container mine, MemoryFact first, MemoryFact current) = await CorrectedFactAsync(world, Owner);

        Attempt<MemoryCopied, MemoryStatus> copied = await world.Copy().RunAsync(As(OwnerUser), first.Id.ToString(), world.Team.Name);

        copied.Succeeded.Should().BeTrue(copied.Detail);
        MemoryFact copy = copied.Result!.Copy;
        copied.Result.CopiedFrom.Should().Be(current.Id, "the current version is copied even when an older version's id is given");
        copy.Id.Should().NotBe(current.Id);
        copy.ContainerId.Should().Be(Guid.Parse(world.Team.Id));
        copy.Scope.Should().Be(MemoryScope.Team);
        (copy.Subject, copy.Attribute, copy.Value, copy.ValidFrom, copy.ValidTo, copy.Reason)
            .Should().Be((current.Subject, current.Attribute, current.Value, current.ValidFrom, current.ValidTo, current.Reason));
        copy.Sources.Should().Equal(current.Sources);
        copy.Author.UserId.Should().Be(Owner);
        copy.ReplacesId.Should().BeNull("the team fact's history starts at the copy");

        MemoryHistoryPage history = await world.Memory.HistoryAsync(Guid.Parse(world.Team.Id), copy.Id, null, 100, CallerOf(Teammate));
        history.Versions.Select(v => v.Fact.Value).Should().Equal(["5"], "the earlier personal version stays private");
        await world.Audit.Received(1).LogAsync("memory_fact.copied", "memory_fact", copy.Id.ToString(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        JsonSerializer.Serialize(world.Audit.ReceivedCalls().Single().GetArguments()[3])
            .Should().NotContain("importer").And.NotContain("retry").And.Contain(current.Id.ToString()).And.Contain(copy.Id.ToString());
    }

    [Fact]
    public async Task RunAsync_Copied_LeavesThePersonalFactUnchanged()
    {
        await using World world = await WorldAsync();
        (Container mine, _, MemoryFact current) = await CorrectedFactAsync(world, Owner);
        MemoryHistoryPage before = await world.Memory.HistoryAsync(Guid.Parse(mine.Id), current.Id, null, 100, CallerOf(Owner));

        (await world.Copy().RunAsync(As(OwnerUser), current.Id.ToString(), world.Team.Id)).Succeeded.Should().BeTrue();

        MemoryHistoryPage after = await world.Memory.HistoryAsync(Guid.Parse(mine.Id), current.Id, null, 100, CallerOf(Owner));
        after.Versions.Should().BeEquivalentTo(before.Versions, "the personal fact and its history are left as they were");
        after.Versions.Should().OnlyContain(v => v.Fact.Scope == MemoryScope.Personal && v.Fact.ContainerId == Guid.Parse(mine.Id));
        (await CurrentAsync(world, mine.Id, Teammate)).Should().BeEmpty("the personal fact is still the owner's alone");
    }

    [Fact]
    public async Task RunAsync_ReadOnlyOwner_IsRefused()
    {
        await using World world = await WorldAsync();
        (_, _, MemoryFact current) = await CorrectedFactAsync(world, ReadOnly);

        Attempt<MemoryCopied, MemoryStatus> copied = await world.Copy().RunAsync(As(ReadOnlyUser), current.Id.ToString(), world.Team.Id);

        copied.Status.Should().Be(MemoryStatus.Forbidden);
        (await CurrentAsync(world, world.Team.Id, Teammate)).Should().BeEmpty();
        (await world.Copy().DestinationsAsync(As(ReadOnlyUser))).Should().BeEmpty("a read-only user has nowhere to copy to");
    }

    [Fact]
    public async Task RunAsync_SomeoneElsesPersonalFact_IsNotFound()
    {
        await using World world = await WorldAsync();
        (_, _, MemoryFact current) = await CorrectedFactAsync(world, Owner);

        Attempt<MemoryCopied, MemoryStatus> copied = await world.Copy().RunAsync(As(TeammateUser), current.Id.ToString(), world.Team.Id);

        copied.Status.Should().Be(MemoryStatus.FactNotFound, "a fact in someone else's personal space reads as missing");
        (await CurrentAsync(world, world.Team.Id, Teammate)).Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_TeamFact_IsRefused()
    {
        await using World world = await WorldAsync();
        MemoryWriteResult team = await world.Memory.RememberAsync(Guid.Parse(world.Team.Id),
            new NewMemoryFact("importer", "owner", "Pat", null, null, null, Scope: MemoryScope.Team), CallerOf(Owner));

        Attempt<MemoryCopied, MemoryStatus> copied = await world.Copy().RunAsync(As(OwnerUser), team.Fact!.Id.ToString(), world.Team.Id);

        copied.Status.Should().Be(MemoryStatus.Invalid);
        copied.Detail.Should().Contain("personal spaces");
    }

    [Fact]
    public async Task RunAsync_ToAPersonalSpace_IsRefused()
    {
        await using World world = await WorldAsync();
        (_, _, MemoryFact current) = await CorrectedFactAsync(world, Owner);
        Container other = await world.Containers.EnsurePersonalSpaceAsync(CallerOf(Owner), $"copy-other-{Guid.NewGuid():N}"[..32], null);
        world.Cleanup.Add(Guid.Parse(other.Id));

        Attempt<MemoryCopied, MemoryStatus> copied = await world.Copy().RunAsync(As(OwnerUser), current.Id.ToString(), other.Id);

        copied.Status.Should().Be(MemoryStatus.Invalid);
        copied.Field.Should().Be("destination");
    }

    [Fact]
    public async Task RunAsync_Copied_IsHiddenFromATeammateWhoCantReadACitedDocument()
    {
        await using World world = await WorldAsync();
        (_, _, MemoryFact current) = await CorrectedFactAsync(world, Owner);

        MemoryFact copy = (await world.Copy().RunAsync(As(OwnerUser), current.Id.ToString(), world.Team.Id)).Result!.Copy;

        (await CurrentAsync(world, world.Team.Id, Teammate)).Select(f => f.Fact.Id).Should().Equal(copy.Id);
        (await CurrentAsync(world, world.Team.Id, Blind)).Should().BeEmpty("they can't read the secret document the fact cites");
    }

    [Fact]
    public async Task RunAsync_DestinationHasTheSameFact_IsRefusedUnlessAnyway()
    {
        await using World world = await WorldAsync();
        (_, _, MemoryFact current) = await CorrectedFactAsync(world, Owner);
        MemoryFact theirs = (await world.Memory.RememberAsync(Guid.Parse(world.Team.Id),
            new NewMemoryFact("Importer", "Retry Limit", "4", null, null, null, Scope: MemoryScope.Team), CallerOf(Teammate))).Fact!;
        CopyMemoryFact copy = world.Copy();

        Attempt<MemoryCopyPlan, MemoryStatus> plan = await copy.PlanAsync(As(OwnerUser), current.Id.ToString(), world.Team.Id);
        Attempt<MemoryCopied, MemoryStatus> refused = await copy.RunAsync(As(OwnerUser), current.Id.ToString(), world.Team.Id);
        Attempt<MemoryCopied, MemoryStatus> anyway = await copy.RunAsync(As(OwnerUser), current.Id.ToString(), world.Team.Id, anyway: true);

        plan.Result!.Existing!.Fact.Id.Should().Be(theirs.Id);
        refused.Status.Should().Be(MemoryStatus.Exists);
        anyway.Succeeded.Should().BeTrue();
        (await CurrentAsync(world, world.Team.Id, Teammate)).Select(f => f.Fact.Value)
            .Should().BeEquivalentTo(["4", "5"], "the teammate's fact is not overwritten or corrected");
    }

    [Fact]
    public async Task CopyEndpoint_OverRest_CopiesForAnEditorAndRefusesAViewer()
    {
        using HttpClient editor = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Editor);
        using HttpClient viewer = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Viewer);
        string team = $"copy-rest-{Guid.NewGuid():N}"[..32];
        string mine = $"copy-rest-mine-{Guid.NewGuid():N}"[..32];
        (await fixture.AdminClient.PostAsJsonAsync("/api/memory/spaces", new { name = team, kind = "team" })).EnsureSuccessStatusCode();
        string mineId = await IdOf(await editor.PostAsJsonAsync("/api/memory/spaces", new { name = mine, kind = "personal" }));
        string teamId = (await ListSpacesAsync(fixture.AdminClient)).Single(s => s.Name == team).Id;
        try
        {
            string factId;
            await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
            {
                IMemoryService memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
                Guid editorId = (await scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>().FindByEmailAsync("editor@authz.connapse.io"))!.Id;
                MemoryWriteResult saved = await memory.RememberAsync(Guid.Parse(mineId),
                    new NewMemoryFact("pager", "rotation", "weekly", null, null, null, Scope: MemoryScope.Personal), new MemoryCaller(editorId, null, null));
                factId = saved.Fact!.Id.ToString();
            }

            (await viewer.PostAsJsonAsync($"/api/memory/facts/{factId}/copy", new { destination = teamId }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            HttpResponseMessage copied = await editor.PostAsJsonAsync($"/api/memory/facts/{factId}/copy", new { destination = teamId });
            copied.StatusCode.Should().Be(HttpStatusCode.OK, await copied.Content.ReadAsStringAsync());
            (await editor.PostAsJsonAsync($"/api/memory/facts/{factId}/copy", new { destination = teamId }))
                .StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await fixture.AdminClient.PostAsJsonAsync($"/api/memory/facts/{factId}/copy", new { destination = teamId }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound, "only the space's owner can copy from it");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/memory/spaces/{teamId}");
            await editor.DeleteAsync($"/api/memory/spaces/{mineId}");
        }
    }

    private sealed record SpaceRow(string Id, string Name);

    private static async Task<string> IdOf(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<IReadOnlyList<SpaceRow>> ListSpacesAsync(HttpClient client)
    {
        JsonElement root = JsonDocument.Parse(await client.GetStringAsync("/api/memory/spaces?limit=100")).RootElement;
        return [.. root.GetProperty("items").EnumerateArray().Select(e => new SpaceRow(e.GetProperty("id").GetString()!, e.GetProperty("name").GetString()!))];
    }
}
