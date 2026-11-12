using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Files;
using Connapse.Application.Ports;
using Connapse.Application.Tables;
using Connapse.Core;
using Connapse.Core.Interfaces;
using CsCheck;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// CON-312: a search passage that holds a table the caller may read shows a note naming the table in place of its
/// text and lists it; a table the caller may not read, or one an admin hid, leaves the passage as stored and isn't
/// named. A file's tables are listed to its readers, and only admins hide or show a whole table.
/// </summary>
[Trait("Category", "Unit")]
public class PassageTablesTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Prose = "Revenue by region:\n";
    private const string Grid = "| Region | Revenue |\n| --- | --- |\n| East | 10 |\n";
    private const string After = "\nEast led the year.";

    private readonly ITablePassageLinks _links = Substitute.For<ITablePassageLinks>();
    private readonly ITableStore _tables = Substitute.For<ITableStore>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IDocumentAccess _access = Substitute.For<IDocumentAccess>();
    private readonly HashSet<string> _unreadable = [];
    private readonly Guid _container = Guid.NewGuid();
    private readonly Guid _document = Guid.NewGuid();

    public PassageTablesTests()
    {
        _access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlySet<string>)c.ArgAt<IReadOnlyList<Document>>(1)
                .Select(d => d.Id).Where(id => !_unreadable.Contains(id)).ToHashSet());
        _documents.GetAsync(_document.ToString(), Arg.Any<CancellationToken>())
            .Returns(new Document(_document.ToString(), _container.ToString(), "report.pdf", "application/pdf", "/report.pdf", 1, Epoch, []));
    }

    private PassageTables Passages => new(_links, _tables, _documents, _access);

    private TableInfo Table(string name, int number, bool hidden = false) =>
        new(Guid.NewGuid(), _container, name, null, _document,
            [new TableColumn("Region", TableColumnType.Text), new TableColumn("Revenue", TableColumnType.WholeNumber)], 1, false, [],
            "tables_x", "t_x", 1, Epoch, Epoch, Placement: new TablePlacement(3, number, "h"), Hidden: hidden);

    /// <summary>One hit whose passage is <paramref name="content"/>, linked to each table over its span.</summary>
    private SearchResult Hit(string content, params (TableInfo Table, int Start, int Length)[] spans)
    {
        Guid chunk = Guid.NewGuid();
        _tables.ListByDocumentAsync(_container, _document, Arg.Any<CancellationToken>()).Returns(spans.Select(s => s.Table).ToList());
        _links.ForChunksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<TablePassageLink>>
            {
                [chunk] = spans.Select(s => new TablePassageLink(s.Table.Id, chunk, s.Start, s.Length)).ToList(),
            });
        return new SearchResult([new SearchHit(chunk.ToString(), _document.ToString(), content, 0.9f, [])], 1, TimeSpan.Zero);
    }

    [Fact]
    public async Task Apply_ReadableTable_ReplacesItsTextWithANoteAndListsIt()
    {
        TableInfo table = Table("Revenue", 1);
        var result = Hit(Prose + Grid + After, (table, Prose.Length, Grid.Length));

        SearchHit hit = (await Passages.ApplyAsync(Caller.User, result)).Hits.Single();

        hit.Content.Should().Be(Prose + PassageTables.Note(table) + After);
        hit.Content.Should().Contain(table.Id.ToString()).And.Contain("table_read").And.Contain("page 3");
        hit.Tables.Should().ContainSingle().Which.Should().Be(new PassageTable(table.Id, _container, "Revenue", 3, 1));
    }

    [Fact]
    public async Task Apply_TwoTables_ReplacesEachInPlaceAndListsThemInOrder()
    {
        TableInfo first = Table("First", 1), second = Table("Second", 2);
        string content = Prose + Grid + After + Grid;
        var result = Hit(content, (second, content.Length - Grid.Length, Grid.Length), (first, Prose.Length, Grid.Length));

        SearchHit hit = (await Passages.ApplyAsync(Caller.User, result)).Hits.Single();

        hit.Content.Should().Be(Prose + PassageTables.Note(first) + After + PassageTables.Note(second));
        hit.Tables!.Select(t => t.Name).Should().Equal("First", "Second");
    }

    [Fact]
    public async Task Apply_TableFromAnUnreadableDocument_LeavesThePassageAndNamesNothing()
    {
        _unreadable.Add(_document.ToString());
        TableInfo table = Table("Salaries", 1);
        var result = Hit(Prose + Grid, (table, Prose.Length, Grid.Length));

        SearchHit hit = (await Passages.ApplyAsync(Caller.User, result)).Hits.Single();

        hit.Content.Should().Be(Prose + Grid);
        hit.Tables.Should().BeNull();
    }

    [Fact]
    public async Task Apply_HiddenTable_LeavesThePassageAndNamesNothing()
    {
        TableInfo table = Table("Salaries", 1, hidden: true);
        var result = Hit(Prose + Grid, (table, Prose.Length, Grid.Length));

        SearchHit hit = (await Passages.ApplyAsync(Caller.User, result)).Hits.Single();

        hit.Content.Should().Be(Prose + Grid);
        hit.Tables.Should().BeNull();
        hit.Content.Should().NotContain(table.Id.ToString());
    }

    [Fact]
    public async Task Apply_AccessCheckFails_LeavesEveryHitAsItCame()
    {
        _access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));
        var result = Hit(Prose + Grid, (Table("Revenue", 1), Prose.Length, Grid.Length));

        (await Passages.ApplyAsync(Caller.User, result)).Should().BeSameAs(result);
    }

    [Fact]
    public async Task Apply_LinksCantBeRead_LeavesEveryHitAsItCame()
    {
        var result = Hit(Prose + Grid, (Table("Revenue", 1), Prose.Length, Grid.Length));
        _links.ForChunksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));

        (await Passages.ApplyAsync(Caller.User, result)).Should().BeSameAs(result);
    }

    [Fact]
    public async Task Apply_SpanThatIsntTableText_KeepsTheTextButStillListsTheTable()
    {
        TableInfo table = Table("Revenue", 1);
        const string window = "East led the year, with revenue well ahead of the rest of the company's regions.";
        var result = Hit(window, (table, 5, 20));

        SearchHit hit = (await Passages.ApplyAsync(Caller.User, result)).Hits.Single();

        hit.Content.Should().Be(window, "a sentence window doesn't hold the stored span, so cutting it would garble the text");
        hit.Tables.Should().ContainSingle().Which.Id.Should().Be(table.Id);
    }

    [Fact]
    [Trait("Size", "Medium")]
    public Task Apply_AnySpan_NeverThrowsAndChangesTheTextOnlyForAReadableTable() =>
        Gen.Select(Gen.String[Gen.Char["ab |\n"], 0, 40], Gen.Int[-5, 45], Gen.Int[-5, 45], Gen.Bool, Gen.Bool)
            .SampleAsync(async t =>
            {
                var (content, start, length, hidden, unreadable) = t;
                var links = Substitute.For<ITablePassageLinks>();
                var tables = Substitute.For<ITableStore>();
                TableInfo table = Table("t", 1, hidden);
                Guid chunk = Guid.NewGuid();
                tables.ListByDocumentAsync(_container, _document, Arg.Any<CancellationToken>()).Returns([table]);
                links.ForChunksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                    .Returns(new Dictionary<Guid, IReadOnlyList<TablePassageLink>> { [chunk] = [new TablePassageLink(table.Id, chunk, start, length)] });
                var access = Substitute.For<IDocumentAccess>();
                access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
                    .Returns(c => (IReadOnlySet<string>)(unreadable ? new HashSet<string>() : c.ArgAt<IReadOnlyList<Document>>(1).Select(d => d.Id).ToHashSet()));
                var result = new SearchResult([new SearchHit(chunk.ToString(), _document.ToString(), content, 1f, [])], 1, TimeSpan.Zero);

                SearchHit hit = (await new PassageTables(links, tables, _documents, access).ApplyAsync(Caller.User, result)).Hits.Single();

                if (hidden || unreadable)
                {
                    hit.Content.Should().Be(content);
                    hit.Tables.Should().BeNull();
                }
                else if (hit.Content != content)
                {
                    hit.Content.Should().Be(content[..start] + PassageTables.Note(table) + content[(start + length)..]);
                }
            }, iter: 1000);

    private readonly ITableQueryService _queries = Substitute.For<ITableQueryService>();

    private PassageTables Previewing => new(_links, _tables, _documents, _access, _queries);

    private static TableDescription Described(TableInfo table, long rowCount, IReadOnlyList<string> columns, params object?[][] rows) =>
        new(table.Id, table.Name, null, table.Columns, rowCount,
            new TableQueryResult(columns, rows.Select(r => (IReadOnlyList<object?>)r).ToList(), false, null), false, []);

    [Fact]
    public async Task Apply_TableNamedByTwoHits_PreviewsItOnTheFirstOnlyWithTheCallersAccess()
    {
        TableInfo table = Table("Revenue", 1);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        _tables.ListByDocumentAsync(_container, _document, Arg.Any<CancellationToken>()).Returns([table]);
        _links.ForChunksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<TablePassageLink>>
            {
                [first] = [new TablePassageLink(table.Id, first, Prose.Length, Grid.Length)],
                [second] = [new TablePassageLink(table.Id, second, 0, Grid.Length)],
            });
        _queries.DescribeAsync(_container, Arg.Any<TableAccess>(), table.Id, Arg.Any<CancellationToken>())
            .Returns(Described(table, 2, ["Region", "Revenue"], ["East", 10L], ["West", 7L]));
        var result = new SearchResult(
            [new SearchHit(first.ToString(), _document.ToString(), Prose + Grid, 0.9f, []),
             new SearchHit(second.ToString(), _document.ToString(), Grid, 0.8f, [])], 2, TimeSpan.Zero);

        var hits = (await Previewing.ApplyAsync(Caller.User, result)).Hits;

        hits[0].Tables!.Single().Preview.Should().BeEquivalentTo(new TablePreview(["Region", "Revenue"], 0, [["East", "10"], ["West", "7"]], 2));
        hits[1].Tables!.Single().Preview.Should().BeNull("one preview per table per search keeps the output small");
        await _queries.Received(1).DescribeAsync(_container,
            Arg.Is<TableAccess>(a => a.Checked && a.ReadableDocuments.Contains(_document)), table.Id, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Apply_HiddenOrUnreadableTable_ReadsNoPreview(bool hidden, bool unreadable)
    {
        if (unreadable)
            _unreadable.Add(_document.ToString());
        TableInfo table = Table("Salaries", 1, hidden);
        var result = Hit(Prose + Grid, (table, Prose.Length, Grid.Length));

        SearchHit hit = (await Previewing.ApplyAsync(Caller.User, result)).Hits.Single();

        hit.Tables.Should().BeNull();
        _queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_PreviewCantBeRead_StillNamesTheTable()
    {
        TableInfo table = Table("Revenue", 1);
        var result = Hit(Prose + Grid, (table, Prose.Length, Grid.Length));
        _queries.DescribeAsync(Arg.Any<Guid>(), Arg.Any<TableAccess>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));

        SearchHit hit = (await Previewing.ApplyAsync(Caller.User, result)).Hits.Single();

        hit.Content.Should().Be(Prose + PassageTables.Note(table));
        hit.Tables.Should().ContainSingle().Which.Should().Be(new PassageTable(table.Id, _container, "Revenue", 3, 1));
    }

    [Fact]
    public void Preview_WideTableWithLongCells_KeepsItSmall()
    {
        TableInfo table = Table("Wide", 1);
        string[] columns = Enumerable.Range(1, 12).Select(i => $"c{i}").ToArray();
        object?[] row = [new string('x', 100) + "\nmore", null, .. Enumerable.Range(0, 10).Select(i => (object?)i)];

        TablePreview preview = PassageTables.Preview(Described(table, 500, columns, row, row, row, row, row));

        preview.Columns.Should().Equal(columns.Take(PassageTables.PreviewColumns));
        preview.MoreColumns.Should().Be(4);
        preview.Rows.Should().HaveCount(PassageTables.PreviewRows);
        preview.Rows[0][0].Should().HaveLength(PassageTables.PreviewCell).And.EndWith("…").And.NotContain("\n");
        preview.Rows[0][1].Should().BeEmpty();
        preview.RowCount.Should().Be(500);
    }

    [Fact]
    [Trait("Size", "Medium")]
    public void Preview_AnyTable_StaysWithinItsCaps() =>
        Gen.Select(Gen.Int[0, 20], Gen.Int[0, 10], Gen.String[Gen.Char["ab\n\r\t |"], 0, 120])
            .Sample((width, height, cell) =>
            {
                TableInfo table = Table("t", 1);
                var columns = Enumerable.Range(0, width).Select(i => $"c{i}").ToList();
                var rows = Enumerable.Range(0, height).Select(_ => Enumerable.Repeat((object?)cell, width).ToArray()).ToArray();

                TablePreview preview = PassageTables.Preview(Described(table, height, columns, rows));

                preview.Columns.Count.Should().Be(Math.Min(width, PassageTables.PreviewColumns));
                (preview.Columns.Count + preview.MoreColumns).Should().Be(width);
                preview.Rows.Count.Should().Be(Math.Min(height, PassageTables.PreviewRows));
                preview.Rows.SelectMany(r => r).Should().OnlyContain(c => c.Length <= PassageTables.PreviewCell
                    && !c.Contains('\n') && !c.Contains('\r') && !c.Contains('\t'));
            }, iter: 1000);

    private GetFileTables Files(IContainerStore containers, IOperationAuthorizer authorizer) =>
        new(containers, _documents, _access, _tables, authorizer);

    private IContainerStore Holding()
    {
        var containers = Substitute.For<IContainerStore>();
        containers.GetAsync(_container, Arg.Any<CancellationToken>())
            .Returns(new Container(_container.ToString(), "c", null, Epoch, Epoch, Kind: ContainerKind.Documents));
        _access.CanReadAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(true);
        return containers;
    }

    [Fact]
    public async Task FileTables_Viewer_SeesReadableTablesButNotHiddenOnes()
    {
        TableInfo shown = Table("Revenue", 1), hidden = Table("Salaries", 2, hidden: true);
        _tables.ListByDocumentAsync(_container, _document, Arg.Any<CancellationToken>()).Returns([hidden, shown]);

        var listed = await Files(Holding(), new PolicyAuthorizer(p => p != Policies.Admin)).RunAsync(Caller, _container, _document.ToString());

        listed.Result.Should().Equal(new PassageTable(shown.Id, _container, "Revenue", 3, 1));
    }

    [Fact]
    public async Task FileTables_Admin_SeesHiddenTablesMarked()
    {
        TableInfo shown = Table("Revenue", 1), hidden = Table("Salaries", 2, hidden: true);
        _tables.ListByDocumentAsync(_container, _document, Arg.Any<CancellationToken>()).Returns([hidden, shown]);

        var listed = await Files(Holding(), new PolicyAuthorizer(_ => true)).RunAsync(Caller, _container, _document.ToString());

        listed.Result!.Select(t => (t.Name, t.Hidden)).Should().Equal(("Revenue", false), ("Salaries", true));
    }

    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(GetFileTables))]
    public Task FileTables_AnyCallerAndFile_ListsOnlyWhatTheCallerMayRead() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Bool, Gen.Int[0, 2], Gen.Bool.Array[0, 4], Gen.Bool)
            .SampleAsync(async t =>
            {
                var (reader, admin, exists, file, hiddenFlags, accessFails) = t;
                var containers = Substitute.For<IContainerStore>();
                var documents = Substitute.For<IDocumentStore>();
                var access = Substitute.For<IDocumentAccess>();
                var tables = Substitute.For<ITableStore>();
                Guid container = Guid.NewGuid(), document = Guid.NewGuid();
                if (exists)
                    containers.GetAsync(container, Arg.Any<CancellationToken>())
                        .Returns(new Container(container.ToString(), "c", null, Epoch, Epoch, Kind: ContainerKind.Documents));
                if (file > 0)
                    documents.GetAsync(document.ToString(), Arg.Any<CancellationToken>())
                        .Returns(new Document(document.ToString(), container.ToString(), "r.pdf", "application/pdf", "/r.pdf", 1, Epoch, []));
                access.CanReadAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(file == 2);
                if (accessFails)
                    access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
                        .ThrowsAsync(new InvalidOperationException("db down"));
                else
                    access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
                        .Returns(c => (IReadOnlySet<string>)c.ArgAt<IReadOnlyList<Document>>(1).Select(d => d.Id).ToHashSet());
                var all = hiddenFlags.Select((h, i) => new TableInfo(Guid.NewGuid(), container, $"t{i}", null, document, [], 0, false, [],
                    "tables_x", "t_x", 1, Epoch, Epoch, Placement: new TablePlacement(null, i + 1, "h"), Hidden: h)).ToList();
                tables.ListByDocumentAsync(container, document, Arg.Any<CancellationToken>()).Returns(all);
                var authorizer = new PolicyAuthorizer(p => p == Policies.Reader ? reader : p == Policies.Admin && admin);

                var listed = await new GetFileTables(containers, documents, access, tables, authorizer).RunAsync(Caller, container, document.ToString());

                FileStatus expected = !reader ? FileStatus.Forbidden
                    : !exists ? FileStatus.ContainerNotFound
                    : file != 2 ? FileStatus.NotFound
                    : accessFails && all.Count > 0 ? FileStatus.LinksUnchecked
                    : FileStatus.Success;
                listed.Status.Should().Be(expected);
                if (expected == FileStatus.Success)
                    listed.Result!.Select(p => (p.Name, p.Hidden)).Should()
                        .Equal(all.Where(x => admin || !x.Hidden).Select(x => (x.Name, x.Hidden)));
                else
                    listed.Result.Should().BeNull();
            }, iter: 1000);

    [Fact]
    public async Task Hide_NotAnAdmin_IsForbiddenAndChangesNothing()
    {
        var hidden = await new HideTable(Holding(), _tables, new PolicyAuthorizer(p => p != Policies.Admin))
            .RunAsync(Caller, _container, Guid.NewGuid(), true);

        hidden.Status.Should().Be(TableStatus.Forbidden);
        _tables.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(HideTable))]
    public Task Hide_AnyCallerContainerAndTable_SavesOnlyForAnAdminOnADocumentsContainer() =>
        Gen.Select(Gen.Bool, Gen.Enum<ContainerKind>().Nullable(), Gen.Bool, Gen.Bool)
            .SampleAsync(async t =>
            {
                var (admin, kind, exists, hide) = t;
                var containers = Substitute.For<IContainerStore>();
                var tables = Substitute.For<ITableStore>();
                Guid id = Guid.NewGuid(), tableId = Guid.NewGuid();
                containers.GetAsync(id, Arg.Any<CancellationToken>())
                    .Returns(kind is { } k ? new Container(id.ToString(), "c", null, Epoch, Epoch, Kind: k) : null);
                tables.SetHiddenAsync(id, tableId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
                    .Returns(c => exists ? Table("t", 1) with { Id = tableId, Hidden = c.ArgAt<bool>(2) } : null);

                var hidden = await new HideTable(containers, tables, new PolicyAuthorizer(p => p == Policies.Admin && admin))
                    .RunAsync(Caller, id, tableId, hide);

                TableStatus expected = !admin ? TableStatus.Forbidden
                    : kind is null ? TableStatus.ContainerNotFound
                    : kind == ContainerKind.Memory ? TableStatus.MemoryContainer
                    : !exists ? TableStatus.TableNotFound
                    : TableStatus.Success;
                hidden.Status.Should().Be(expected);
                if (expected is TableStatus.Forbidden or TableStatus.ContainerNotFound or TableStatus.MemoryContainer)
                    tables.ReceivedCalls().Should().BeEmpty();
                if (expected == TableStatus.Success)
                    hidden.Result!.Hidden.Should().Be(hide);
            }, iter: 1000);
}
