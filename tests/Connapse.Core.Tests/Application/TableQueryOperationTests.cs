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
/// CON-305: agents describe, read and aggregate a container's tables, optionally joining one other. Each operation is
/// for readers of a documents container, runs only over tables the caller may read (a missing table and an unreadable
/// one fail alike, joined or not), fails closed when access can't be checked, and hands back a refused request's
/// message so the agent can fix it. Listing can search names, descriptions and visible headers.
/// </summary>
[Trait("Category", "Unit")]
public class TableQueryOperationTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Mcp);
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TableQueryResult Answer = new(["a"], [["x"]], false, null);

    private readonly IContainerStore _containers = Substitute.For<IContainerStore>();
    private readonly ITableStore _tables = Substitute.For<ITableStore>();
    private readonly ITableQueryService _queries = Substitute.For<ITableQueryService>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IDocumentAccess _access = Substitute.For<IDocumentAccess>();
    private readonly PolicyAuthorizer _authorizer = new(_ => true);
    private readonly HashSet<Guid> _unreadable = [];
    private readonly Guid _containerId = Guid.NewGuid();

    public TableQueryOperationTests()
    {
        _containers.GetAsync(_containerId, Arg.Any<CancellationToken>())
            .Returns(new Container(_containerId.ToString(), "c", null, Epoch, Epoch, Kind: ContainerKind.Documents));
        _access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlySet<string>)c.ArgAt<IReadOnlyList<Document>>(1)
                .Select(d => d.Id).Where(id => !_unreadable.Contains(Guid.Parse(id))).ToHashSet());
        _queries.ReadAsync(default, default!, default!, default).ReturnsForAnyArgs(Answer);
        _queries.AggregateAsync(default, default!, default!, default).ReturnsForAnyArgs(Answer);
    }

    private DescribeTable Describe => new(_containers, _tables, _queries, _documents, _access, _authorizer);

    private ReadTable Read => new(_containers, _tables, _queries, _documents, _access, _authorizer);

    private AggregateTable Aggregate => new(_containers, _tables, _queries, _documents, _access, _authorizer);

    private ListTables List => new(_containers, _tables, _queries, _documents, _access, _authorizer);

    /// <summary>A table in the test's container from a document present in it, stored so it can be fetched and listed.</summary>
    private TableInfo Holds(string name = "sales", string? description = null, string? key = null, DateTime? historyFrom = null,
        IReadOnlyList<string>? hidden = null, params string[] columns)
    {
        Guid document = Guid.NewGuid();
        TableInfo table = new(Guid.NewGuid(), _containerId, name, description, document,
            [.. (columns.Length == 0 ? ["region", "amount"] : columns).Select(c => new TableColumn(c, TableColumnType.Text))],
            3, false, [], "tables_x", "t_x", 1, Epoch, Epoch, key, historyFrom, HiddenColumns: hidden);
        _tables.GetAsync(_containerId, table.Id, Arg.Any<CancellationToken>()).Returns(table);
        _documents.GetAsync(document.ToString(), Arg.Any<CancellationToken>())
            .Returns(new Document(document.ToString(), _containerId.ToString(), "t.csv", "text/csv", "/t.csv", 1, Epoch, []));
        return table;
    }

    private static TableDescription Description(TableInfo table) =>
        new(table.Id, table.Name, null, table.VisibleColumns, 3, Answer, false, []);

    private static TableJoin JoinTo(TableInfo table) => new(table.Id, "j", "region", "region");

    [Fact]
    public async Task Describe_NotAReader_IsForbiddenAndReadsNothing()
    {
        _authorizer.Allows = policy => policy != Policies.Reader;

        var described = await Describe.RunAsync(Caller, _containerId, Holds().Id);

        described.Status.Should().Be(TableStatus.Forbidden);
        _tables.ReceivedCalls().Should().BeEmpty();
        _queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Describe_MissingContainer_IsNotFound()
    {
        var described = await Describe.RunAsync(Caller, Guid.NewGuid(), Guid.NewGuid());

        described.Status.Should().Be(TableStatus.ContainerNotFound);
    }

    [Fact]
    public async Task Describe_MemoryContainer_IsRefusedAndReadsNoTables()
    {
        Guid memory = Guid.NewGuid();
        _containers.GetAsync(memory, Arg.Any<CancellationToken>())
            .Returns(new Container(memory.ToString(), "m", null, Epoch, Epoch, Kind: ContainerKind.Memory));

        var described = await Describe.RunAsync(Caller, memory, Guid.NewGuid());

        described.Status.Should().Be(TableStatus.MemoryContainer);
        _tables.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Describe_MissingAndUnreadableTables_FailAlikeAndQueryNothing()
    {
        Guid missing = Guid.NewGuid();
        TableInfo secret = Holds();
        _unreadable.Add(secret.DocumentId!.Value);

        var gone = await Describe.RunAsync(Caller, _containerId, missing);
        var hidden = await Describe.RunAsync(Caller, _containerId, secret.Id);

        gone.Status.Should().Be(TableStatus.TableNotFound);
        hidden.Status.Should().Be(TableStatus.TableNotFound);
        hidden.Detail.Should().Be(gone.Detail!.Replace(missing.ToString(), secret.Id.ToString(), StringComparison.Ordinal));
        hidden.Detail.Should().Contain("List the container's tables");
        _queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Describe_AccessCheckFails_IsAccessUncheckedAndQueriesNothing()
    {
        TableInfo table = Holds();
        _access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlySet<string>>(_ => throw new HttpRequestException("down"));

        var described = await Describe.RunAsync(Caller, _containerId, table.Id);

        described.Status.Should().Be(TableStatus.AccessUnchecked);
        described.Detail.Should().Be(TableAccess.UncheckedMessage);
        _queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Describe_ReadableTable_ReturnsTheDescriptionKeyAndHistoryStart()
    {
        DateTime from = Epoch.AddDays(3);
        TableInfo table = Holds(key: "region", historyFrom: from);
        TableDescription description = Description(table);
        _queries.DescribeAsync(_containerId, Arg.Is<TableAccess>(a => a.CanRead(table)), table.Id, Arg.Any<CancellationToken>()).Returns(description);

        var described = await Describe.RunAsync(Caller, _containerId, table.Id);

        described.Status.Should().Be(TableStatus.Success);
        described.Result.Should().Be(new TableDetails(description, "region", from));
    }

    [Theory]
    [InlineData("region", null, false)]
    [InlineData(null, "2026-01-04", false)]
    [InlineData("region", "2026-01-04", true)]
    public async Task Describe_HiddenKeyOrNoHistory_LeavesThemOut(string? key, string? historyFrom, bool hideKey)
    {
        TableInfo table = Holds(key: key, historyFrom: historyFrom is null ? null : DateTime.Parse(historyFrom),
            hidden: hideKey ? ["region"] : null);
        _queries.DescribeAsync(default, default!, default, default).ReturnsForAnyArgs(Description(table));

        var described = await Describe.RunAsync(Caller, _containerId, table.Id);

        described.Result!.Key.Should().Be(key is not null && !hideKey ? key : null);
        described.Result.HistoryFrom.Should().Be(table.HasHistory ? table.HistoryFrom : null);
    }

    [Fact]
    public async Task Describe_TableDeletedBeforeTheQuery_IsInvalidRequestWithTheServicesMessage()
    {
        TableInfo table = Holds();
        _queries.DescribeAsync(default, default!, default, default).ThrowsAsyncForAnyArgs(new TableQueryException("There is no table with id x."));

        var described = await Describe.RunAsync(Caller, _containerId, table.Id);

        described.Status.Should().Be(TableStatus.InvalidRequest);
        described.Detail.Should().Be("There is no table with id x.");
    }

    [Fact]
    public async Task Read_ReadableTable_PassesTheRequestAndTheCallersAccess()
    {
        TableInfo table = Holds();
        var request = new TableReadRequest(table.Id, ["region"]);

        var read = await Read.RunAsync(Caller, _containerId, request);

        read.Status.Should().Be(TableStatus.Success);
        read.Result.Should().BeSameAs(Answer);
        await _queries.Received(1).ReadAsync(_containerId, Arg.Is<TableAccess>(a => a.CanRead(table)), request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Read_RefusedRequest_IsInvalidRequestWithWhatToFix()
    {
        TableInfo table = Holds();
        _queries.ReadAsync(default, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new TableQueryException("Table \"sales\" has no column \"x\". Its columns are: \"region\"."));

        var read = await Read.RunAsync(Caller, _containerId, new TableReadRequest(table.Id, ["x"]));

        read.Status.Should().Be(TableStatus.InvalidRequest);
        read.Detail.Should().Contain("Its columns are");
    }

    [Fact]
    public async Task Read_JoinedTableUnreadable_IsTableNotFoundForTheJoinedTable()
    {
        TableInfo table = Holds(), other = Holds("owners");
        _unreadable.Add(other.DocumentId!.Value);

        var read = await Read.RunAsync(Caller, _containerId, new TableReadRequest(table.Id, Join: JoinTo(other)));

        read.Status.Should().Be(TableStatus.TableNotFound);
        read.Detail.Should().Contain(other.Id.ToString()).And.NotContain(table.Id.ToString());
        _queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Read_JoinedTableMissing_IsTableNotFound()
    {
        TableInfo table = Holds();
        Guid missing = Guid.NewGuid();

        var read = await Read.RunAsync(Caller, _containerId, new TableReadRequest(table.Id, Join: new TableJoin(missing, "j", "a", "b")));

        read.Status.Should().Be(TableStatus.TableNotFound);
        read.Detail.Should().Contain(missing.ToString());
        _queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Read_SelfJoin_LoadsTheTableOnce()
    {
        TableInfo table = Holds();

        var read = await Read.RunAsync(Caller, _containerId, new TableReadRequest(table.Id, Join: JoinTo(table)));

        read.Status.Should().Be(TableStatus.Success);
        await _tables.Received(1).GetAsync(_containerId, table.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Read_NullRequest_Throws()
    {
        Func<Task> act = () => Read.RunAsync(Caller, _containerId, null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Aggregate_JoinedReadableTable_PassesAccessForBothTables()
    {
        TableInfo table = Holds(), other = Holds("owners");
        var request = new TableAggregateRequest(table.Id, [new TableField(null, TableAggregateFunction.Count)], Join: JoinTo(other));

        var aggregated = await Aggregate.RunAsync(Caller, _containerId, request);

        aggregated.Status.Should().Be(TableStatus.Success);
        await _queries.Received(1).AggregateAsync(_containerId,
            Arg.Is<TableAccess>(a => a.CanRead(table) && a.CanRead(other)), request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Aggregate_RefusedRequest_IsInvalidRequest()
    {
        TableInfo table = Holds();
        _queries.AggregateAsync(default, default!, default!, default).ThrowsAsyncForAnyArgs(new TableQueryException("sum doesn't work on text."));

        var aggregated = await Aggregate.RunAsync(Caller, _containerId,
            new TableAggregateRequest(table.Id, [new TableField("region", TableAggregateFunction.Sum)]));

        aggregated.Status.Should().Be(TableStatus.InvalidRequest);
        aggregated.Detail.Should().Be("sum doesn't work on text.");
    }

    [Fact]
    public async Task Aggregate_NullRequest_Throws()
    {
        Func<Task> act = () => Aggregate.RunAsync(Caller, _containerId, null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData("SALES", "sales")]
    [InlineData("quarter", "costs")]
    [InlineData("manager", "owners")]
    [InlineData("  owners ", "owners")]
    public async Task List_Search_KeepsTablesWhoseNameDescriptionOrVisibleHeaderMatches(string search, string expected)
    {
        TableInfo[] all =
        [
            Holds("sales", hidden: ["manager"], columns: ["region", "manager"]),
            Holds("costs", description: "Costs by Quarter"),
            Holds("owners", columns: ["region", "Manager"]),
        ];
        _tables.ListAsync(_containerId, Arg.Any<CancellationToken>()).Returns(all);

        var listed = await List.RunAsync(Caller, _containerId, 0, 50, search);

        listed.Result!.Items.Select(t => t.Name).Should().Equal(expected);
        listed.Result.TotalCount.Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task List_BlankSearch_ListsEveryReadableTable(string? search)
    {
        TableInfo[] all = [Holds("a"), Holds("b")];
        _tables.ListAsync(_containerId, Arg.Any<CancellationToken>()).Returns(all);

        var listed = await List.RunAsync(Caller, _containerId, 0, 50, search);

        listed.Result!.TotalCount.Should().Be(2);
    }

    /// <summary>One sample's world: a container that may be missing or memory, and tables that may be missing or unreadable.</summary>
    private sealed class World
    {
        public readonly IContainerStore Containers = Substitute.For<IContainerStore>();
        public readonly ITableStore Tables = Substitute.For<ITableStore>();
        public readonly ITableQueryService Queries = Substitute.For<ITableQueryService>();
        public readonly IDocumentStore Documents = Substitute.For<IDocumentStore>();
        public readonly IDocumentAccess Access = Substitute.For<IDocumentAccess>();
        public readonly PolicyAuthorizer Authorizer;
        public readonly Guid ContainerId = Guid.NewGuid();
        public readonly Guid TableId = Guid.NewGuid();
        public readonly Guid JoinedId = Guid.NewGuid();
        private readonly HashSet<string> _readable = [];

        /// <param name="table">Whether the queried table exists, and whether it's readable.</param>
        /// <param name="joined">The same for the joined table.</param>
        public World(bool reader, ContainerKind? kind, (bool Exists, bool Readable) table, (bool Exists, bool Readable) joined, bool checkFails)
        {
            Authorizer = new PolicyAuthorizer(policy => policy == Policies.Reader && reader);
            Containers.GetAsync(ContainerId, Arg.Any<CancellationToken>())
                .Returns(kind is { } k ? new Container(ContainerId.ToString(), "c", null, Epoch, Epoch, Kind: k) : null);
            Add(TableId, table);
            Add(JoinedId, joined);
            if (checkFails)
                Access.ReadableAsync(default!, default!, default).ReturnsForAnyArgs<IReadOnlySet<string>>(_ => throw new TimeoutException());
            else
                Access.ReadableAsync(default!, default!, default).ReturnsForAnyArgs(_readable);
            Queries.ReadAsync(default, default!, default!, default).ReturnsForAnyArgs(Answer);
            Queries.AggregateAsync(default, default!, default!, default).ReturnsForAnyArgs(Answer);
            Queries.DescribeAsync(default, default!, default, default)
                .ReturnsForAnyArgs(new TableDescription(TableId, "t", null, [], 1, Answer, false, []));
        }

        private void Add(Guid id, (bool Exists, bool Readable) state)
        {
            if (!state.Exists)
                return;
            Guid document = Guid.NewGuid();
            Tables.GetAsync(ContainerId, id, Arg.Any<CancellationToken>()).Returns(new TableInfo(id, ContainerId, "t", null, document,
                [new TableColumn("a", TableColumnType.Text)], 1, false, [], "tables_x", "t_x", 1, Epoch, Epoch));
            Documents.GetAsync(document.ToString(), Arg.Any<CancellationToken>())
                .Returns(new Document(document.ToString(), ContainerId.ToString(), "t.csv", null, "/t.csv", 1, Epoch, []));
            if (state.Readable)
                _readable.Add(document.ToString());
        }

        /// <summary>The first rule a request over the queried table, and the joined one when there is a join, breaks.</summary>
        public TableStatus Expected(bool reader, ContainerKind? kind, (bool Exists, bool Readable) table, (bool Exists, bool Readable)? joined, bool checkFails)
        {
            (bool Exists, bool Readable)[] all = joined is { } j ? [table, j] : [table];
            return !reader ? TableStatus.Forbidden
                : kind is null ? TableStatus.ContainerNotFound
                : kind == ContainerKind.Memory ? TableStatus.MemoryContainer
                : all.Any(t => !t.Exists) ? TableStatus.TableNotFound
                : checkFails ? TableStatus.AccessUnchecked
                : all.Any(t => !t.Readable) ? TableStatus.TableNotFound
                : TableStatus.Success;
        }
    }

    private static readonly Gen<(bool, bool)> TableState = Gen.Select(Gen.Bool, Gen.Bool);

    private static readonly Gen<(bool Reader, ContainerKind? Kind, (bool, bool) Table, (bool, bool) Joined, bool CheckFails)> Situation =
        Gen.Select(Gen.Bool, Gen.Enum<ContainerKind>().Nullable(), TableState, TableState, Gen.Int[0, 4].Select(i => i == 0));

    private static void Check(World world, TableStatus expected, TableStatus status)
    {
        status.Should().Be(expected);
        if (expected != TableStatus.Success)
            world.Queries.ReceivedCalls().Should().BeEmpty("a refused request runs no query");
    }

    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(DescribeTable))]
    public Task Describe_AnyCallerContainerAndTable_DescribesOnlyAReadableTableOrSaysTheFirstRuleItBreaks() =>
        Situation.SampleAsync(async t =>
        {
            var world = new World(t.Reader, t.Kind, t.Table, t.Joined, t.CheckFails);

            var described = await new DescribeTable(world.Containers, world.Tables, world.Queries, world.Documents, world.Access, world.Authorizer)
                .RunAsync(Caller, world.ContainerId, world.TableId);

            Check(world, world.Expected(t.Reader, t.Kind, t.Table, null, t.CheckFails), described.Status);
        }, iter: 1000);

    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(ReadTable))]
    public Task Read_AnyCallerContainerTableAndJoin_ReadsOnlyReadableTablesOrSaysTheFirstRuleItBreaks() =>
        Gen.Select(Situation, Gen.Bool).SampleAsync(async s =>
        {
            var (t, join) = s;
            var world = new World(t.Reader, t.Kind, t.Table, t.Joined, t.CheckFails);
            var request = new TableReadRequest(world.TableId, Join: join ? new TableJoin(world.JoinedId, "j", "a", "a") : null);

            var read = await new ReadTable(world.Containers, world.Tables, world.Queries, world.Documents, world.Access, world.Authorizer)
                .RunAsync(Caller, world.ContainerId, request);

            Check(world, world.Expected(t.Reader, t.Kind, t.Table, join ? t.Joined : null, t.CheckFails), read.Status);
        }, iter: 1000);

    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(AggregateTable))]
    public Task Aggregate_AnyCallerContainerTableAndJoin_CountsOnlyReadableTablesOrSaysTheFirstRuleItBreaks() =>
        Gen.Select(Situation, Gen.Bool).SampleAsync(async s =>
        {
            var (t, join) = s;
            var world = new World(t.Reader, t.Kind, t.Table, t.Joined, t.CheckFails);
            var request = new TableAggregateRequest(world.TableId, [new TableField(null, TableAggregateFunction.Count)],
                Join: join ? new TableJoin(world.JoinedId, "j", "a", "a") : null);

            var aggregated = await new AggregateTable(world.Containers, world.Tables, world.Queries, world.Documents, world.Access, world.Authorizer)
                .RunAsync(Caller, world.ContainerId, request);

            Check(world, world.Expected(t.Reader, t.Kind, t.Table, join ? t.Joined : null, t.CheckFails), aggregated.Status);
        }, iter: 1000);
}
