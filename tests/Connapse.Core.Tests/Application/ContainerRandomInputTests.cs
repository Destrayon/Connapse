using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Containers;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Mcp;
using CsCheck;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The container operations' rules over random inputs, as OperationRandomInputTests checks the others: who
/// is refused, what is never written on a refusal, and what a success records. Each expectation is worked
/// out here from the rule, not by calling the code under test. Every sample builds its own fakes, because
/// CsCheck runs samples on several threads.
/// </summary>
[Trait("Category", "Unit")]
public class ContainerRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Gen<T?> Maybe<T>(Gen<T> gen) where T : class =>
        Gen.Frequency((1, Gen.Const((T?)null)), (3, gen.Select(v => (T?)v)));

    // Spaces at either end, capitals and hyphens, so trimming and every part of the name rule matter.
    private static readonly Gen<string> Name = Gen.String[Gen.Char[" aAb-1"], 0, 5];

    private static readonly Gen<OperationContext> AnyCaller =
        Gen.Enum<OperationSurface>().Select(s => new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), s));

    /// <summary>A stored container, named as the store keeps names: lowercase, trimmed.</summary>
    private static readonly Gen<Container> AnyContainer = Gen.Select(Gen.Guid, Gen.String[Gen.Char["ab-1"], 2, 4],
        (id, name) => new Container(id.ToString(), $"c{name}x", null, Epoch, Epoch));

    /// <summary>The rule written out: 2-128 characters of a-z, 0-9 and hyphens, starting and ending with a letter or digit.</summary>
    private static bool IsValidName(string name) =>
        name.Length is >= 2 and <= 128
        && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
        && name[0] != '-' && name[^1] != '-';

    private sealed class World
    {
        public readonly IContainerStore Containers = Substitute.For<IContainerStore>();
        public readonly IDocumentStore Documents = Substitute.For<IDocumentStore>();
        public readonly IAuditLogger Audit = Substitute.For<IAuditLogger>();
        public readonly IOperationAuthorizer Authorizer = Substitute.For<IOperationAuthorizer>();

        public World(bool reader, bool writer)
        {
            Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Reader).Returns(reader);
            Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Writer).Returns(writer);
        }

        /// <summary>The store holds <paramref name="container"/> when <paramref name="exists"/>, by id and by name.</summary>
        public void Holds(Container container, bool exists)
        {
            var id = Guid.Parse(container.Id);
            Containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(exists ? container : null);
            Containers.GetByNameAsync(container.Name, Arg.Any<CancellationToken>()).Returns(exists ? container : null);
            Containers.ExistsAsync(id, Arg.Any<CancellationToken>()).Returns(exists);
        }

        public bool Wrote =>
            Containers.ReceivedCalls().Any(c => c.GetMethodInfo().Name is "CreateAsync" or "UpdateAsync" or "DeleteAsync" or "SaveSettingsOverridesAsync")
            || Audit.ReceivedCalls().Any();
    }

    /// <summary>A key to look a container up by: its id, a stranger's id, its name in any case and spacing, another name, or blank.</summary>
    private static Gen<ContainerKey> KeyFor(Container container) => Gen.OneOf(
        Gen.Const(ContainerKey.ById(Guid.Parse(container.Id))),
        Gen.Guid.Select(ContainerKey.ById),
        Gen.Const(ContainerKey.ByName($" {container.Name.ToUpperInvariant()} ")),
        Gen.Const(ContainerKey.ByName(container.Name)),
        Name.Select(ContainerKey.ByName),
        Gen.Const(ContainerKey.ByName("  ")));

    private static bool Names(ContainerKey key, Container container) =>
        key.Id is Guid id ? id.ToString() == container.Id
        : !string.IsNullOrWhiteSpace(key.Name) && string.Equals(key.Name.Trim(), container.Name, StringComparison.OrdinalIgnoreCase);

    // ── containers.list ───────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(ListContainers))]
    public Task List_AnyPage_AnswersThatSliceAndWhetherMoreFollow() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Int[-2, 6], Gen.Int[-1, 205].Nullable(), Gen.Int[0, 8])
            .SampleAsync(async t =>
            {
                var (caller, reader, skip, take, stored) = t;
                var world = new World(reader, writer: false);
                var all = Enumerable.Range(0, stored).Select(i => new Container(Guid.NewGuid().ToString(), $"c{i}", null, Epoch, Epoch)).ToList();
                world.Containers.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(c => all.Skip(c.ArgAt<int>(0)).Take(c.ArgAt<int>(1)).ToList());

                var listed = await new ListContainers(world.Containers, world.Authorizer).RunAsync(caller, new ListContainersQuery(skip, take));

                ContainerStatus expected = !reader ? ContainerStatus.Forbidden
                    : skip < 0 || take is < 1 or > 200 ? ContainerStatus.InvalidPage
                    : ContainerStatus.Success;
                listed.Status.Should().Be(expected);
                if (expected != ContainerStatus.Success)
                {
                    world.Containers.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                var page = all.Skip(skip).Take(take ?? int.MaxValue).ToList();
                listed.Result!.Items.Should().Equal(page);
                listed.Result.HasMore.Should().Be(take is int size && stored - skip > size);
            }, iter: 2000);

    // ── containers.get ────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(GetContainer))]
    public Task Get_AnyKey_FindsOnlyTheContainerItNames() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool)
            .SelectMany(t => KeyFor(t.Item3).Select(key => (t.Item1, t.Item2, t.Item3, t.Item4, key)))
            .SampleAsync(async t =>
            {
                var (caller, reader, container, exists, key) = t;
                var world = new World(reader, writer: false);
                world.Holds(container, exists);

                var read = await new GetContainer(world.Containers, world.Authorizer).RunAsync(caller, key);

                ContainerStatus expected = !reader ? ContainerStatus.Forbidden
                    : exists && Names(key, container) ? ContainerStatus.Success
                    : ContainerStatus.NotFound;
                read.Status.Should().Be(expected);
                if (expected == ContainerStatus.Success)
                    read.Result.Should().Be(container);
                if (!reader)
                    world.Containers.ReceivedCalls().Should().BeEmpty();
            }, iter: 2000);

    // ── containers.create ─────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(CreateContainer))]
    public Task Create_AnyName_SavesOnlyAValidFreeNameAndAuditsIt() =>
        Gen.Select(AnyCaller, Gen.Bool, Name, Maybe(Name), Gen.Bool, Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, writer, name, description, taken, raced) = t;
                var world = new World(reader: true, writer);
                string trimmed = name.Trim();
                world.Containers.GetByNameAsync(trimmed, Arg.Any<CancellationToken>())
                    .Returns(taken ? new Container(Guid.NewGuid().ToString(), trimmed, null, Epoch, Epoch) : null);
                world.Containers.CreateAsync(Arg.Any<CreateContainerRequest>(), Arg.Any<CancellationToken>()).Returns(c =>
                    raced
                        ? throw new InvalidOperationException("Container already exists")
                        : new Container(Guid.NewGuid().ToString(), c.Arg<CreateContainerRequest>().Name, c.Arg<CreateContainerRequest>().Description, Epoch, Epoch));

                var created = await new CreateContainer(world.Containers, world.Audit, world.Authorizer)
                    .RunAsync(caller, new CreateContainerRequest(name, description));

                ContainerStatus expected = !writer ? ContainerStatus.Forbidden
                    : !IsValidName(trimmed) ? ContainerStatus.Invalid
                    : taken || raced ? ContainerStatus.Conflict
                    : ContainerStatus.Success;
                created.Status.Should().Be(expected);
                if (expected is ContainerStatus.Invalid or ContainerStatus.Conflict)
                    created.Field.Should().Be("name");
                if (expected != ContainerStatus.Success)
                {
                    world.Audit.ReceivedCalls().Should().BeEmpty();
                    if (expected != ContainerStatus.Conflict || !raced || taken)
                        await world.Containers.DidNotReceiveWithAnyArgs().CreateAsync(default!);
                    return;
                }
                // What is saved is trimmed, and the audit row names the saved container.
                string? savedDescription = description?.Trim();
                await world.Containers.Received(1).CreateAsync(
                    Arg.Is<CreateContainerRequest>(r => r.Name == trimmed && r.Description == savedDescription), Arg.Any<CancellationToken>());
                created.Result!.Name.Should().Be(trimmed);
                await world.Audit.Received(1).LogAsync("container.created", "container", created.Result.Id, Arg.Any<object?>(), Arg.Any<CancellationToken>());
            }, iter: 2000);

    // ── containers.rename ─────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(RenameContainer))]
    public Task Rename_AnyChange_SavesOnlyAValidChangeToAFreeName() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, Maybe(Name), Maybe(Name), Gen.Bool, Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, writer, container, exists, name, description, taken, raced) = t;
                var world = new World(reader: true, writer);
                var id = Guid.Parse(container.Id);
                world.Containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(exists ? container : null);
                string? newName = name?.Trim();
                if (newName is not null && newName != container.Name)
                    world.Containers.GetByNameAsync(newName, Arg.Any<CancellationToken>())
                        .Returns(taken ? new Container(Guid.NewGuid().ToString(), newName, null, Epoch, Epoch) : null);
                world.Containers.UpdateAsync(id, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(c =>
                    raced
                        ? throw new InvalidOperationException("Container already exists")
                        : container with { Name = c.ArgAt<string?>(1) ?? container.Name, Description = c.ArgAt<string?>(2) ?? container.Description });

                var renamed = await new RenameContainer(world.Containers, world.Audit, world.Authorizer)
                    .RunAsync(caller, id, new ContainerChanges(name, description));

                bool takenByAnother = newName is not null && newName != container.Name && taken;
                ContainerStatus expected = !writer ? ContainerStatus.Forbidden
                    : name is null && description is null ? ContainerStatus.Invalid
                    : newName is not null && !IsValidName(newName) ? ContainerStatus.Invalid
                    : !exists ? ContainerStatus.NotFound
                    : takenByAnother || raced ? ContainerStatus.Conflict
                    : ContainerStatus.Success;
                renamed.Status.Should().Be(expected);
                if (expected != ContainerStatus.Success)
                {
                    world.Audit.ReceivedCalls().Should().BeEmpty();
                    if (expected is not ContainerStatus.Conflict || takenByAnother)
                        await world.Containers.DidNotReceiveWithAnyArgs().UpdateAsync(default, default, default);
                    return;
                }
                await world.Containers.Received(1).UpdateAsync(id, newName, description?.Trim(), Arg.Any<CancellationToken>());
                await world.Audit.Received(1).LogAsync("container.updated", "container", container.Id, Arg.Any<object?>(), Arg.Any<CancellationToken>());
            }, iter: 2000);

    // ── containers.delete ─────────────────────────────────────────────────────────────────────────

    /// <summary>What the store does with a delete: removes it, finds nothing, or refuses one with documents.</summary>
    public enum DeleteAnswer { Deleted, Gone, NotEmpty }

    [Fact]
    [RandomInputTestOf(typeof(DeleteContainer))]
    public Task Delete_AnyKey_DeletesOnlyTheNamedEmptyContainer() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, Gen.Enum<DeleteAnswer>())
            .SelectMany(t => KeyFor(t.Item3).Select(key => (t.Item1, t.Item2, t.Item3, t.Item4, t.Item5, key)))
            .SampleAsync(async t =>
            {
                var (caller, writer, container, exists, answer, key) = t;
                var world = new World(reader: true, writer);
                world.Holds(container, exists);
                var id = Guid.Parse(container.Id);
                if (answer == DeleteAnswer.NotEmpty)
                    world.Containers.DeleteAsync(id, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("Container has 3 documents"));
                else
                    world.Containers.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(answer == DeleteAnswer.Deleted);

                var deleted = await new DeleteContainer(world.Containers, world.Audit, world.Authorizer).RunAsync(caller, key);

                bool named = exists && Names(key, container);
                ContainerStatus expected = !writer ? ContainerStatus.Forbidden
                    : !named ? ContainerStatus.NotFound
                    : answer switch
                    {
                        DeleteAnswer.NotEmpty => ContainerStatus.NotEmpty,
                        DeleteAnswer.Gone => ContainerStatus.NotFound,
                        _ => ContainerStatus.Success,
                    };
                deleted.Status.Should().Be(expected);
                if (!named)
                    await world.Containers.DidNotReceiveWithAnyArgs().DeleteAsync(default);
                if (expected == ContainerStatus.Success)
                    await world.Audit.Received(1).LogAsync("container.deleted", "container", container.Id, Arg.Any<object?>(), Arg.Any<CancellationToken>());
                else
                    world.Audit.ReceivedCalls().Should().BeEmpty();
            }, iter: 2000);

    // ── containers.stats ──────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(GetContainerStats))]
    public Task Stats_AnyKey_CountsOnlyTheNamedContainer() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, Gen.Int[0, 9])
            .SelectMany(t => KeyFor(t.Item3).Select(key => (t.Item1, t.Item2, t.Item3, t.Item4, t.Item5, key)))
            .SampleAsync(async t =>
            {
                var (caller, reader, container, exists, documents, key) = t;
                var world = new World(reader, writer: false);
                world.Holds(container, exists);
                var stats = new ContainerStats(documents, documents, 0, 0, documents * 3, 0, null);
                world.Documents.GetContainerStatsAsync(Guid.Parse(container.Id), Arg.Any<CancellationToken>()).Returns(stats);

                var read = await new GetContainerStats(world.Containers, world.Documents, McpCaller.NoModels(), world.Authorizer).RunAsync(caller, key);

                ContainerStatus expected = !reader ? ContainerStatus.Forbidden
                    : exists && Names(key, container) ? ContainerStatus.Success
                    : ContainerStatus.NotFound;
                read.Status.Should().Be(expected);
                if (expected == ContainerStatus.Success)
                    read.Result.Should().BeEquivalentTo(new { Container = container, Stats = stats });
                else
                    world.Documents.ReceivedCalls().Should().BeEmpty();
            }, iter: 2000);

    // ── containers.settings.* ─────────────────────────────────────────────────────────────────────

    private static readonly Gen<ContainerSettingsOverrides> AnyOverrides = Gen.Select(Gen.Bool, Gen.Bool, Gen.Int[1, 50],
        (chunking, search, topK) => new ContainerSettingsOverrides
        {
            Chunking = chunking ? new ChunkingSettings() : null,
            Search = search ? new SearchSettings { TopK = topK } : null,
        });

    [Fact]
    [RandomInputTestOf(typeof(GetContainerSettings))]
    public Task GetSettings_AnyContainer_AnswersItsOverridesOrNoneForAReader() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, Maybe(AnyOverrides))
            .SampleAsync(async t =>
            {
                var (caller, reader, container, exists, saved) = t;
                var world = new World(reader, writer: false);
                world.Holds(container, exists);
                var id = Guid.Parse(container.Id);
                world.Containers.GetSettingsOverridesAsync(id, Arg.Any<CancellationToken>()).Returns(saved);

                var read = await new GetContainerSettings(world.Containers, world.Authorizer).RunAsync(caller, id);

                ContainerStatus expected = !reader ? ContainerStatus.Forbidden : exists ? ContainerStatus.Success : ContainerStatus.NotFound;
                read.Status.Should().Be(expected);
                if (expected == ContainerStatus.Success)
                    read.Result.Should().Be(saved ?? new ContainerSettingsOverrides());
                if (!reader)
                    world.Containers.ReceivedCalls().Should().BeEmpty();
            }, iter: 2000);

    [Fact]
    [RandomInputTestOf(typeof(UpdateContainerSettings))]
    public Task UpdateSettings_AnyOverrides_SavesThemOnlyForAWriterOfAnExistingContainer() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, AnyOverrides)
            .SampleAsync(async t =>
            {
                var (caller, writer, container, exists, overrides) = t;
                var world = new World(reader: true, writer);
                world.Holds(container, exists);
                var id = Guid.Parse(container.Id);

                var saved = await new UpdateContainerSettings(world.Containers, world.Audit, world.Authorizer).RunAsync(caller, id, overrides);

                ContainerStatus expected = !writer ? ContainerStatus.Forbidden : exists ? ContainerStatus.Success : ContainerStatus.NotFound;
                saved.Status.Should().Be(expected);
                if (expected != ContainerStatus.Success)
                {
                    world.Wrote.Should().BeFalse();
                    return;
                }
                saved.Result.Should().Be(overrides);
                await world.Containers.Received(1).SaveSettingsOverridesAsync(id, overrides, Arg.Any<CancellationToken>());
                await world.Audit.Received(1).LogAsync("container.settings_updated", "container", container.Id, Arg.Any<object?>(), Arg.Any<CancellationToken>());
            }, iter: 2000);

    [Fact]
    [RandomInputTestOf(typeof(ResetContainerSettings))]
    public Task ResetSettings_AnyContainer_ClearsEveryOverrideOnlyForAWriter() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, writer, container, exists) = t;
                var world = new World(reader: true, writer);
                world.Holds(container, exists);
                var id = Guid.Parse(container.Id);

                var cleared = await new ResetContainerSettings(world.Containers, world.Audit, world.Authorizer).RunAsync(caller, id);

                ContainerStatus expected = !writer ? ContainerStatus.Forbidden : exists ? ContainerStatus.Success : ContainerStatus.NotFound;
                cleared.Status.Should().Be(expected);
                if (expected != ContainerStatus.Success)
                {
                    world.Wrote.Should().BeFalse();
                    return;
                }
                await world.Containers.Received(1).SaveSettingsOverridesAsync(id, new ContainerSettingsOverrides(), Arg.Any<CancellationToken>());
                await world.Audit.Received(1).LogAsync("container.settings_reset", "container", container.Id, Arg.Any<object?>(), Arg.Any<CancellationToken>());
            }, iter: 2000);
}
