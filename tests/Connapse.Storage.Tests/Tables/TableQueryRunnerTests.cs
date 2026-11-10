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
/// CON-303 after CON-320: the readable-document check, the answer's size count and the database error messages. The
/// runner's database behaviour (limits, cancel, byte cap) is covered by the integration tests.
/// </summary>
[Trait("Category", "Unit")]
public class TableQueryRunnerTests
{
    private static readonly Guid ContainerId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static TableInfo Table(Guid? document) => new(
        Guid.NewGuid(), ContainerId, "sales", null, document, [new TableColumn("region", TableColumnType.Text)], 10, false, [],
        "tables_11111111222233334444555555555555", "t_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1, DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public void CanRead_DocumentInTheSet_IsTrue()
    {
        Guid document = Guid.NewGuid();

        new TableAccess(new HashSet<Guid> { document }).CanRead(Table(document)).Should().BeTrue();
    }

    [Fact]
    public void CanRead_DocumentNotInTheSet_IsFalse()
    {
        new TableAccess(new HashSet<Guid> { Guid.NewGuid() }).CanRead(Table(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public void CanRead_TableWithNoSourceDocument_IsFalseEvenWithAnEmptyGuidInTheSet()
    {
        new TableAccess(new HashSet<Guid> { Guid.Empty, Guid.NewGuid() }).CanRead(Table(null)).Should().BeFalse();
    }

    [Fact]
    public void CanRead_RandomSetsAndTables_IsTrueExactlyWhenTheSourceDocumentIsInTheSet()
    {
        Random random = new(303);
        Guid[] pool = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        for (int i = 0; i < 500; i++)
        {
            HashSet<Guid> readable = pool.Where(_ => random.Next(2) == 0).ToHashSet();
            int pick = random.Next(pool.Length + 1);
            Guid? document = pick == pool.Length ? null : pool[pick];

            bool canRead = new TableAccess(readable).CanRead(Table(document));

            canRead.Should().Be(document is not null && readable.Contains(document.Value), $"case {i}");
        }
    }

    [Theory]
    [InlineData(null, 4)]
    [InlineData("abc", 3)]
    [InlineData("é", 2)]
    [InlineData("日本", 6)]
    [InlineData(12.5, 4)]
    [InlineData(-7L, 2)]
    [InlineData(true, 4)]
    public void Size_Cell_CountsUtf8BytesOrTheInvariantText(object? value, int expected)
    {
        TableQueryRunner.Size(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.QueryCanceled, "*took longer than*")]
    [InlineData(PostgresErrorCodes.LockNotAvailable, "*being reloaded*")]
    [InlineData(PostgresErrorCodes.UndefinedTable, "*table changed*")]
    [InlineData(PostgresErrorCodes.InsufficientPrivilege, "*table changed*")]
    [InlineData(PostgresErrorCodes.OutOfMemory, "*more memory or disk*")]
    [InlineData(PostgresErrorCodes.DiskFull, "*more memory or disk*")]
    [InlineData(PostgresErrorCodes.ConfigurationLimitExceeded, "*more memory or disk*")]
    [InlineData(PostgresErrorCodes.DivisionByZero, "The query couldn't be computed: secret detail.")]
    [InlineData(PostgresErrorCodes.SyntaxError, "The query failed in the database.")]
    public void Describe_DatabaseError_GivesAPlainMessage(string sqlState, string expected)
    {
        PostgresException ex = new("secret detail", "ERROR", "ERROR", sqlState);

        TableQueryRunner.Describe(ex).Message.Should().Match(expected);
    }

    [Fact]
    public async Task ReadAsync_NullAccess_Throws()
    {
        await using Service service = new(Table(Guid.NewGuid()));

        Func<Task> act = () => service.Query.ReadAsync(ContainerId, null!, new TableReadRequest(Guid.NewGuid()));

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AggregateAsync_NullAccess_Throws()
    {
        await using Service service = new(Table(Guid.NewGuid()));

        Func<Task> act = () => service.Query.AggregateAsync(ContainerId, null!,
            new TableAggregateRequest(Guid.NewGuid(), [new TableField(null, TableAggregateFunction.Count)]));

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    public static TheoryData<string> Unreadable => ["missing", "other document", "no document"];

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task ReadAsync_UnreadableTable_IsNotFoundBeforeCompiling(string kind)
    {
        Guid readable = Guid.NewGuid();
        TableInfo? table = kind switch
        {
            "missing" => null,
            "other document" => Table(Guid.NewGuid()),
            _ => Table(null),
        };
        await using Service service = new(table);
        Guid tableId = table?.Id ?? Guid.NewGuid();

        // An unknown column would make the compiler list the table's columns; the access check must come first.
        Func<Task> act = () => service.Query.ReadAsync(
            ContainerId, new TableAccess(new HashSet<Guid> { readable }), new TableReadRequest(tableId, ["no such column"]));

        (await act.Should().ThrowAsync<TableQueryException>())
            .Which.Message.Should().Be($"There is no table with id {tableId} in this container.");
    }

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task AggregateAsync_UnreadableTable_IsNotFoundBeforeCompiling(string kind)
    {
        TableInfo? table = kind switch
        {
            "missing" => null,
            "other document" => Table(Guid.NewGuid()),
            _ => Table(null),
        };
        await using Service service = new(table);
        Guid tableId = table?.Id ?? Guid.NewGuid();

        Func<Task> act = () => service.Query.AggregateAsync(ContainerId, new TableAccess(new HashSet<Guid>()),
            new TableAggregateRequest(tableId, [new TableField("no such column", TableAggregateFunction.Sum)]));

        (await act.Should().ThrowAsync<TableQueryException>())
            .Which.Message.Should().Be($"There is no table with id {tableId} in this container.");
    }

    /// <summary>A service over a substitute catalog and a reader login that is never opened.</summary>
    private sealed class Service : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _admin = NpgsqlDataSource.Create("Host=unused");
        private readonly TableReaderLogin _login;

        public Service(TableInfo? table)
        {
            ITableStore tables = Substitute.For<ITableStore>();
            tables.GetAsync(ContainerId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);
            _login = new TableReaderLogin(_admin, "Host=unused", new ConfigurationBuilder().Build(), NullLogger<TableReaderLogin>.Instance);
            Query = new TableQueryService(tables, _login, new TableQueryRunner(_login), NullLogger<TableQueryService>.Instance);
        }

        public TableQueryService Query { get; }

        public async ValueTask DisposeAsync()
        {
            await _login.DisposeAsync();
            await _admin.DisposeAsync();
        }
    }
}
