using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The memory operations' rules over random inputs: who is refused, that a refused or unresolved call never reaches
/// memory, and how memory's typed failures become statuses. Each expectation is worked out here from the rule. Every
/// sample builds its own fakes, because CsCheck runs samples on several threads.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class MemoryOperationRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Gen<OperationContext> AnyCaller =
        Gen.Enum<OperationSurface>().Select(s => new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), s));

    private static readonly Gen<Container> AnyContainer = Gen.Select(Gen.Guid, Gen.String[Gen.Char["ab-1"], 2, 4],
        (id, name) => new Container(id.ToString(), $"c{name}x", null, Epoch, Epoch));

    /// <summary>How a call names the container: its id, its name in any case and spacing, a stranger's id, another name, or blank.</summary>
    private static Gen<(string Key, bool Names)> KeyFor(Container container) => Gen.OneOf(
        Gen.Const((container.Id, true)),
        Gen.Const(($" {container.Name.ToUpperInvariant()} ", true)),
        Gen.Const((container.Name, true)),
        Gen.Guid.Select(g => (g.ToString(), false)),
        Gen.Const(("zz-elsewhere", false)),
        Gen.Const(("  ", false)));

    /// <summary>A fact id: a real one, or text that isn't an id.</summary>
    private static readonly Gen<(string Text, bool IsId)> FactId = Gen.OneOf(
        Gen.Guid.Select(g => (g.ToString(), true)),
        Gen.String[Gen.Char["xyz-1"], 0, 6].Select(s => (s, false)));

    private static readonly NewMemoryFact AFact = new("Halcyon", "database", "PostgreSQL 15", null, null, []);

    private static MemoryFact Stored(Guid container) =>
        new(Guid.NewGuid(), Guid.NewGuid(), container, "Halcyon", "database", "PostgreSQL 15", null, null,
            DateTimeOffset.UnixEpoch, null, null, null, new MemoryCaller(null, null, null), []);

    /// <summary>The status a write failure must become, written out.</summary>
    private static MemoryStatus StatusOf(MemoryWriteFailure failure) => failure switch
    {
        MemoryWriteFailure.None => MemoryStatus.Success,
        MemoryWriteFailure.NotFound => MemoryStatus.FactNotFound,
        MemoryWriteFailure.Unchecked => MemoryStatus.Unchecked,
        MemoryWriteFailure.Forbidden => MemoryStatus.Forbidden,
        _ => MemoryStatus.Invalid,
    };

    /// <summary>A scope a call can ask for: none, personal, team, or a value that is neither.</summary>
    private static readonly Gen<MemoryScope?> AnyScope = Gen.Int[-1, 2].Select(v => v < 0 ? (MemoryScope?)null : (MemoryScope)v);

    private sealed class World
    {
        public readonly IContainerStore Containers = Substitute.For<IContainerStore>();
        public readonly IMemoryService Memory = Substitute.For<IMemoryService>();
        public readonly PolicyAuthorizer Authorizer;

        public World(bool reader, bool writer, Container container, bool exists)
        {
            Authorizer = new PolicyAuthorizer(policy => policy switch { Policies.KnowledgeRead => reader, Policies.Writer or Policies.KnowledgeWrite => writer, _ => false });
            Containers.GetAsync(Guid.Parse(container.Id), Arg.Any<CancellationToken>()).Returns(exists ? container : null);
            Containers.GetByNameAsync(container.Name, Arg.Any<CancellationToken>()).Returns(exists ? container : null);
        }

        public MemoryWriteResult Answers(MemoryWriteFailure failure, Guid container) =>
            failure == MemoryWriteFailure.None ? new MemoryWriteResult(Stored(container), null, null) : MemoryWriteResult.Failed("no", failure);
    }

    // ── memory.remember ───────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(RememberFact))]
    public Task Remember_AnyCall_WritesPersonalFactsForReadersTeamFactsForWritersIntoTheContainerNamed() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, AnyContainer, Gen.Bool, Gen.Enum<MemoryWriteFailure>(), AnyScope)
            .SelectMany(t => KeyFor(t.Item4).SelectMany(key => Gen.Bool.Select(cites => (t, key, cites))))
            .SampleAsync(async s =>
            {
                var ((caller, reader, writer, container, exists, failure, scope), key, cites) = s;
                NewMemoryFact fact = AFact with { Scope = scope, Sources = cites ? [new MemorySource(MemorySourceKind.Document, Guid.Empty.ToString())] : [] };
                var world = new World(reader, writer, container, exists);
                Guid id = Guid.Parse(container.Id);
                world.Memory.RememberAsync(id, fact, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>()).Returns(world.Answers(failure, id));

                var written = await new RememberFact(world.Containers, world.Memory, world.Authorizer).RunAsync(caller, key.Key, fact);

                // Without a scope, a fact citing sources is team and one citing none personal.
                MemoryScope effective = scope ?? (cites ? MemoryScope.Team : MemoryScope.Personal);
                bool known = (int)effective <= 1;
                bool allowed = effective == MemoryScope.Team ? writer : reader || writer;
                MemoryStatus expected = !known ? MemoryStatus.Invalid
                    : !allowed ? MemoryStatus.Forbidden
                    : !(exists && key.Names) ? MemoryStatus.ContainerNotFound
                    : StatusOf(failure);
                written.Status.Should().Be(expected);
                if (!known || !allowed || !(exists && key.Names))
                    world.Memory.ReceivedCalls().Should().BeEmpty();
                else
                    await world.Memory.Received(1).RememberAsync(id, fact, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
            }, iter: 1000);

    // ── memory.correct ────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(CorrectFact))]
    public Task Correct_AnyCall_ReachesMemoryOnlyWithAReaderARealFactIdAndAKnownKindAndSaysWhetherItMayWriteTeamFacts() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, Gen.Enum<MemoryWriteFailure>(), FactId, Gen.Int[0, 3])
            .SelectMany(t => KeyFor(t.Item3).SelectMany(key => Gen.Bool.Select(reader => (t, key, reader))))
            .SampleAsync(async s =>
            {
                var ((caller, writer, container, exists, failure, factId, kindValue), key, reader) = s;
                var kind = (MemoryCorrection)kindValue;
                var world = new World(reader, writer, container, exists);
                Guid id = Guid.Parse(container.Id);
                world.Memory.CorrectAsync(id, Arg.Any<Guid>(), kind, AFact, Arg.Any<MemoryCaller>(), writer, Arg.Any<CancellationToken>())
                    .Returns(world.Answers(failure, id));

                var written = await new CorrectFact(world.Containers, world.Memory, world.Authorizer).RunAsync(caller, key.Key, factId.Text, kind, AFact);

                MemoryStatus expected = !reader ? MemoryStatus.Forbidden
                    : !(exists && key.Names) ? MemoryStatus.ContainerNotFound
                    : !factId.IsId ? MemoryStatus.FactNotFound
                    : kindValue > 1 ? MemoryStatus.Invalid
                    : StatusOf(failure);
                written.Status.Should().Be(expected);
                bool reached = reader && exists && key.Names && factId.IsId && kindValue <= 1;
                world.Memory.ReceivedCalls().Should().HaveCount(reached ? 1 : 0);
            }, iter: 1000);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task Remember_TeamFact_NeedsTheWriterRoleAndTheWriteScope(bool writerRole, bool writeScope, bool expected)
    {
        // An Editor's knowledge:read token passes the writer role but not the scope; it must not write team facts.
        var authorizer = new PolicyAuthorizer(policy => policy switch
        {
            Policies.Writer => writerRole,
            Policies.KnowledgeWrite => writeScope,
            _ => false,
        });

        Container container = AnyContainer.Single();
        var world = new World(true, false, container, true);
        world.Memory.RememberAsync(Guid.Parse(container.Id), Arg.Any<NewMemoryFact>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(world.Answers(MemoryWriteFailure.None, Guid.Parse(container.Id)));

        var written = await new RememberFact(world.Containers, world.Memory, authorizer).RunAsync(
            new OperationContext(new ClaimsPrincipal(), OperationSurface.Mcp), container.Id,
            new NewMemoryFact("Halcyon", "database", "PostgreSQL", null, null, [], null, MemoryScope.Team));

        written.Status.Should().Be(expected ? MemoryStatus.Success : MemoryStatus.Forbidden);
    }

    // ── memory.forget ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(ForgetFact))]
    public Task Forget_AnyCall_DeletesOnlyForAReaderOrWriterAndHidesWhyAFactWasNotFound() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, AnyContainer, Gen.Bool, FactId, Gen.Int[0, 4])
            .SelectMany(t => KeyFor(t.Item4).SelectMany(key => Gen.OneOfConst(MemoryWriteFailure.None, MemoryWriteFailure.NotFound, MemoryWriteFailure.Forbidden, MemoryWriteFailure.Unchecked)
                .Select(failure => (t, key, failure))))
            .SampleAsync(async s =>
            {
                var ((caller, reader, writer, container, exists, factId, deleted), key, failure) = s;
                var world = new World(reader, writer, container, exists);
                Guid id = Guid.Parse(container.Id);
                world.Memory.ForgetAsync(id, Arg.Any<Guid>(), Arg.Any<MemoryCaller>(), writer, Arg.Any<CancellationToken>())
                    .Returns((failure == MemoryWriteFailure.None ? deleted : 0, failure));

                var forgotten = await new ForgetFact(world.Containers, world.Memory, world.Authorizer).RunAsync(caller, key.Key, factId.Text);

                bool reached = (reader || writer) && exists && key.Names && factId.IsId;
                MemoryStatus expected = !(reader || writer) ? MemoryStatus.Forbidden
                    : !(exists && key.Names) ? MemoryStatus.ContainerNotFound
                    : !factId.IsId ? MemoryStatus.FactNotFound
                    : failure == MemoryWriteFailure.Forbidden ? MemoryStatus.Forbidden
                    : failure == MemoryWriteFailure.NotFound ? MemoryStatus.FactNotFound
                    : failure == MemoryWriteFailure.Unchecked ? MemoryStatus.Unchecked
                    : MemoryStatus.Success;
                forgotten.Status.Should().Be(expected);
                world.Memory.ReceivedCalls().Should().HaveCount(reached ? 1 : 0);
                if (expected == MemoryStatus.Success)
                    forgotten.Result.Should().Be(deleted);
            }, iter: 1000);

    // ── memory.setScope ───────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(SetFactScope))]
    public Task SetScope_AnyCall_ReachesMemoryOnlyForAWriterWithARealFactIdAndAKnownScope() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, Gen.Enum<MemoryWriteFailure>(), FactId, Gen.Int[0, 2])
            .SelectMany(t => KeyFor(t.Item3).Select(key => (t, key)))
            .SampleAsync(async s =>
            {
                var ((caller, writer, container, exists, failure, factId, scopeValue), key) = s;
                var scope = (MemoryScope)scopeValue;
                var world = new World(reader: true, writer, container, exists);
                Guid id = Guid.Parse(container.Id);
                world.Memory.SetScopeAsync(id, Arg.Any<Guid>(), scope, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(world.Answers(failure, id));

                var changed = await new SetFactScope(world.Containers, world.Memory, world.Authorizer).RunAsync(caller, key.Key, factId.Text, scope);

                MemoryStatus expected = !writer ? MemoryStatus.Forbidden
                    : !(exists && key.Names) ? MemoryStatus.ContainerNotFound
                    : !factId.IsId ? MemoryStatus.FactNotFound
                    : scopeValue > 1 ? MemoryStatus.Invalid
                    : StatusOf(failure);
                changed.Status.Should().Be(expected);
                bool reached = writer && exists && key.Names && factId.IsId && scopeValue <= 1;
                world.Memory.ReceivedCalls().Should().HaveCount(reached ? 1 : 0);
            }, iter: 1000);

    // ── memory.recall ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(RecallMemory))]
    public Task Recall_AnyCall_ReadsOnlyTheNamedContainerAndShowsNothingWhenAccessIsUnchecked() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyContainer, Gen.Bool, Gen.Bool, Gen.Guid, Gen.Int[0, 3])
            .SelectMany(t => KeyFor(t.Item3).Select(key => (t, key)))
            .SampleAsync(async s =>
            {
                var ((caller, reader, container, exists, denied, claimed, count), key) = s;
                var world = new World(reader, writer: false, container, exists);
                Guid id = Guid.Parse(container.Id);
                var facts = Enumerable.Range(0, count).Select(_ => new RecalledFact(Stored(id), [])).ToList();
                world.Memory.RecallAsync(Arg.Any<MemoryQuery>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(denied ? MemoryRecall.DeniedResult : new MemoryRecall(facts));

                // The query names another container; the one the call names must win.
                var recalled = await new RecallMemory(world.Containers, world.Memory, world.Authorizer)
                    .RunAsync(caller, key.Key, new MemoryQuery(claimed, Text: "db"));

                MemoryStatus expected = !reader ? MemoryStatus.Forbidden
                    : !(exists && key.Names) ? MemoryStatus.ContainerNotFound
                    : denied ? MemoryStatus.Unchecked
                    : MemoryStatus.Success;
                recalled.Status.Should().Be(expected);
                if (expected is MemoryStatus.Forbidden or MemoryStatus.ContainerNotFound)
                {
                    world.Memory.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                await world.Memory.Received(1).RecallAsync(Arg.Is<MemoryQuery>(q => q.ContainerId == id && q.Text == "db"),
                    Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                if (expected == MemoryStatus.Success)
                    recalled.Result.Should().Equal(facts);
            }, iter: 1000);
}
