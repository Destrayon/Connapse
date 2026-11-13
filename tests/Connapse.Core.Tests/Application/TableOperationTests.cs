using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Tables;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Mcp;
using CsCheck;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// CON-319: a container's tables are paged through by name, for anyone who can read the container. CON-304: only
/// tables from documents the caller may read are listed, each trimmed to what the caller may see, and nothing is when
/// that can't be checked; admins hide columns.
/// </summary>
[Trait("Category", "Unit")]
public class TableOperationTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IContainerStore _containers = Substitute.For<IContainerStore>();
    private readonly ITableStore _tables = Substitute.For<ITableStore>();
    private readonly ITableQueryService _queries = Substitute.For<ITableQueryService>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IDocumentAccess _access = Substitute.For<IDocumentAccess>();
    private readonly PolicyAuthorizer _authorizer = new(_ => true);
    private readonly HashSet<Guid> _unreadable = [];

    public TableOperationTests()
    {
        _access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlySet<string>)c.ArgAt<IReadOnlyList<Document>>(1)
                .Select(d => d.Id).Where(id => !_unreadable.Contains(Guid.Parse(id))).ToHashSet());
    }

    private readonly IMemoryService _memory = Viewing();

    private readonly ITableCardIndex _cards = WordsOnly();

    private ListTables List => new(_containers, _tables, _queries, _documents, _access, _authorizer, _memory, _cards);

    /// <summary>A card index whose embeddings are down, so a ranking goes by words alone.</summary>
    private static ITableCardIndex WordsOnly()
    {
        var cards = Substitute.For<ITableCardIndex>();
        cards.SimilaritiesAsync(default, default!, default!, default)
            .ReturnsForAnyArgs(c => TableCardSimilarities.Unavailable(c.ArgAt<IReadOnlyList<TableCard>?>(1)?.Count ?? 0));
        return cards;
    }

    /// <summary>A memory service that lets any caller view any container's memory, seeing no sources.</summary>
    private static IMemoryService Viewing()
    {
        var memory = Substitute.For<IMemoryService>();
        memory.ViewerOfAsync(default, default!, default)
            .ReturnsForAnyArgs(c => new MemoryViewer(c.ArgAt<MemoryCaller>(1), new HashSet<MemorySource>()));
        return memory;
    }

    private HideTableColumns Hide => new(_containers, _tables, _authorizer);

    private Guid ContainerOfKind(ContainerKind kind)
    {
        Guid id = Guid.NewGuid();
        _containers.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(new Container(id.ToString(), "c", null, Epoch, Epoch, Kind: kind));
        _containers.GetAsync(id, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
            .Returns(new Container(id.ToString(), "c", null, Epoch, Epoch, Kind: kind));
        return id;
    }

    /// <summary>Stores <paramref name="tables"/> in the container, with each source document present in it.</summary>
    private void Holds(Guid containerId, params TableInfo[] tables)
    {
        _tables.ListAsync(containerId, Arg.Any<CancellationToken>()).Returns(tables);
        foreach (Guid document in tables.SelectMany(t => t.DocumentId is Guid d ? [d] : t.SourceDocuments!))
            _documents.GetAsync(document.ToString(), Arg.Any<CancellationToken>())
                .Returns(new Document(document.ToString(), containerId.ToString(), "t.csv", "text/csv", "/t.csv", 1, Epoch, []));
    }

    private static TableInfo Table(Guid containerId, string name) =>
        new(Guid.NewGuid(), containerId, name, null, Guid.NewGuid(), [new TableColumn("a", TableColumnType.Text)], 1, false, [],
            "tables_x", "t_x", 1, Epoch, Epoch);

    private static TableInfo Merged(Guid containerId, params Guid[] sources) =>
        new(Guid.NewGuid(), containerId, "merged", null, null,
            [new TableColumn("a", TableColumnType.Text), new TableColumn("b", TableColumnType.Text)], 10, false,
            [.. sources.Select((s, i) => new TableReject(i + 2, 1, $"bad{i}", "not text", s))],
            "tables_x", "t_x", 3, Epoch, Epoch.AddDays(1), SourceDocuments: sources);

    [Fact]
    public async Task List_NotAReader_IsForbiddenAndReadsNothing()
    {
        _authorizer.Allows = policy => policy != Policies.Reader;

        var listed = await List.RunAsync(Caller, Guid.NewGuid(), 0, 10);

        listed.Status.Should().Be(TableStatus.Forbidden);
        _tables.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1, 10, "skip must be >= 0")]
    [InlineData(0, 0, "take must be >= 1")]
    [InlineData(0, 201, "take must be <= 200")]
    public async Task List_PageOutOfRange_IsInvalidPage(int skip, int take, string detail)
    {
        var listed = await List.RunAsync(Caller, ContainerOfKind(ContainerKind.Documents), skip, take);

        listed.Status.Should().Be(TableStatus.InvalidPage);
        listed.Detail.Should().Be(detail);
    }

    [Fact]
    public async Task List_MissingContainer_IsNotFound()
    {
        var listed = await List.RunAsync(Caller, Guid.NewGuid(), 0, 10);

        listed.Status.Should().Be(TableStatus.ContainerNotFound);
    }

    [Fact]
    public async Task List_MemoryContainer_ListsOnlyItsMemoryTableAndReadsNoCatalog()
    {
        Guid memory = ContainerOfKind(ContainerKind.Memory);
        _queries.CountAsync(memory, Arg.Any<TableAccess>(), MemoryTable.Id, Arg.Any<CancellationToken>()).Returns(3);

        var listed = await List.RunAsync(Caller, memory, 0, 10);

        listed.Status.Should().Be(TableStatus.Success);
        listed.Result!.Items.Should().ContainSingle().Which.Should().Match<TableInfo>(t => t.Id == MemoryTable.Id && t.RowCount == 3);
        listed.Result.TotalCount.Should().Be(1);
        _tables.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 2, 5, true)]
    [InlineData(2, 2, 5, true)]
    [InlineData(4, 2, 5, false)]
    [InlineData(0, 50, 2, false)]
    public async Task List_DocumentsContainer_ReturnsThePageTheTotalAndWhetherMoreFollow(int skip, int take, int total, bool hasMore)
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        TableInfo[] all = [.. Enumerable.Range(0, total).Select(i => Table(id, $"t{i}"))];
        Holds(id, all);

        var listed = await List.RunAsync(Caller, id, skip, take);

        listed.Status.Should().Be(TableStatus.Success);
        listed.Result!.Items.Select(x => x.Id).Should().Equal(all.Skip(skip).Take(take).Select(x => x.Id));
        listed.Result.TotalCount.Should().Be(total);
        listed.Result.HasMore.Should().Be(hasMore);
    }

    [Fact]
    public async Task List_TableFromAnUnreadableDocument_IsNeitherListedNorCounted()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        TableInfo open = Table(id, "open"), secret = Table(id, "secret");
        Holds(id, open, secret);
        _unreadable.Add(secret.DocumentId!.Value);

        var listed = await List.RunAsync(Caller, id, 0, 10);

        listed.Result!.Items.Select(x => x.Id).Should().Equal(open.Id);
        listed.Result.TotalCount.Should().Be(1);
        listed.Result.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task List_SourceDocumentGoneOrInAnotherContainer_IsNotListed()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        TableInfo gone = Table(id, "gone"), elsewhere = Table(id, "elsewhere");
        _tables.ListAsync(id, Arg.Any<CancellationToken>()).Returns([gone, elsewhere]);
        _documents.GetAsync(elsewhere.DocumentId!.Value.ToString(), Arg.Any<CancellationToken>())
            .Returns(new Document(elsewhere.DocumentId.Value.ToString(), Guid.NewGuid().ToString(), "t.csv", null, "/t.csv", 1, Epoch, []));

        var listed = await List.RunAsync(Caller, id, 0, 10);

        listed.Result!.Items.Should().BeEmpty();
        listed.Result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task List_AccessCheckFails_ListsNothingAndSaysSo()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        Holds(id, Table(id, "t"));
        _access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("permission source down"));

        var listed = await List.RunAsync(Caller, id, 0, 10);

        listed.Status.Should().Be(TableStatus.AccessUnchecked);
        listed.Detail.Should().Be(TableAccess.UncheckedMessage);
        listed.Result.Should().BeNull();
    }

    [Fact]
    public async Task List_HiddenColumn_IsLeftOutForAViewer()
    {
        _authorizer.Allows = policy => policy != Policies.Admin;
        Guid id = ContainerOfKind(ContainerKind.Documents);
        TableInfo table = Merged(id, Guid.NewGuid()) with { HiddenColumns = ["a"] };
        Holds(id, table);

        var listed = await List.RunAsync(Caller, id, 0, 10);

        TableInfo item = listed.Result!.Items.Single();
        item.Columns.Select(c => c.Name).Should().Equal("b");
        item.HiddenColumns.Should().BeNull();
        item.Rejects.Should().BeEmpty("the only reject is in the hidden column, and its value would show what it holds");
    }

    [Fact]
    public async Task List_HiddenColumn_IsShownMarkedToAnAdmin()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        TableInfo table = Table(id, "t") with { HiddenColumns = ["a"] };
        Holds(id, table);

        var listed = await List.RunAsync(Caller, id, 0, 10);

        TableInfo item = listed.Result!.Items.Single();
        item.Columns.Select(c => c.Name).Should().Equal("a");
        item.HiddenColumns.Should().Equal("a");
    }

    [Fact]
    public async Task List_HiddenTable_IsLeftOutForAViewer()
    {
        _authorizer.Allows = policy => policy != Policies.Admin;
        Guid id = ContainerOfKind(ContainerKind.Documents);
        Holds(id, Table(id, "shown"), Table(id, "secret") with { Hidden = true });

        var listed = await List.RunAsync(Caller, id, 0, 10);

        listed.Result!.Items.Select(t => t.Name).Should().Equal("shown");
        listed.Result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task List_HiddenTable_IsShownMarkedToAnAdmin()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        Holds(id, Table(id, "shown"), Table(id, "secret") with { Hidden = true });

        var listed = await List.RunAsync(Caller, id, 0, 10);

        listed.Result!.Items.Select(t => (t.Name, t.Hidden)).Should().Equal(("shown", false), ("secret", true));
    }

    [Fact]
    public async Task List_MergedTableReadInPart_CountsOnlyReadableRowsAndNamesOnlyReadableSources()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        Guid open = Guid.NewGuid(), secret = Guid.NewGuid();
        TableInfo table = Merged(id, open, secret);
        Holds(id, table);
        _unreadable.Add(secret);
        _queries.CountAsync(id, Arg.Is<TableAccess>(a => a.ReadableDocuments.SetEquals(new[] { open })), table.Id, Arg.Any<CancellationToken>())
            .Returns(4);

        var listed = await List.RunAsync(Caller, id, 0, 10);

        TableInfo item = listed.Result!.Items.Single();
        item.Partial.Should().BeTrue();
        item.RowCount.Should().Be(4);
        item.SourceDocuments.Should().Equal(open);
        item.Rejects.Select(r => r.DocumentId).Should().Equal(open);
    }

    [Fact]
    public async Task List_MergedTableReadInFull_IsNotPartialAndRunsNoCount()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        TableInfo table = Merged(id, Guid.NewGuid(), Guid.NewGuid());
        Holds(id, table);

        var listed = await List.RunAsync(Caller, id, 0, 10);

        TableInfo item = listed.Result!.Items.Single();
        item.Partial.Should().BeFalse();
        item.RowCount.Should().Be(10);
        _queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task List_MergedTableDeletedBeforeItsCount_IsLeftOut()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        Guid secret = Guid.NewGuid();
        Holds(id, Merged(id, Guid.NewGuid(), secret));
        _unreadable.Add(secret);
        _queries.CountAsync(id, Arg.Any<TableAccess>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TableQueryException("There is no table with that id in this container."));

        var listed = await List.RunAsync(Caller, id, 0, 10);

        listed.Result!.Items.Should().BeEmpty();
    }

    /// <summary>Holds <paramref name="count"/> tables named fill000, fill001, … plus <paramref name="extra"/>, all readable.</summary>
    private TableInfo[] Many(Guid containerId, int count, params TableInfo[] extra)
    {
        TableInfo[] all = [.. Enumerable.Range(0, count).Select(i => Table(containerId, $"fill{i:D3}")), .. extra];
        Holds(containerId, all);
        return all;
    }

    [Fact]
    public async Task List_SearchOverManyTables_RanksEveryTableWithTheMatchFirst()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        TableInfo payroll = Table(id, "staff_payroll") with { Description = "What each employee is paid." };
        Many(id, TableSearch.ManyTables, payroll);

        var listed = await List.RunAsync(Caller, id, 0, 10, "employee pay");

        listed.Result!.Mode.Should().Be(TableListMode.Ranked);
        listed.Result.Items.Should().HaveCount(10);
        listed.Result.Items[0].Id.Should().Be(payroll.Id);
        listed.Result.TotalCount.Should().Be(TableSearch.ManyTables + 1, "a ranking drops no table");
        listed.Result.HasMore.Should().BeTrue();
        listed.Result.MeaningUsed.Should().BeFalse();
        listed.Result.Unembedded.Should().Be(TableSearch.ManyTables + 1);
    }

    [Fact]
    public async Task List_SearchOverManyTables_FindsByMeaningWhatNoWordMatches()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        TableInfo wages = Table(id, "zz_w2");
        Many(id, TableSearch.ManyTables, wages);
        _cards.SimilaritiesAsync(id, Arg.Any<IReadOnlyList<TableCard>>(), "salaries", Arg.Any<CancellationToken>())
            .Returns(new TableCardSimilarities(new Dictionary<Guid, double> { [wages.Id] = 0.9 }, TableSearch.ManyTables, true));

        var listed = await List.RunAsync(Caller, id, 0, 5, " salaries ");

        listed.Result!.Items[0].Id.Should().Be(wages.Id);
        listed.Result.MeaningUsed.Should().BeTrue();
        listed.Result.Unembedded.Should().Be(TableSearch.ManyTables);
    }

    [Fact]
    public async Task List_SearchOverAtMostManyTables_StillFiltersAndEmbedsNothing()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        Many(id, TableSearch.ManyTables);

        var listed = await List.RunAsync(Caller, id, 0, 200, "fill01");

        listed.Result!.Mode.Should().Be(TableListMode.Filtered);
        listed.Result.Items.Select(t => t.Name).Should().Equal(Enumerable.Range(10, 10).Select(i => $"fill{i:D3}"));
        listed.Result.MeaningUsed.Should().BeNull();
        _cards.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task List_ManyTablesButFewReadable_FiltersAndNeverEmbedsUnreadableOnes()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        TableInfo[] all = Many(id, TableSearch.ManyTables + 10);
        foreach (TableInfo table in all.Take(11))
            _unreadable.Add(table.DocumentId!.Value);

        var listed = await List.RunAsync(Caller, id, 0, 200, "fill");

        listed.Result!.Mode.Should().Be(TableListMode.Filtered);
        listed.Result.TotalCount.Should().Be(TableSearch.ManyTables - 1);
        _cards.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task List_RankedSearch_SendsOnlyReadableTablesVisibleColumnsToTheIndex()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        TableInfo closed = Table(id, "closed_table");
        TableInfo people = Table(id, "people") with
        {
            Columns = [new TableColumn("name", TableColumnType.Text), new TableColumn("ssn", TableColumnType.Text, "Social security number")],
            HiddenColumns = ["ssn"],
        };
        Many(id, TableSearch.ManyTables + 1, closed, people);
        _unreadable.Add(closed.DocumentId!.Value);
        IReadOnlyList<TableCard>? sent = null;
        _cards.SimilaritiesAsync(id, Arg.Do<IReadOnlyList<TableCard>>(c => sent = c), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => TableCardSimilarities.Unavailable(c.ArgAt<IReadOnlyList<TableCard>>(1).Count));

        var listed = await List.RunAsync(Caller, id, 0, 200, "ssn social security");

        listed.Result!.Mode.Should().Be(TableListMode.Ranked);
        sent!.Select(c => c.TableId).Should().NotContain(closed.Id);
        sent!.Should().OnlyContain(c => !c.Text.Contains("ssn") && !c.Text.Contains("Social security"));
        listed.Result.Items.Select(t => t.Id).Should().NotContain(closed.Id);
        listed.Result.Items[0].Id.Should().NotBe(people.Id, "a hidden column can't move its table up");
    }

    [Fact]
    public async Task List_RankedSearchWhenTheIndexFails_FallsBackToWords()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        TableInfo payroll = Table(id, "payroll");
        Many(id, TableSearch.ManyTables, payroll);
        _cards.SimilaritiesAsync(default, default!, default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));

        var listed = await List.RunAsync(Caller, id, 0, 1, "payroll");

        listed.Result!.Items.Single().Id.Should().Be(payroll.Id);
        listed.Result.MeaningUsed.Should().BeFalse();
    }

    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(ListTables))]
    public Task List_AnyCallerContainerPageAndReadableSet_AnswersTheReadableSliceOrTheFirstRuleItBreaks() =>
        Gen.Select(Gen.Bool, Gen.Enum<ContainerKind>().Nullable(), Gen.Int[-2, 6], Gen.Int[-1, 205],
                Gen.OneOf(Gen.Bool.Array[0, 8], Gen.Bool.Array[45, 60]), Gen.OneOfConst<string?>(null, " ", "t1", "a", "zz"))
            .SampleAsync(async t =>
            {
                var (reader, kind, skip, take, readable, search) = t;
                var containers = Substitute.For<IContainerStore>();
                var tables = Substitute.For<ITableStore>();
                var documents = Substitute.For<IDocumentStore>();
                var access = Substitute.For<IDocumentAccess>();
                var authorizer = new PolicyAuthorizer(policy => (policy == Policies.Reader || policy == Policies.KnowledgeRead) && reader);
                Guid id = Guid.NewGuid();
                containers.GetAsync(id, Arg.Any<CancellationToken>())
                    .Returns(kind is { } k ? new Container(id.ToString(), "c", null, Epoch, Epoch, Kind: k) : null);
                containers.GetAsync(id, Arg.Any<MemoryCaller>(), Arg.Any<CancellationToken>())
                    .Returns(kind is { } m ? new Container(id.ToString(), "c", null, Epoch, Epoch, Kind: m) : null);
                var all = readable.Select((_, i) => Table(id, $"t{i}")).ToList();
                tables.ListAsync(id, Arg.Any<CancellationToken>()).Returns(all);
                documents.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(c => new Document(c.ArgAt<string>(0), id.ToString(), "t.csv", null, "/t.csv", 1, Epoch, []));
                HashSet<string> open = [.. all.Where((_, i) => readable[i]).Select(x => x.DocumentId!.Value.ToString())];
                access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
                    .Returns(open);

                var listed = await new ListTables(containers, tables, Substitute.For<ITableQueryService>(), documents, access, authorizer, Viewing(), WordsOnly())
                    .RunAsync(Caller, id, skip, take, search);

                TableStatus expected = !reader ? TableStatus.Forbidden
                    : skip < 0 || take is < 1 or > 200 ? TableStatus.InvalidPage
                    : kind is null ? TableStatus.ContainerNotFound
                    : TableStatus.Success;
                listed.Status.Should().Be(expected);
                if (expected != TableStatus.Success || kind == ContainerKind.Memory)
                {
                    tables.ReceivedCalls().Should().BeEmpty();
                    if (expected == TableStatus.Success && search is null or " ")
                    {
                        // A memory container lists just its memory table (CON-309), on the first page.
                        listed.Result!.Items.Select(x => x.Id).Should().Equal(skip == 0 ? [MemoryTable.Id] : []);
                        listed.Result.TotalCount.Should().Be(1);
                    }
                    return;
                }
                var visible = all.Where((_, i) => readable[i]).ToList();
                bool searching = !string.IsNullOrWhiteSpace(search);
                listed.Result!.Mode.Should().Be(!searching ? TableListMode.All
                    : visible.Count > TableSearch.ManyTables ? TableListMode.Ranked : TableListMode.Filtered);
                if (listed.Result.Mode == TableListMode.Ranked)
                {
                    // Every readable table is ranked, each once, and none other.
                    listed.Result.Items.Select(x => x.Id).Should().OnlyHaveUniqueItems()
                        .And.BeSubsetOf(visible.Select(x => x.Id))
                        .And.HaveCount(Math.Max(0, Math.Min(take, visible.Count - skip)));
                }
                else
                {
                    // Every table here has the one column "a", so "a" keeps them all.
                    if (searching)
                        visible = visible.Where(x => x.Name.Contains(search!) || search == "a").ToList();
                    listed.Result.Items.Select(x => x.Id).Should().Equal(visible.Skip(skip).Take(take).Select(x => x.Id));
                }
                listed.Result.TotalCount.Should().Be(visible.Count);
                listed.Result.HasMore.Should().Be(visible.Count - skip > take);
            }, iter: 2000);

    /// <summary>
    /// CON-326: agent SQL runs only for a reader with a query, on a documents container whose Tables.AgentSql is on, and
    /// sees only tables whose source document is in that container and readable by the caller.
    /// </summary>
    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(RunTableSql))]
    public Task Sql_AnyCallerContainerSettingAndSources_RunsOverTheReadableTablesOrTheFirstRuleItBreaks() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<ContainerKind>().Nullable(), Gen.Bool,
                Gen.Select(Gen.Int[0, 2], Gen.Bool).Array[0, 5], Gen.Int[0, 2])
            .SampleAsync(async t =>
            {
                var (reader, hasQuery, kind, agentSql, sources, outcome) = t;
                // outcome: 0 = the query runs, 1 = it is refused, 2 = the access check throws.
                bool refused = outcome == 1, accessFails = outcome == 2 && sources.Any(s => s.Item1 == 0);
                var containers = Substitute.For<IContainerStore>();
                var tables = Substitute.For<ITableStore>();
                var documents = Substitute.For<IDocumentStore>();
                var access = Substitute.For<Connapse.Application.Ports.IDocumentAccess>();
                var settings = Substitute.For<IContainerSettingsResolver>();
                var sql = Substitute.For<ITableSqlService>();
                var authorizer = new PolicyAuthorizer(policy => policy == Policies.Reader && reader);
                Guid id = Guid.NewGuid();
                containers.GetAsync(id, Arg.Any<MemoryCaller?>(), Arg.Any<CancellationToken>())
                    .Returns(kind is { } k ? new Container(id.ToString(), "c", null, Epoch, Epoch, Kind: k) : null);
                settings.GetTableSettingsAsync(id, Arg.Any<CancellationToken>()).Returns(new TableSettings { AgentSql = agentSql });

                // Each table's source: 0 = in this container, 1 = in another container, 2 = gone; and whether it's readable.
                HashSet<Guid> expectedReadable = [];
                List<TableInfo> all = [];
                foreach (var (where, readable) in sources)
                {
                    TableInfo table = Table(id, $"t{all.Count}");
                    all.Add(table);
                    string doc = table.DocumentId!.Value.ToString();
                    if (where != 2)
                        documents.GetAsync(doc, Arg.Any<CancellationToken>()).Returns(new Document(
                            doc, where == 0 ? id.ToString() : Guid.NewGuid().ToString(), "f.csv", null, "/f.csv", 1, Epoch, []));
                    if (where == 0 && readable)
                        expectedReadable.Add(table.DocumentId.Value);
                }
                HashSet<string> readableIds = sources.Zip(all).Where(p => p.First.Item2).Select(p => p.Second.DocumentId!.Value.ToString()).ToHashSet();
                tables.ListAsync(id, Arg.Any<CancellationToken>()).Returns(all);
                access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
                    .Returns(c => accessFails
                        ? throw new InvalidOperationException("access store down")
                        : (IReadOnlySet<string>)c.ArgAt<IReadOnlyList<Document>>(1).Select(d => d.Id).Where(readableIds.Contains).ToHashSet());
                TableQueryResult answer = new(["a"], [], false, null);
                TableAccess? seen = null;
                sql.RunAsync(id, Arg.Do<TableAccess>(a => seen = a), "SELECT 1", Arg.Any<CancellationToken>())
                    .Returns(_ => refused ? throw new TableQueryException("refused") : answer);

                var ran = await new RunTableSql(containers, tables, documents, access, settings, sql, authorizer)
                    .RunAsync(Caller, id, hasQuery ? "SELECT 1" : " ");

                TableStatus expected = !reader ? TableStatus.Forbidden
                    : !hasQuery ? TableStatus.QueryNotRun
                    : kind is null ? TableStatus.ContainerNotFound
                    : kind == ContainerKind.Memory ? TableStatus.MemoryContainer
                    : !agentSql ? TableStatus.AgentSqlOff
                    : accessFails ? TableStatus.AccessUnchecked
                    : refused ? TableStatus.QueryNotRun
                    : TableStatus.Success;
                ran.Status.Should().Be(expected);
                bool reachedSql = reader && hasQuery && kind == ContainerKind.Documents && agentSql;
                if (!reachedSql)
                {
                    sql.ReceivedCalls().Should().BeEmpty();
                    tables.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                if (accessFails)
                {
                    sql.ReceivedCalls().Should().BeEmpty("an access check that failed reads nothing");
                    return;
                }
                seen!.ReadableDocuments.Should().BeEquivalentTo(expectedReadable, "only this container's readable sources count");
                if (refused)
                    ran.Detail.Should().Be("refused");
                else
                    ran.Result.Should().BeSameAs(answer);
            }, iter: 1000);

    /// <summary>
    /// CON-336: a GraphQL query runs for a reader with a query on a documents container, with no setting to turn on, and
    /// reaches the engine with only the tables whose source document is in that container and readable by the caller.
    /// </summary>
    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(RunTableGraphQL))]
    public Task GraphQL_AnyCallerContainerAndSources_RunsOverTheReadableTablesOrTheFirstRuleItBreaks() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<ContainerKind>().Nullable(),
                Gen.Select(Gen.Int[0, 2], Gen.Bool).Array[0, 5], Gen.Int[0, 2])
            .SampleAsync(async t =>
            {
                var (reader, hasQuery, kind, sources, outcome) = t;
                // outcome: 0 = the query runs, 1 = it is refused, 2 = the access check throws.
                bool refused = outcome == 1, accessFails = outcome == 2 && sources.Any(s => s.Item1 == 0);
                var containers = Substitute.For<IContainerStore>();
                var tables = Substitute.For<ITableStore>();
                var documents = Substitute.For<IDocumentStore>();
                var access = Substitute.For<Connapse.Application.Ports.IDocumentAccess>();
                var graphql = Substitute.For<ITableGraphQLService>();
                var authorizer = new PolicyAuthorizer(policy => policy == Policies.Reader && reader);
                Guid id = Guid.NewGuid();
                containers.GetAsync(id, Arg.Any<MemoryCaller?>(), Arg.Any<CancellationToken>())
                    .Returns(kind is { } k ? new Container(id.ToString(), "c", null, Epoch, Epoch, Kind: k) : null);

                // Each table's source: 0 = in this container, 1 = in another container, 2 = gone; and whether it's readable.
                HashSet<Guid> expectedReadable = [];
                List<TableInfo> all = [];
                foreach (var (where, readable) in sources)
                {
                    TableInfo table = Table(id, $"t{all.Count}");
                    all.Add(table);
                    string doc = table.DocumentId!.Value.ToString();
                    if (where != 2)
                        documents.GetAsync(doc, Arg.Any<CancellationToken>()).Returns(new Document(
                            doc, where == 0 ? id.ToString() : Guid.NewGuid().ToString(), "f.csv", null, "/f.csv", 1, Epoch, []));
                    if (where == 0 && readable)
                        expectedReadable.Add(table.DocumentId.Value);
                }
                HashSet<string> readableIds = sources.Zip(all).Where(p => p.First.Item2).Select(p => p.Second.DocumentId!.Value.ToString()).ToHashSet();
                tables.ListAsync(id, Arg.Any<CancellationToken>()).Returns(all);
                access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
                    .Returns(c => accessFails
                        ? throw new InvalidOperationException("access store down")
                        : (IReadOnlySet<string>)c.ArgAt<IReadOnlyList<Document>>(1).Select(d => d.Id).Where(readableIds.Contains).ToHashSet());
                TableGraphQLResult answer = new(default, false, null);
                TableAccess? seen = null;
                graphql.RunAsync(id, Arg.Do<TableAccess>(a => seen = a), "{ _schema }", Arg.Any<CancellationToken>())
                    .Returns(_ => refused ? throw new TableQueryException("refused") : answer);

                var ran = await new RunTableGraphQL(containers, tables, documents, access, graphql, authorizer)
                    .RunAsync(Caller, id, hasQuery ? "{ _schema }" : " ");

                TableStatus expected = !reader ? TableStatus.Forbidden
                    : !hasQuery ? TableStatus.QueryNotRun
                    : kind is null ? TableStatus.ContainerNotFound
                    : kind == ContainerKind.Memory ? TableStatus.MemoryContainer
                    : accessFails ? TableStatus.AccessUnchecked
                    : refused ? TableStatus.QueryNotRun
                    : TableStatus.Success;
                ran.Status.Should().Be(expected);
                bool reached = reader && hasQuery && kind == ContainerKind.Documents;
                if (!reached)
                {
                    graphql.ReceivedCalls().Should().BeEmpty();
                    tables.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                if (accessFails)
                {
                    graphql.ReceivedCalls().Should().BeEmpty("an access check that failed reads nothing");
                    return;
                }
                seen!.ReadableDocuments.Should().BeEquivalentTo(expectedReadable, "only this container's readable sources count");
                if (refused)
                    ran.Detail.Should().Be("refused");
                else
                    ran.Result.Should().BeSameAs(answer);
            }, iter: 1000);

    [Fact]
    public async Task Hide_NotAnAdmin_IsForbiddenAndChangesNothing()
    {
        _authorizer.Allows = policy => policy != Policies.Admin;

        var hidden = await Hide.RunAsync(Caller, ContainerOfKind(ContainerKind.Documents), Guid.NewGuid(), ["a"]);

        hidden.Status.Should().Be(TableStatus.Forbidden);
        _tables.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Hide_UnknownColumn_IsInvalidColumn()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents), table = Guid.NewGuid();
        _tables.SetHiddenColumnsAsync(id, table, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ArgumentException("The table has no column \"z\"."));

        var hidden = await Hide.RunAsync(Caller, id, table, ["z"]);

        hidden.Status.Should().Be(TableStatus.InvalidColumn);
        hidden.Detail.Should().Be("The table has no column \"z\".");
    }

    [Fact]
    public async Task Hide_MissingTable_IsTableNotFound()
    {
        var hidden = await Hide.RunAsync(Caller, ContainerOfKind(ContainerKind.Documents), Guid.NewGuid(), ["a"]);

        hidden.Status.Should().Be(TableStatus.TableNotFound);
    }

    [Fact]
    public async Task Hide_KnownColumns_SavesThemAndReturnsTheTable()
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        TableInfo table = Table(id, "t") with { HiddenColumns = ["a"] };
        _tables.SetHiddenColumnsAsync(id, table.Id, Arg.Is<IReadOnlyList<string>>(c => c.SequenceEqual(new[] { "a" })), Arg.Any<CancellationToken>())
            .Returns(table);

        var hidden = await Hide.RunAsync(Caller, id, table.Id, ["a"]);

        hidden.Status.Should().Be(TableStatus.Success);
        hidden.Result.Should().Be(table);
    }

    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(HideTableColumns))]
    public Task Hide_AnyCallerContainerAndColumns_SavesOnlyForAnAdminOnADocumentsContainer() =>
        Gen.Select(Gen.Bool, Gen.Enum<ContainerKind>().Nullable(), Gen.Bool, Gen.String[Gen.Char.AlphaNumeric, 0, 4].Array[0, 4])
            .SampleAsync(async t =>
            {
                var (admin, kind, exists, columns) = t;
                var containers = Substitute.For<IContainerStore>();
                var tables = Substitute.For<ITableStore>();
                Guid id = Guid.NewGuid(), tableId = Guid.NewGuid();
                containers.GetAsync(id, Arg.Any<CancellationToken>())
                    .Returns(kind is { } k ? new Container(id.ToString(), "c", null, Epoch, Epoch, Kind: k) : null);
                bool known = columns.All(c => c.Length > 0);
                tables.SetHiddenColumnsAsync(id, tableId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                    .Returns(c => known
                        ? exists ? Table(id, "t") with { HiddenColumns = c.ArgAt<IReadOnlyList<string>>(2) } : null
                        : throw new ArgumentException("The table has no column \"\"."));

                var hidden = await new HideTableColumns(containers, tables, new PolicyAuthorizer(p => p == Policies.Admin && admin))
                    .RunAsync(Caller, id, tableId, columns);

                TableStatus expected = !admin ? TableStatus.Forbidden
                    : kind is null ? TableStatus.ContainerNotFound
                    : kind == ContainerKind.Memory ? TableStatus.MemoryContainer
                    : !known ? TableStatus.InvalidColumn
                    : !exists ? TableStatus.TableNotFound
                    : TableStatus.Success;
                hidden.Status.Should().Be(expected);
                if (expected is TableStatus.Forbidden or TableStatus.ContainerNotFound or TableStatus.MemoryContainer)
                    tables.ReceivedCalls().Should().BeEmpty();
                if (expected == TableStatus.Success)
                    hidden.Result!.HiddenColumns.Should().Equal(columns);
            }, iter: 1000);
}
