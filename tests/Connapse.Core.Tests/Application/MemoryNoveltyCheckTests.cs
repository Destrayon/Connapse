using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using Connapse.Application.Search;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>A document check that finds every passage close, so a save it is asked about is always held.</summary>
internal sealed class AllClosePassages : IDocumentPassages
{
    public static readonly AllClosePassages Instance = new();

    public Task<PassageLookup> FindAsync(OperationContext context, Guid containerId, string text, CancellationToken ct = default) =>
        Task.FromResult(new PassageLookup([new MemoryPassage(Guid.NewGuid(), "handbook.md", "Halcyon runs PostgreSQL 15.")]));
}

/// <summary>
/// CON-245: a save into a document container's memory that looks close to one of its passages is held with the
/// passages, until it is sent again with <c>whatIsNew</c>, which is stored with them. Memory spaces never check.
/// </summary>
[Trait("Category", "Unit")]
public class MemoryNoveltyCheckTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly OperationContext Alice = new(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")), OperationSurface.Mcp);

    private readonly IContainerStore _containers = ContainerStoreFakes.Create();
    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();
    private readonly IDocumentPassages _passages = Substitute.For<IDocumentPassages>();
    private readonly RememberFact _remember;

    public MemoryNoveltyCheckTests()
    {
        _remember = new RememberFact(_containers, _memory, new PolicyAuthorizer(_ => true), _passages);
        _memory.RememberAsync(Arg.Any<Guid>(), Arg.Any<NewMemoryFact>(), Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(call => new MemoryWriteResult(new MemoryFact(Guid.NewGuid(), Guid.NewGuid(), call.ArgAt<Guid>(0), "Halcyon", "database",
                "PostgreSQL 15", null, null, DateTimeOffset.UnixEpoch, null, null, null, call.ArgAt<MemoryCaller>(2), [],
                call.ArgAt<NewMemoryFact>(1).Scope ?? MemoryScope.Personal, call.ArgAt<NewMemoryFact>(1).Novelty), null, null));
        _containers.EnsurePersonalSpaceAsync(Arg.Any<MemoryCaller>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Have(call.ArgAt<string>(1), ContainerKind.Memory, MemoryScope.Personal));
    }

    private Container Have(string name, ContainerKind kind, MemoryScope audience = MemoryScope.Team)
    {
        Container space = new(Guid.NewGuid().ToString(), name, null, Epoch, Epoch, Kind: kind, Audience: audience);
        _containers.GetAsync(Guid.Parse(space.Id), Arg.Any<MemoryCaller?>(), Arg.Any<CancellationToken>()).Returns(space);
        _containers.FindByNameAsync(name, Arg.Any<MemoryCaller?>(), Arg.Any<CancellationToken>()).Returns([space]);
        return space;
    }

    private static NewMemoryFact Fact(string value = "PostgreSQL 15") =>
        new(" Halcyon ", "database ", value, null, null, [new MemorySource(MemorySourceKind.Document, Guid.NewGuid().ToString())]);

    private void Finds(params MemoryPassage[] found) =>
        _passages.FindAsync(Arg.Any<OperationContext>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PassageLookup(found));

    private static MemoryPassage Passage(string name, string text = "Halcyon runs PostgreSQL 15.") => new(Guid.NewGuid(), name, text);

    private NewMemoryFact Saved() =>
        (NewMemoryFact)_memory.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IMemoryService.RememberAsync)).GetArguments()[1]!;

    [Fact]
    public async Task Remember_CloseToADocumentWithoutWhatIsNew_IsHeldWithThePassagesAndSavesNothing()
    {
        Container docs = Have("handbook", ContainerKind.Documents);
        MemoryPassage first = Passage("ops.md", "Halcyon's   database is\nPostgreSQL 15.");
        MemoryPassage second = Passage("setup.md");
        Finds(first, second);

        var written = await _remember.RunAsync(Alice, MemoryTarget.Space("handbook"), Fact(), whatIsNew: "  ");

        written.Status.Should().Be(MemoryStatus.CloseToDocuments);
        written.Field.Should().Be("whatIsNew");
        written.Detail.Should().Be(
            "Not saved. These passages look close:"
            + $"\n- ops.md ({first.DocumentId}): \"Halcyon's database is PostgreSQL 15.\""
            + $"\n- setup.md ({second.DocumentId}): \"Halcyon runs PostgreSQL 15.\""
            + "\nSave again only if your memory says something they don't, and give the reason in whatIsNew.");
        await _passages.Received(1).FindAsync(Alice, Guid.Parse(docs.Id), "Halcyon database PostgreSQL 15", Arg.Any<CancellationToken>());
        await _memory.DidNotReceiveWithAnyArgs().RememberAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task Remember_CloseWithWhatIsNew_SavesWithTheReasonAndAtMostThreeShortenedPassages()
    {
        Have("handbook", ContainerKind.Documents);
        string longText = new string('x', RememberFact.PassageChars + 50);
        MemoryPassage[] found = [Passage("a.md", longText), Passage("b.md"), Passage("c.md"), Passage("d.md")];
        Finds(found);

        var written = await _remember.RunAsync(Alice, MemoryTarget.Space("handbook"), Fact(), "  No document says which revision is in force. ");

        written.Succeeded.Should().BeTrue();
        MemoryNovelty novelty = Saved().Novelty!;
        novelty.WhatIsNew.Should().Be("No document says which revision is in force.");
        novelty.ClosePassages.Select(p => p.DocumentName).Should().Equal("a.md", "b.md", "c.md");
        novelty.ClosePassages[0].Text.Should().Be(new string('x', RememberFact.PassageChars) + "…");
        novelty.ClosePassages[1].Should().Be(found[1]);
        written.Result!.Fact.Novelty.Should().Be(novelty);
    }

    [Fact]
    public async Task Remember_PassageOfExactlyTheLimit_IsStoredWhole()
    {
        Have("handbook", ContainerKind.Documents);
        string text = new string('y', RememberFact.PassageChars);
        Finds(Passage("a.md", text));

        await _remember.RunAsync(Alice, MemoryTarget.Space("handbook"), Fact(), "new");

        Saved().Novelty!.ClosePassages.Single().Text.Should().Be(text);
    }

    [Fact]
    public async Task Remember_NothingClose_SavesWithoutNoveltyEvenWithWhatIsNew()
    {
        Have("handbook", ContainerKind.Documents);
        Finds();

        var plain = await _remember.RunAsync(Alice, MemoryTarget.Space("handbook"), Fact());
        plain.Succeeded.Should().BeTrue();
        Saved().Novelty.Should().BeNull();

        _memory.ClearReceivedCalls();
        (await _remember.RunAsync(Alice, MemoryTarget.Space("handbook"), Fact(), "a reason nobody needed")).Succeeded.Should().BeTrue();
        Saved().Novelty.Should().BeNull();
    }

    [Fact]
    public async Task Remember_DocumentsCannotBeSearched_IsNotSavedAndSaysWhy()
    {
        Have("handbook", ContainerKind.Documents);
        _passages.FindAsync(Arg.Any<OperationContext>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PassageLookup.Failed("embeddings are unavailable."));

        var written = await _remember.RunAsync(Alice, MemoryTarget.Space("handbook"), Fact(), "anything");

        written.Status.Should().Be(MemoryStatus.DocumentsUnchecked);
        written.Detail.Should().Be("not saved: 'handbook' could not be searched to check that its documents don't already say this "
            + "(embeddings are unavailable.) Try again shortly.");
        await _memory.DidNotReceiveWithAnyArgs().RememberAsync(default, default!, default!, default);
    }

    [Theory]
    [InlineData(MemoryScope.Team)]
    [InlineData(MemoryScope.Personal)]
    public async Task Remember_MemorySpace_IsNeverCheckedAndIgnoresWhatIsNew(MemoryScope audience)
    {
        Have("notes", ContainerKind.Memory, audience);
        Finds(Passage("ops.md"));

        var written = await _remember.RunAsync(Alice, MemoryTarget.Space("notes"), Fact(), "ignored here");

        written.Succeeded.Should().BeTrue();
        Saved().Novelty.Should().BeNull();
        _passages.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Remember_OldStyleWithoutSourcesOnADocumentContainer_GoesToAPersonalSpaceUnchecked()
    {
        Container docs = Have("handbook", ContainerKind.Documents);
        Finds(Passage("ops.md"));

        var written = await _remember.RunAsync(Alice, docs.Id, Fact() with { Sources = [] });

        written.Succeeded.Should().BeTrue();
        written.Result!.SavedTo!.Name.Should().Be("handbook-personal");
        _passages.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Remember_BlankValue_IsLeftToMemoryToRefuseWithoutSearching(string value)
    {
        Have("handbook", ContainerKind.Documents);

        await _remember.RunAsync(Alice, MemoryTarget.Space("handbook"), Fact(value));

        _passages.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Remember_WhatIsNewTooLong_IsInvalidAndLooksNothingUp()
    {
        Have("handbook", ContainerKind.Documents);
        Finds();

        var written = await _remember.RunAsync(Alice, MemoryTarget.Space("handbook"), Fact(), new string('n', RememberFact.MaxWhatIsNew + 1));
        var padded = await _remember.RunAsync(Alice, MemoryTarget.Space("handbook"), Fact(), $"  {new string('n', RememberFact.MaxWhatIsNew)}  ");

        written.Status.Should().Be(MemoryStatus.Invalid);
        written.Field.Should().Be("whatIsNew");
        written.Detail.Should().Be($"whatIsNew must be at most {RememberFact.MaxWhatIsNew} characters.");
        await _passages.Received(1).FindAsync(Arg.Any<OperationContext>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        padded.Succeeded.Should().BeTrue("the limit counts the trimmed text");
    }

    [Fact]
    public void CheckText_IsSubjectAttributeAndValueTrimmed() =>
        RememberFact.CheckText(new NewMemoryFact(" a ", "b\t", "\nc", null, null, null)).Should().Be("a b c");

    // ── the search the check makes ────────────────────────────────────────────────────────────────

    private sealed class Search
    {
        public readonly IContainerStore Containers = ContainerStoreFakes.Create();
        public readonly IKnowledgeSearch Knowledge = Substitute.For<IKnowledgeSearch>();
        public SearchOptions? Asked;

        public Search(params SearchHit[] hits)
        {
            ISourceVisibility visibility = Substitute.For<ISourceVisibility>();
            visibility.ForAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>()).Returns(_ => true);
            Knowledge.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>()).Returns(c =>
            {
                Asked = c.ArgAt<SearchOptions>(1);
                return new SearchResult([.. hits], hits.Length, TimeSpan.Zero);
            });
            IOptionsMonitor<SearchSettings> settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
            settings.CurrentValue.Returns(new SearchSettings());
            Finder = new SearchDocumentPassages(new SearchContainer(new SearchKnowledge(
                Containers, Substitute.For<ISourceStore>(), visibility, Knowledge, settings,
                new PolicyAuthorizer(policy => policy == Policies.KnowledgeRead))));
        }

        public SearchDocumentPassages Finder { get; }

        public Guid Container()
        {
            Guid id = Guid.NewGuid();
            Containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new Container(id.ToString(), "handbook", null, Epoch, Epoch));
            return id;
        }
    }

    [Fact]
    public async Task SearchDocumentPassages_Hits_AreTheTopSemanticPassagesAtTheCloseCosineWithTheirDocumentNames()
    {
        Guid named = Guid.NewGuid();
        var search = new Search(
            new SearchHit("c1", named.ToString(), "Halcyon runs PostgreSQL 15.", 0.95f, new() { ["fileName"] = "ops.md" }),
            new SearchHit("c2", "not-a-guid", "Halcyon's database.", 0.9f, []));
        Guid container = search.Container();

        PassageLookup found = await search.Finder.FindAsync(Alice, container, "Halcyon database PostgreSQL 15");

        found.Error.Should().BeNull();
        found.Passages.Should().Equal(
            new MemoryPassage(named, "ops.md", "Halcyon runs PostgreSQL 15."),
            new MemoryPassage(Guid.Empty, "not-a-guid", "Halcyon's database."));
        search.Asked!.Mode.Should().Be(SearchMode.Semantic);
        search.Asked.TopK.Should().Be(RememberFact.PassagesShown);
        search.Asked.MinScore.Should().Be(SearchDocumentPassages.CloseCosine);
        search.Asked.ContainerId.Should().Be(container.ToString());
    }

    [Fact]
    public async Task SearchDocumentPassages_SearchFails_ReturnsTheReasonAndNoPassages()
    {
        var search = new Search();

        PassageLookup found = await search.Finder.FindAsync(Alice, Guid.NewGuid(), "Halcyon database PostgreSQL 15");

        found.Passages.Should().BeEmpty();
        found.Error.Should().Contain("not found");
    }
}
