using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Tests.Support;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// memory.copy over random inputs: who is refused, which facts and destinations are refused, that a duplicate check
/// that couldn't finish refuses rather than copies, and that nothing is saved unless every rule passed. Each
/// expectation is worked out here from the rule. Every sample builds its own fakes, because CsCheck runs samples on
/// several threads.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class CopyMemoryFactRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Where the fact is held: the caller's personal space, a team space, a document container, or nowhere they can see.</summary>
    public enum Source { Personal, TeamSpace, Documents, Hidden }

    /// <summary>What the fact's history shows the caller: its current version, a superseded one, nothing, or a failed check.</summary>
    public enum Version { Current, Superseded, Missing, Denied }

    /// <summary>The destination: a team space, a document container, a personal space, or nothing that resolves.</summary>
    public enum Destination { TeamSpace, Documents, Personal, Missing }

    /// <summary>What the destination holds: no match, a match, a refused search, or more facts than the check reads.</summary>
    public enum Existing { None, Match, Denied, TooMany }

    private static Container Space(Guid id, string name, bool memory, MemoryScope audience) =>
        new(id.ToString(), name, null, Epoch, Epoch, Kind: memory ? ContainerKind.Memory : ContainerKind.Documents, Audience: audience);

    private static MemoryFact Fact(Guid container, string attribute, DateTimeOffset? superseded = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), container, "Halcyon", attribute, "PostgreSQL 15", null, null,
            DateTimeOffset.UnixEpoch, superseded, null, null, new MemoryCaller(null, null, null), []);

    private static RecalledFact Shown(MemoryFact fact) => new(fact, []);

    [Fact]
    [RandomInputTestOf(typeof(CopyMemoryFact))]
    public Task Copy_AnyCall_SavesOnlyTheCurrentPersonalVersionToATeamDestination() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<Source>(), Gen.Enum<Version>(), Gen.Enum<Destination>(), Gen.Enum<Existing>())
            .SelectMany(t => Gen.Select(Gen.Bool, Gen.Enum<MemoryWriteFailure>(), Gen.Bool, (anyway, failure, byName) => (t, anyway, failure, byName)))
            .SampleAsync(async s =>
            {
                var ((reader, writer, source, version, destination, existing), anyway, failure, byName) = s;
                var caller = new OperationContext(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")), OperationSurface.Rest);
                IContainerStore containers = ContainerStoreFakes.Create();
                IMemoryService memory = Substitute.For<IMemoryService>();
                var authorizer = new PolicyAuthorizer(p => p switch
                {
                    Policies.KnowledgeRead => reader,
                    Policies.Writer or Policies.KnowledgeWrite => writer,
                    _ => false,
                });

                Guid fromId = Guid.NewGuid(), toId = Guid.NewGuid();
                Container from = source switch
                {
                    Source.Personal => Space(fromId, "mine", true, MemoryScope.Personal),
                    Source.TeamSpace => Space(fromId, "team", true, MemoryScope.Team),
                    _ => Space(fromId, "docs", false, MemoryScope.Team),
                };
                containers.GetAsync(fromId, Arg.Any<CancellationToken>()).Returns(source == Source.Hidden ? null : from);
                Container? to = destination switch
                {
                    Destination.TeamSpace => Space(toId, "team-dest", true, MemoryScope.Team),
                    Destination.Documents => Space(toId, "docs-dest", false, MemoryScope.Team),
                    Destination.Personal => Space(toId, "mine-dest", true, MemoryScope.Personal),
                    _ => null,
                };
                containers.GetAsync(toId, Arg.Any<CancellationToken>()).Returns(to);
                if (to is not null)
                    containers.GetByNameAsync(to.Name, Arg.Any<CancellationToken>()).Returns(to);

                MemoryFact original = Fact(fromId, "database", version == Version.Superseded ? DateTimeOffset.UnixEpoch : null);
                memory.SpaceOfAsync(original.Id, Arg.Any<CancellationToken>()).Returns(fromId);
                memory.HistoryAsync(fromId, original.Id, null, 1, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>()).Returns(version switch
                {
                    Version.Missing => MemoryHistoryPage.Missing,
                    Version.Denied => MemoryHistoryPage.DeniedResult,
                    _ => new MemoryHistoryPage([Shown(original)], 1, null),
                });
                memory.ListAsync(toId, Arg.Any<MemoryFactFilter>(), MemoryFactSort.Subject, Arg.Any<MemoryFactKey?>(), Arg.Any<int>(),
                    Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>()).Returns(_ => existing switch
                {
                    Existing.Match => new MemoryFactPage([Shown(Fact(toId, "other")), Shown(Fact(toId, "DATABASE"))], null),
                    Existing.Denied => MemoryFactPage.DeniedResult,
                    Existing.TooMany => new MemoryFactPage([.. Enumerable.Range(0, 100).Select(_ => Shown(Fact(toId, "other")))], new MemoryFactKey(Guid.NewGuid())),
                    _ => new MemoryFactPage([Shown(Fact(toId, "other"))], null),
                });
                memory.RememberAsync(Arg.Any<Guid>(), Arg.Any<NewMemoryFact>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(call => failure == MemoryWriteFailure.None
                        ? new MemoryWriteResult(Fact(call.ArgAt<Guid>(0), "database"), null, null)
                        : MemoryWriteResult.Failed("no", failure));

                var copy = new CopyMemoryFact(containers, memory, new RememberFact(containers, memory, authorizer), NoAudit.Instance, authorizer);
                string named = to is null ? (byName ? "zz-elsewhere" : Guid.NewGuid().ToString()) : byName ? to.Name : to.Id;
                var copied = await copy.RunAsync(caller, original.Id.ToString(), named, anyway);

                // The rules, written out, in the order they apply.
                MemoryStatus Expected()
                {
                    if (!reader || !writer)
                        return MemoryStatus.Forbidden;
                    if (source == Source.Hidden)
                        return MemoryStatus.FactNotFound;
                    if (source != Source.Personal)
                        return MemoryStatus.Invalid;
                    if (version == Version.Denied)
                        return MemoryStatus.Unchecked;
                    if (version == Version.Missing)
                        return MemoryStatus.FactNotFound;
                    if (version == Version.Superseded)
                        return MemoryStatus.Invalid;
                    if (destination == Destination.Missing)
                        return MemoryStatus.ContainerNotFound;
                    if (destination is Destination.Personal or Destination.Documents)
                        return MemoryStatus.Invalid;
                    if (existing is Existing.Denied or Existing.TooMany)
                        return MemoryStatus.Unchecked;
                    if (existing == Existing.Match && !anyway)
                        return MemoryStatus.Exists;
                    return failure switch
                    {
                        MemoryWriteFailure.None => MemoryStatus.Success,
                        MemoryWriteFailure.NotFound => MemoryStatus.FactNotFound,
                        MemoryWriteFailure.Unchecked => MemoryStatus.Unchecked,
                        MemoryWriteFailure.Forbidden => MemoryStatus.Forbidden,
                        _ => MemoryStatus.Invalid,
                    };
                }

                MemoryStatus expected = Expected();
                copied.Status.Should().Be(expected);
                bool saves = expected == MemoryStatus.Success
                    || (destination == Destination.TeamSpace && existing is Existing.None or Existing.Match
                        && (anyway || existing == Existing.None) && reader && writer && source == Source.Personal && version == Version.Current);
                if (saves)
                    await memory.Received(1).RememberAsync(toId, Arg.Is<NewMemoryFact>(f => f.Value == original.Value && f.Attribute == original.Attribute),
                        Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>());
                else
                    await memory.DidNotReceiveWithAnyArgs().RememberAsync(default, default!, default!, default);
                if (copied.Succeeded)
                    copied.Result!.CopiedFrom.Should().Be(original.Id);
            }, iter: 300);
}
