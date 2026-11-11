using System.Diagnostics;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Connapse.Storage.Tables;

/// <summary>
/// Runs agent-written table queries (CON-326, ticket c of docs/plans/con-320-raw-sql-rls.md). The scope is the
/// container's tables whose source document is in the request's <see cref="TableAccess"/>; tables outside it don't
/// appear in refusals or error listings. The text goes through <see cref="TableSqlCheck"/>, then
/// <see cref="TableQueryRunner"/> sends it unchanged with the container's schema on <c>search_path</c>, under the reader
/// role, row-level security and the runner's limits. Database errors come back as SQLSTATE class 22 and 42 messages
/// and hints only, never the fields that can quote internal queries or values
/// (docs/research/con-326-table-sql-runner-2026-11-10.md).
/// <para>
/// A table with columns an admin hid (CON-304) is left out of the scope altogether, so <see cref="TableSqlCheck"/>
/// refuses any query that names it: the reader role's grant covers every column, and only the parser could keep a
/// hidden one out of <c>SELECT *</c> or a whole-row reference. A merged table is in the scope when the caller may read
/// any of its documents; row-level security then shows only the rows of those documents.
/// </para>
/// </summary>
public sealed class TableSqlService(
    ITableStore tables,
    TableReaderLogin reader,
    TableQueryRunner runner,
    ILogger<TableSqlService> logger) : ITableSqlService
{
    /// <summary>The most rows one answer holds; later rows are dropped and the answer says so.</summary>
    internal const int MaxRows = 1000;

    /// <summary>The longest database error message passed on, in characters.</summary>
    internal const int MaxErrorMessage = 200;

    /// <summary>The longest table listing put in a refusal or error, in characters.</summary>
    internal const int MaxListing = 4000;

    /// <summary>
    /// Whether queries go through <see cref="TableSqlCheck"/>. Only tests turn it off, to show the database layers hold
    /// on their own.
    /// </summary>
    internal bool CheckQueries { get; init; } = true;

    public async Task<TableQueryResult> RunAsync(Guid containerId, TableAccess access, string sql, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(sql);

        bool retried = false;
        while (true)
        {
            List<TableInfo> canRead = (await tables.ListAsync(containerId, ct)).Where(access.CanRead).ToList();
            IReadOnlyList<TableInfo> readable = canRead.Where(t => t.HiddenColumns is not { Count: > 0 }).ToList();
            if (readable.Count == 0)
                throw new TableQueryException(canRead.Count == 0
                    ? "This container has no tables you can query."
                    : "This container has no tables you can query with SQL: an administrator hid columns in each of its tables.");
            string schema = readable[0].StorageSchema;

            if (CheckQueries)
            {
                TableSqlVerdict verdict = TableSqlCheck.Check(
                    sql, new TableSqlScope(schema, readable.Select(t => t.StorageName).ToHashSet(StringComparer.Ordinal)));
                if (!verdict.Allowed)
                    throw new TableQueryException(
                        $"The query can't run: {verdict.Detail}. Write one SELECT over these tables:\n{Listing(readable)}");
            }

            await reader.EnsureGrantsAsync(containerId, schema, ct);
            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                TableQueryResult result = await runner.RunAsync(containerId, access, new CompiledTableQuery(sql, [], null, MaxRows, schema), ct);
                logger.LogDebug("Table SQL in {ContainerId} returned {Rows} rows in {Elapsed} ms", containerId, result.Rows.Count, elapsed.ElapsedMilliseconds);
                return result;
            }
            catch (PostgresException ex) when (!retried && ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                // The schema was recreated (grants gone) since we looked.
                retried = true;
                reader.ForgetGrants(schema);
            }
            catch (PostgresException ex)
            {
                logger.LogInformation("Table SQL in {ContainerId} failed with {SqlState} after {Elapsed} ms", containerId, ex.SqlState, elapsed.ElapsedMilliseconds);
                throw Describe(ex, readable);
            }
        }
    }

    /// <summary>
    /// A message for a database error that names what to fix: the message, hint and position of a syntax, name or type
    /// error (class 42) or a data error (class 22), cut to <see cref="MaxErrorMessage"/>, plus the table listing when a
    /// name was wrong. Every other error gets <see cref="TableQueryRunner.Describe"/>'s plain message.
    /// </summary>
    internal static TableQueryException Describe(PostgresException ex, IReadOnlyList<TableInfo> readable)
    {
        if (ex.SqlState == PostgresErrorCodes.ReadOnlySqlTransaction)
            return new TableQueryException("Table queries can only read.");
        if (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
            return new TableQueryException($"The query couldn't run: {Detail(ex)}. These are the tables you can query:\n{Listing(readable)}");
        if (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            return new TableQueryException("The query reads something table queries may not read. Read only the listed tables.");
        if (ex.SqlState.StartsWith("42", StringComparison.Ordinal))
            return new TableQueryException($"The query couldn't run: {Detail(ex)}.");
        if (ex.SqlState.StartsWith("22", StringComparison.Ordinal))
            return new TableQueryException($"The query couldn't be computed: {Detail(ex)}.");
        return TableQueryRunner.Describe(ex);
    }

    private static string Detail(PostgresException ex)
    {
        StringBuilder text = new(Cut(ex.MessageText));
        if (ex.Position > 0)
            text.Append(" (at character ").Append(ex.Position).Append(')');
        if (!string.IsNullOrEmpty(ex.Hint))
            text.Append(". Hint: ").Append(Cut(ex.Hint));
        return text.ToString();
    }

    private static string Cut(string text) => text.Length <= MaxErrorMessage ? text : text[..MaxErrorMessage] + "…";

    /// <summary>
    /// The tables an agent may query, one per line, by the names its SQL must use, with the headers they came from:
    /// <c>t_… "Sales" (12 rows): c_1 "Region" text, c_2 "Amount" numeric</c>. Cut at <see cref="MaxListing"/>.
    /// </summary>
    internal static string Listing(IReadOnlyList<TableInfo> readable)
    {
        StringBuilder text = new();
        foreach (TableInfo table in readable)
        {
            StringBuilder line = new();
            line.Append("- ").Append(table.StorageName).Append(' ').Append(Quoted(table.Name))
                .Append(" (").Append(table.RowCount).Append(" rows): ");
            line.AppendJoin(", ", table.Columns.Select((column, i) =>
                $"{TableDdl.ColumnName(i)} {Quoted(column.Name)} {TableDdl.SqlType(column.Type)}"));
            if (table.IsMerged)
                line.Append($"; merged from several files, each row's file id in {TableDdl.DocumentColumn} (uuid)");
            if (table.HasHistory)
                line.Append($"; with history in {TableHistory.SeenFrom} and {TableHistory.SeenUntil} (timestamptz), current rows have {TableHistory.SeenUntil} = 'infinity'");

            if (text.Length + line.Length + 1 > MaxListing)
            {
                text.Append("- (more tables not shown)");
                break;
            }
            text.Append(line).Append('\n');
        }
        return text.ToString().TrimEnd('\n');
    }

    private static string Quoted(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
