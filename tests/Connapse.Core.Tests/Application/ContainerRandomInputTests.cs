using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Containers;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Mcp;
using CsCheck;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
[Trait("Size", "Medium")]
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

    // ── containers.reindex ────────────────────────────────────────────────────────────────────────

    private static readonly Gen<ContainerReindexQuery> AnyReindex = Gen.Select(
        Gen.Bool.Nullable(), Gen.Bool.Nullable(), Gen.Enum<ChunkingStrategy>().Nullable(), Gen.Bool.Nullable(), Gen.Int[-2, 5].Nullable(),
        Gen.OneOf(Gen.Const((string?)null), Gen.Const((string?)"page-2"), Gen.Guid.Select(g => (string?)g.ToString())),
        (force, detect, strategy, dryRun, max, after) => new ContainerReindexQuery(force, detect, strategy, dryRun, max, after));

    [Fact]
    [RandomInputTestOf(typeof(ReindexContainer))]
    public Task Reindex_AnyRequest_AsksForThisContainerOnlyWhenItsPageIsValid() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, AnyReindex)
            .SampleAsync(async t =>
            {
                var (caller, writer, container, exists, query) = t;
                var world = new World(reader: true, writer);
                world.Holds(container, exists);
                var id = Guid.Parse(container.Id);
                var reindex = Substitute.For<IReindexService>();
                reindex.ReindexAsync(Arg.Any<ReindexOptions>(), Arg.Any<CancellationToken>()).Returns(new ReindexResult { BatchId = "b" });

                var ran = await new ReindexContainer(world.Containers, reindex, world.Authorizer).RunAsync(caller, id, query);

                (ContainerStatus Status, string? Field) expected = !writer ? (ContainerStatus.Forbidden, null)
                    : !exists ? (ContainerStatus.NotFound, null)
                    : query.MaxDocuments is <= 0 ? (ContainerStatus.InvalidReindex, "maxDocuments")
                    : query.ContinueAfter is not null && !Guid.TryParse(query.ContinueAfter, out _) ? (ContainerStatus.InvalidReindex, "continueAfter")
                    : (ContainerStatus.Success, null);
                ran.Status.Should().Be(expected.Status);
                ran.Field.Should().Be(expected.Field);
                if (expected.Status != ContainerStatus.Success)
                {
                    reindex.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                // What wasn't asked for takes the service's defaults: no force, settings changes detected, no dry run.
                await reindex.Received(1).ReindexAsync(Arg.Is<ReindexOptions>(o =>
                    o.ContainerId == container.Id
                    && o.Force == (query.Force ?? false)
                    && o.DetectSettingsChanges == (query.DetectSettingsChanges ?? true)
                    && o.DryRun == (query.DryRun ?? false)
                    && o.Strategy == query.Strategy
                    && o.MaxDocuments == query.MaxDocuments
                    && o.ContinueAfter == query.ContinueAfter), Arg.Any<CancellationToken>());
            }, iter: 2000);

    // ── containers.retryFailed ────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(RetryFailedContainerDocuments))]
    public Task RetryFailed_AnyContainer_RetriesAndAuditsOnlyForAWriterOfAnExistingOne() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, Gen.Int[0, 6])
            .SampleAsync(async t =>
            {
                var (caller, writer, container, exists, failed) = t;
                var world = new World(reader: true, writer);
                world.Holds(container, exists);
                var id = Guid.Parse(container.Id);
                var reindex = Substitute.For<IReindexService>();
                reindex.RetryFailedAsync(id, Arg.Any<CancellationToken>()).Returns(failed);
                var scopes = new ServiceCollection().AddSingleton(reindex).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

                var retried = await new RetryFailedContainerDocuments(world.Containers, scopes, world.Audit, world.Authorizer).RunAsync(caller, id);

                ContainerStatus expected = !writer ? ContainerStatus.Forbidden : exists ? ContainerStatus.Success : ContainerStatus.NotFound;
                retried.Status.Should().Be(expected);
                if (expected != ContainerStatus.Success)
                {
                    reindex.ReceivedCalls().Should().BeEmpty();
                    world.Audit.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                retried.Result.Should().Be(failed);
                await world.Audit.Received(1).LogAsync("container.retry_failed", "container", container.Id, Arg.Any<object?>(), Arg.Any<CancellationToken>());
            }, iter: 2000);

    // ── containers.reconcile ──────────────────────────────────────────────────────────────────────

    /// <summary>A stored object: one of a few names, with or without its leading slash, and whether a document already has its path.</summary>
    private static readonly Gen<(string Path, bool Known)[]> AnyStoredFiles =
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Bool, Gen.Bool, Gen.Bool, Gen.Bool, (a, b, c, slashA, slashB, known) =>
                new[] { (a, "notes.md", slashA), (b, "deck.pdf", slashB), (c, "raw.bin", !slashA) }
                    .Where(f => f.Item1)
                    .Select((f, i) => ((f.Item3 ? "/" : "") + f.Item2, known && i == 0))
                    .ToArray());

    /// <summary>The content type a reconcile gives each sample file's extension; an unknown one has none.</summary>
    private static readonly Dictionary<string, string?> TypeOf = new() { [".md"] = "text/markdown", [".pdf"] = "application/pdf", [".bin"] = null };

    [Fact]
    [RandomInputTestOf(typeof(ReconcileContainer))]
    public Task Reconcile_AnyStoredFiles_RegistersAndQueuesOnlyThoseWithNoDocument() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, AnyStoredFiles)
            .SampleAsync(async t =>
            {
                var (caller, writer, container, exists, files) = t;
                var world = new World(reader: true, writer);
                world.Holds(container, exists);
                var id = Guid.Parse(container.Id);
                var connector = Substitute.For<IConnector>();
                connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(files.Select(f => new ConnectorFile(f.Path, 1, Epoch, null)).ToList());
                var storage = Substitute.For<IManagedStorageProvider>();
                storage.CreateConnector(container.Id).Returns(connector);
                static string Rooted(string path) => path.StartsWith('/') ? path : "/" + path;
                world.Documents.ListAsync(id, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(files.Where(f => f.Known).Select(f => new Document(Guid.NewGuid().ToString(), container.Id, "x", null, Rooted(f.Path), 1, Epoch, [])).ToList());
                var queue = Substitute.For<IIngestionQueue>();
                var chunking = Substitute.For<IOptionsMonitor<ChunkingSettings>>();
                chunking.CurrentValue.Returns(new ChunkingSettings());

                var synced = await new ReconcileContainer(world.Containers, storage, world.Documents, queue, chunking, world.Audit, world.Authorizer)
                    .RunAsync(caller, id);

                ContainerStatus expected = !writer ? ContainerStatus.Forbidden : exists ? ContainerStatus.Success : ContainerStatus.NotFound;
                synced.Status.Should().Be(expected);
                if (expected != ContainerStatus.Success)
                {
                    storage.ReceivedCalls().Should().BeEmpty();
                    queue.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                var fresh = files.Where(f => !f.Known).Select(f => Rooted(f.Path)).ToList();
                synced.Result.Should().BeEquivalentTo(new { TotalFiles = files.Length, EnqueuedCount = fresh.Count, SkippedCount = files.Length - fresh.Count });
                // Each new object gets a document at its rooted path and one job in the reconcile's batch; a known one gets neither.
                var stored = world.Documents.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "StoreAsync")
                    .Select(c => (Document)c.GetArguments()[0]!).ToList();
                stored.Select(d => d.Path).Should().BeEquivalentTo(fresh);
                // Its type from the extension, and marked as found by a reconcile rather than uploaded.
                stored.Should().OnlyContain(d => d.ContentType == TypeOf[Path.GetExtension(d.Path)] && d.Metadata["Source"] == "ConnectorSync");
                var jobs = queue.ReceivedCalls().Select(c => (IngestionJob)c.GetArguments()[0]!).ToList();
                jobs.Should().OnlyContain(j => j.BatchId == synced.Result!.BatchId && j.Options.ContentType == TypeOf[Path.GetExtension(j.Options.Path!)]
                    && j.Options.Metadata!["OriginalFileName"] == Path.GetFileName(j.Options.Path) && j.Options.Metadata["Source"] == "ConnectorSync");
                jobs.Select(j => j.Options.Path).Should().BeEquivalentTo(fresh);
                await world.Audit.Received(1).LogAsync("container.synced", "container", container.Id, Arg.Any<object?>(), Arg.Any<CancellationToken>());
            }, iter: 1000);

    // ── containers.summaries.* ────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(RegenerateContainerSummaries))]
    public Task RegenerateSummaries_AnyCaller_QueuesARollupOnlyForAnAdministrator() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, AnyContainer, Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, admin, writer, container, exists) = t;
                var world = new World(reader: true, writer);
                world.Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Admin).Returns(admin);
                world.Holds(container, exists);
                var id = Guid.Parse(container.Id);
                var rollup = Substitute.For<ISummaryRollup>();
                rollup.Enqueue(id).Returns("17");

                var queued = await new RegenerateContainerSummaries(world.Containers, rollup, world.Audit, world.Authorizer).RunAsync(caller, id);

                // An Editor may write containers but not regenerate: the Summary tab has always been for administrators.
                ContainerStatus expected = !admin ? ContainerStatus.Forbidden : exists ? ContainerStatus.Success : ContainerStatus.NotFound;
                queued.Status.Should().Be(expected);
                if (expected != ContainerStatus.Success)
                {
                    rollup.ReceivedCalls().Should().BeEmpty();
                    world.Audit.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                queued.Result.Should().Be(new SummaryRegeneration("17"));
                await world.Audit.Received(1).LogAsync("container.summaries_regenerated", "container", container.Id, Arg.Any<object?>(), Arg.Any<CancellationToken>());
            }, iter: 2000);

    [Fact]
    [RandomInputTestOf(typeof(ClearContainerSummaries))]
    public Task ClearSummaries_AnyCaller_ClearsOnlyForAnAdministrator() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, AnyContainer, Gen.Bool, Gen.Int[0, 9])
            .SampleAsync(async t =>
            {
                var (caller, admin, writer, container, exists, summaries) = t;
                var world = new World(reader: true, writer);
                world.Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Admin).Returns(admin);
                world.Holds(container, exists);
                var id = Guid.Parse(container.Id);
                world.Documents.ClearDocumentSummariesAsync(id, Arg.Any<CancellationToken>()).Returns(summaries);

                var cleared = await new ClearContainerSummaries(world.Containers, world.Documents, world.Audit, world.Authorizer).RunAsync(caller, id);

                ContainerStatus expected = !admin ? ContainerStatus.Forbidden : exists ? ContainerStatus.Success : ContainerStatus.NotFound;
                cleared.Status.Should().Be(expected);
                if (expected != ContainerStatus.Success)
                {
                    world.Documents.ReceivedCalls().Should().BeEmpty();
                    await world.Containers.DidNotReceiveWithAnyArgs().UpdateSummaryAsync(default, default, default, default);
                    world.Audit.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                cleared.Result.Should().Be(summaries);
                await world.Containers.Received(1).UpdateSummaryAsync(id, null, null, null, Arg.Any<CancellationToken>());
                await world.Audit.Received(1).LogAsync("container.summaries_cleared", "container", container.Id, Arg.Any<object?>(), Arg.Any<CancellationToken>());
            }, iter: 2000);
}
