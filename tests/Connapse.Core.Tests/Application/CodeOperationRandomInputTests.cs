using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Code;
using Connapse.Application.Ports;
using Connapse.Application.Search;
using Connapse.Core;
using Connapse.Core.Interfaces;
using CsCheck;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The code operations' rules over random inputs: who is refused, which container or source a question goes to,
/// that a refused or unresolved question never reaches the code engine, and that a hidden source answering nothing
/// reads as missing. Each expectation is worked out here from the rule. Every sample builds its own fakes, because
/// CsCheck runs samples on several threads.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class CodeOperationRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Gen<OperationContext> AnyCaller =
        Gen.Enum<OperationSurface>().Select(s => new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), s));

    /// <summary>What the question names.</summary>
    private enum Target { ContainerById, ContainerByName, VisibleSource, VisibleSourceByName, HiddenSearchableSource, HiddenSource, Unknown, Blank }

    /// <summary>What the code engine answers.</summary>
    private enum Answer { Something, Nothing, Denied }

    private static readonly CodeFileMatch AFile = new("src/Ledger.cs", Guid.NewGuid().ToString(), 1, "defines", []);

    private sealed class World
    {
        public readonly IContainerStore Containers = Substitute.For<IContainerStore>();
        public readonly ISourceStore Sources = Substitute.For<ISourceStore>();
        public readonly FixedVisibility Visibility = new();
        public readonly ICodeTools Code = Substitute.For<ICodeTools>();
        public readonly SearchKnowledge Search;
        public readonly PolicyAuthorizer Authorizer;
        public readonly string Key;

        /// <summary>The owner a resolved question must be asked of; null when it must not resolve.</summary>
        public readonly Guid? Owner;

        /// <summary>Whether the owner is a source the caller can't see, which hides an empty answer.</summary>
        public readonly bool Hidden;

        public World(bool reader, Target target, Answer answer)
        {
            Authorizer = new PolicyAuthorizer(policy => policy == Policies.KnowledgeRead && reader);
            var settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
            settings.CurrentValue.Returns(new SearchSettings());
            Search = new SearchKnowledge(Containers, Sources, Visibility, Substitute.For<IKnowledgeSearch>(), settings, Authorizer);

            var container = new Container(Guid.NewGuid().ToString(), "billing", null, Epoch, Epoch);
            Containers.GetAsync(Guid.Parse(container.Id), Arg.Any<CancellationToken>()).Returns(container);
            Containers.GetByNameAsync("billing", Arg.Any<CancellationToken>()).Returns(container);
            var source = new Source(Guid.NewGuid(), "ledger-repo", null, null, "{}", Epoch, Epoch);
            Sources.FindAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);
            Sources.FindByNameAsync("ledger-repo", Arg.Any<CancellationToken>()).Returns(source);
            Visibility.Sees = _ => target is Target.VisibleSource or Target.VisibleSourceByName;
            Visibility.SearchableWhenHidden = _ => target == Target.HiddenSearchableSource;

            (Key, Owner, Hidden) = target switch
            {
                Target.ContainerById => (container.Id, Guid.Parse(container.Id), false),
                Target.ContainerByName => (" BILLING ", Guid.Parse(container.Id), false),
                Target.VisibleSource => (source.Id.ToString(), source.Id, false),
                Target.VisibleSourceByName => ("Ledger-Repo", source.Id, false),
                Target.HiddenSearchableSource => (source.Id.ToString(), source.Id, true),
                // A hidden source that search can't trim for the caller is never asked about, by id or by name.
                Target.HiddenSource => (source.Id.ToString(), (Guid?)null, false),
                Target.Unknown => (Guid.NewGuid().ToString(), null, false),
                _ => ("  ", null, false),
            };

            Code.FindSymbolAsync(default!, default!, default, default, default, default).ReturnsForAnyArgs(answer switch
            {
                Answer.Denied => CodeSymbolResult.DeniedResult,
                Answer.Nothing => new CodeSymbolResult([], [], []),
                _ => new CodeSymbolResult([], [], [AFile]),
            });
            Code.ReferencesAsync(default!, default!, default, default, default).ReturnsForAnyArgs(answer switch
            {
                Answer.Denied => CodeReferencesResult.DeniedResult,
                Answer.Nothing => new CodeReferencesResult([], []),
                _ => new CodeReferencesResult([], [AFile]),
            });
            Code.ImpactAsync(default!, default!, default, default, default).ReturnsForAnyArgs(answer switch
            {
                Answer.Denied => CodeImpactResult.DeniedResult,
                Answer.Nothing => new CodeImpactResult([], [], [], []),
                _ => new CodeImpactResult([], [], [AFile], []),
            });
            Code.RepoMapAsync(default!, default, default, default).ReturnsForAnyArgs(answer switch
            {
                Answer.Denied => CodeRepoMap.DeniedResult,
                Answer.Nothing => new CodeRepoMap([], 0),
                _ => new CodeRepoMap([new CodeMapFile(AFile.Path, AFile.DocumentId, 1, [])], 1),
            });
        }

        /// <summary>The rule written out, given whether the request itself was invalid.</summary>
        public CodeStatus Expected(bool reader, bool invalid, Answer answer) =>
            !reader ? CodeStatus.Forbidden
            : invalid ? CodeStatus.Invalid
            : Owner is null ? CodeStatus.NotFound
            : answer == Answer.Denied ? CodeStatus.Unchecked
            : Hidden && answer == Answer.Nothing ? CodeStatus.NotFound
            : CodeStatus.Success;

        /// <summary>The engine was asked once, of the right owner, exactly when the question resolved.</summary>
        public void AskedOnlyWhenResolved(bool reader, bool invalid)
        {
            var calls = Code.ReceivedCalls().ToList();
            if (!reader || invalid || Owner is null)
            {
                calls.Should().BeEmpty();
                return;
            }
            calls.Should().ContainSingle();
            ((CodeScope)calls[0].GetArguments()[0]!).OwnerId.Should().Be(Owner.Value);
        }
    }

    private static readonly Gen<string> Text = Gen.OneOf(Gen.Const(""), Gen.Const("  "), Gen.Const("Ledger.Post"), Gen.String[Gen.Char["ab. "], 0, 5]);

    // ── code.findSymbol ───────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(FindCodeSymbol))]
    public Task FindSymbol_AnyQuestion_AnswersOnlyAReaderAboutWhatSearchWouldSearch() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Enum<Target>(), Gen.Enum<Answer>(), Text, Gen.Int[0, 3], Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, reader, target, answer, query, matchValue, follow) = t;
                var world = new World(reader, target, answer);

                var found = await new FindCodeSymbol(world.Search, world.Authorizer, world.Code)
                    .RunAsync(caller, world.Key, query, (CodeMatch)matchValue, follow, 10);

                bool invalid = string.IsNullOrWhiteSpace(query) || matchValue > 2;
                found.Status.Should().Be(world.Expected(reader, invalid, answer));
                world.AskedOnlyWhenResolved(reader, invalid);
            }, iter: 1000);

    // ── code.references ───────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(FindCodeReferences))]
    public Task References_AnyQuestion_AnswersOnlyAReaderAboutWhatSearchWouldSearch() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Enum<Target>(), Gen.Enum<Answer>(), Text, Gen.Int[0, 4])
            .SampleAsync(async t =>
            {
                var (caller, reader, target, answer, symbol, kindValue) = t;
                var world = new World(reader, target, answer);

                var found = await new FindCodeReferences(world.Search, world.Authorizer, world.Code)
                    .RunAsync(caller, world.Key, symbol, (CodeReferenceKind)kindValue, 20);

                bool invalid = string.IsNullOrWhiteSpace(symbol) || kindValue > 2;
                found.Status.Should().Be(world.Expected(reader, invalid, answer));
                world.AskedOnlyWhenResolved(reader, invalid);
            }, iter: 1000);

    // ── code.impact ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(FindCodeImpact))]
    public Task Impact_AnyQuestion_AnswersOnlyAReaderAboutWhatSearchWouldSearch() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Enum<Target>(), Gen.Enum<Answer>(), Text, Gen.Int[1, 3])
            .SampleAsync(async t =>
            {
                var (caller, reader, target, answer, symbol, depth) = t;
                var world = new World(reader, target, answer);

                var found = await new FindCodeImpact(world.Search, world.Authorizer, world.Code)
                    .RunAsync(caller, world.Key, symbol, depth, 30);

                bool invalid = string.IsNullOrWhiteSpace(symbol);
                found.Status.Should().Be(world.Expected(reader, invalid, answer));
                world.AskedOnlyWhenResolved(reader, invalid);
            }, iter: 1000);

    // ── code.repoMap ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(MapCodeRepository))]
    public Task RepoMap_AnyQuestion_AnswersOnlyAReaderAboutWhatSearchWouldSearch() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Enum<Target>(), Gen.Enum<Answer>(), Gen.OneOf(Gen.Const((string?)null), Gen.Const((string?)"src")))
            .SampleAsync(async t =>
            {
                var (caller, reader, target, answer, path) = t;
                var world = new World(reader, target, answer);

                var mapped = await new MapCodeRepository(world.Search, world.Authorizer, world.Code)
                    .RunAsync(caller, world.Key, path, 30);

                mapped.Status.Should().Be(world.Expected(reader, invalid: false, answer));
                world.AskedOnlyWhenResolved(reader, invalid: false);
            }, iter: 1000);
}
