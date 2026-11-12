using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Tables;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Connapse.Storage.Tests.Tables;

/// <summary>
/// CON-326: the scope an agent's SQL gets, what a refusal and a database error show, and the runner's search path. The
/// database layers (grants, row-level security, limits) are covered by the integration tests.
/// </summary>
[Trait("Category", "Unit")]
public class TableSqlServiceTests
{
    private static readonly Guid ContainerId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Schema = "tables_11111111222233334444555555555555";

    private static TableInfo Table(Guid? document, string storageName, string name = "Sales", Guid? key = null) => new(
        Guid.NewGuid(), ContainerId, name, null, document,
        [new TableColumn("Region", TableColumnType.Text), new TableColumn("Amount", TableColumnType.Number)], 12, false, [],
        Schema, storageName, 1, DateTime.UtcNow, DateTime.UtcNow,
        key is null ? null : "Region", key is null ? null : DateTime.UtcNow);

    private const string Readable = "t_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Hidden = "t_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task RunAsync_NullAccessOrSql_Throws()
    {
        await using Service service = new();

        Func<Task> noAccess = () => service.Sql.RunAsync(ContainerId, null!, "SELECT 1");
        Func<Task> noSql = () => service.Sql.RunAsync(ContainerId, new TableAccess(new HashSet<Guid>()), null!);

        await noAccess.Should().ThrowAsync<ArgumentNullException>();
        await noSql.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RunAsync_NoReadableTables_SaysSoWithoutNamingAny()
    {
        await using Service service = new(Table(Guid.NewGuid(), Hidden, "Secret payroll"), Table(null, Hidden));

        Func<Task> act = () => service.Sql.RunAsync(ContainerId, new TableAccess(new HashSet<Guid> { Guid.NewGuid() }), "SELECT 1");

        (await act.Should().ThrowAsync<TableQueryException>())
            .Which.Message.Should().Be("This container has no tables you can query.");
    }

    [Fact]
    public async Task RunAsync_QueryNamesATableTheCallerMayNotRead_IsRefusedWithOnlyTheReadableTablesListed()
    {
        Guid document = Guid.NewGuid();
        await using Service service = new(Table(document, Readable), Table(Guid.NewGuid(), Hidden, "Secret payroll"));

        Func<Task> act = () => service.Sql.RunAsync(ContainerId, new TableAccess(new HashSet<Guid> { document }), $"SELECT * FROM {Hidden}");

        string message = (await act.Should().ThrowAsync<TableQueryException>()).Which.Message;
        message.Should().StartWith("The query can't run: ");
        message.Should().Contain($"{Readable} \"Sales\" (12 rows): c_1 \"Region\" text, c_2 \"Amount\" numeric");
        message.Should().NotContain("Secret payroll");
        message.Split('\n').Skip(1).Should().ContainSingle();
    }

    [Theory]
    [InlineData("SELECT pg_sleep(10)")]
    [InlineData("SET ROLE postgres")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("DELETE FROM t_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task RunAsync_QueryTheCheckRefuses_NeverReachesTheDatabase(string sql)
    {
        Guid document = Guid.NewGuid();
        await using Service service = new(Table(document, Readable));

        // The login's host is "unused": reaching the database would throw a socket error, not this refusal.
        Func<Task> act = () => service.Sql.RunAsync(ContainerId, new TableAccess(new HashSet<Guid> { document }), sql);

        (await act.Should().ThrowAsync<TableQueryException>()).Which.Message.Should().StartWith("The query can't run: ");
    }

    [Fact]
    public void Listing_TableWithHistory_SaysHowToReadCurrentRows()
    {
        string listing = TableSqlService.Listing([Table(Guid.NewGuid(), Readable, key: Guid.NewGuid())]);

        listing.Should().Contain("_seen_from and _seen_until (timestamptz), current rows have _seen_until = 'infinity'");
    }

    [Fact]
    public void Listing_NameWithAQuote_DoublesIt()
    {
        TableSqlService.Listing([Table(Guid.NewGuid(), Readable, "Q1 \"final\"")]).Should().Contain("\"Q1 \"\"final\"\"\"");
    }

    [Fact]
    public void Listing_MoreTablesThanTheColumnsFit_NamesEveryTable()
    {
        // CON-328: the old listing stopped at 4,000 characters, so tables past it couldn't be named in SQL at all.
        TableInfo[] many = Enumerable.Range(0, 300).Select(i => Table(Guid.NewGuid(), $"t_{i:x32}", $"Sheet {i}")).ToArray();

        string[] lines = TableSqlService.Listing(many).Split('\n');

        lines.Should().HaveCount(300);
        lines.Select((line, i) => line.StartsWith($"- t_{i:x32} \"Sheet {i}\" (12 rows)", StringComparison.Ordinal))
            .Should().AllBeEquivalentTo(true);
        lines[0].Should().Contain("c_1 \"Region\" text, c_2 \"Amount\" numeric");
        lines[^1].Should().Be($"- t_{299:x32} \"Sheet 299\" (12 rows), table id {many[^1].Id}: columns c_1, c_2, ... in the order table_describe lists them");
        string.Join('\n', lines.TakeWhile(l => l.Contains("c_1 \"Region\"", StringComparison.Ordinal))).Length
            .Should().BeLessThanOrEqualTo(TableSqlService.MaxListing);
    }

    [Fact]
    public void Listing_MoreTablesThanTheCap_SaysHowManyMoreExist()
    {
        TableInfo[] many = Enumerable.Range(0, TableSqlService.MaxListedTables + 7)
            .Select(i => Table(Guid.NewGuid(), $"t_{i:x32}")).ToArray();

        string[] lines = TableSqlService.Listing(many).Split('\n');

        lines.Should().HaveCount(TableSqlService.MaxListedTables + 1);
        lines[^1].Should().Be("- (7 more tables not shown)");
    }

    [Theory]
    [InlineData(PostgresErrorCodes.SyntaxError, "The query couldn't run: syntax error at or near \"FORM\" (at character 10). Hint: try FROM.")]
    [InlineData(PostgresErrorCodes.UndefinedFunction, "The query couldn't run: syntax error at or near \"FORM\" (at character 10). Hint: try FROM.")]
    [InlineData(PostgresErrorCodes.DivisionByZero, "The query couldn't be computed: syntax error at or near \"FORM\" (at character 10). Hint: try FROM.")]
    public void Describe_NameTypeOrDataError_GivesMessagePositionAndHintOnly(string sqlState, string expected)
    {
        PostgresException ex = Error(sqlState);

        TableSqlService.Describe(ex, []).Message.Should().Be(expected);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UndefinedTable)]
    [InlineData(PostgresErrorCodes.UndefinedColumn)]
    public void Describe_UnknownName_ListsTheReadableTables(string sqlState)
    {
        string message = TableSqlService.Describe(Error(sqlState), [Table(Guid.NewGuid(), Readable)]).Message;

        message.Should().Contain("These are the tables you can query:").And.Contain(Readable);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.SyntaxError)]
    [InlineData(PostgresErrorCodes.UndefinedColumn)]
    [InlineData(PostgresErrorCodes.InvalidTextRepresentation)]
    [InlineData(PostgresErrorCodes.QueryCanceled)]
    [InlineData(PostgresErrorCodes.InsufficientPrivilege)]
    [InlineData(PostgresErrorCodes.ReadOnlySqlTransaction)]
    [InlineData(PostgresErrorCodes.InternalError)]
    [InlineData(PostgresErrorCodes.RaiseException)]
    public void Describe_AnyError_NeverShowsDetailWhereInternalQueryOrConstraint(string sqlState)
    {
        string message = TableSqlService.Describe(Error(sqlState), []).Message;

        message.Should().NotContainAny("secret detail", "secret where", "secret internal", "secret constraint", "secret routine");
    }

    [Theory]
    [InlineData(PostgresErrorCodes.QueryCanceled, "*took longer than*")]
    [InlineData(PostgresErrorCodes.OutOfMemory, "*more memory or disk*")]
    [InlineData(PostgresErrorCodes.ReadOnlySqlTransaction, "Table queries can only read.")]
    [InlineData(PostgresErrorCodes.InsufficientPrivilege, "*may not read*")]
    [InlineData(PostgresErrorCodes.RaiseException, "The query failed in the database.")]
    public void Describe_OtherErrors_GiveThePlainMessages(string sqlState, string expected)
    {
        TableSqlService.Describe(Error(sqlState), []).Message.Should().Match(expected);
    }

    [Fact]
    public void Describe_LongMessage_IsCut()
    {
        PostgresException ex = new(new string('x', 5000), "ERROR", "ERROR", PostgresErrorCodes.InvalidTextRepresentation);

        TableSqlService.Describe(ex, []).Message.Length.Should().BeLessThan(TableSqlService.MaxErrorMessage + 60);
    }

    [Fact]
    public void SearchPath_PutsTheCatalogFirstAndTheSchemaBeforeTemp()
    {
        TableQueryRunner.SearchPath(Schema).Should().Be($"pg_catalog, \"{Schema}\", pg_temp");
        TableQueryRunner.SearchPath(null).Should().Be("pg_catalog, pg_temp");
    }

    private static PostgresException Error(string sqlState) => new(
        "syntax error at or near \"FORM\"", "ERROR", "ERROR", sqlState,
        detail: "secret detail", hint: "try FROM", position: 10, internalPosition: 3, internalQuery: "secret internal",
        where: "secret where", constraintName: "secret constraint", routine: "secret routine");

    /// <summary>A service over a substitute catalog and a reader login that is never opened.</summary>
    private sealed class Service : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _admin = NpgsqlDataSource.Create("Host=unused");
        private readonly TableReaderLogin _login;

        public Service(params TableInfo[] tables)
        {
            ITableStore store = Substitute.For<ITableStore>();
            store.ListAsync(ContainerId, Arg.Any<CancellationToken>()).Returns(tables);
            _login = new TableReaderLogin(_admin, "Host=unused", new ConfigurationBuilder().Build(), NullLogger<TableReaderLogin>.Instance);
            Sql = new TableSqlService(store, _login, new TableQueryRunner(_login), NullLogger<TableSqlService>.Instance);
        }

        public TableSqlService Sql { get; }

        public async ValueTask DisposeAsync()
        {
            await _login.DisposeAsync();
            await _admin.DisposeAsync();
        }
    }
}
