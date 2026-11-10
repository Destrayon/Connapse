using System.Globalization;
using System.Text;
using Connapse.Core;
using Npgsql;

namespace Connapse.Storage.Tables;

/// <summary>
/// Runs one table query on the reader login and shapes its answer (CON-303). The structured tools use it today, and
/// <c>table_sql</c> (CON-326) is meant to run through it too, so both answer under the same limits and in the same
/// shape (docs/plans/con-320-raw-sql-rls.md):
/// <list type="bullet">
/// <item>a <c>READ ONLY</c> transaction that sets <c>statement_timeout</c>, <c>lock_timeout</c>, <c>work_mem</c>,
/// <c>max_parallel_workers_per_gather</c> and a fixed <c>search_path</c> with <c>set_config(…, true)</c>, and is
/// always rolled back;</item>
/// <item>the query sent as the only command of an <see cref="NpgsqlBatch"/>, which Npgsql never splits, so the text
/// can only ever be one statement;</item>
/// <item>a client-side deadline a little past the statement timeout: when it fires, Npgsql cancels the query from a
/// separate connection, so a server that ignores its own timeout still can't hold the caller;</item>
/// <item>rows capped at the query's limit and the answer at <see cref="MaxAnswerBytes"/> UTF-8 bytes while streaming,
/// with text cells cut at <see cref="TableQueryCompiler.MaxCellCharacters"/>; the answer says when it was cut, and
/// always carries <see cref="TableQueryResult.Notice"/>.</item>
/// </list>
/// Database errors other than the client deadline are thrown as <see cref="PostgresException"/>; callers turn them into
/// messages with <see cref="Describe"/>.
/// </summary>
public sealed class TableQueryRunner(TableReaderLogin reader)
{
    /// <summary>Roughly how many bytes of cell text one answer may hold before later rows are dropped.</summary>
    internal const int MaxAnswerBytes = 64 * 1024;

    /// <summary>No parallel workers, so a query's memory is bounded by <c>work_mem</c> per step, not times the workers.</summary>
    internal const string MaxParallelWorkersPerGather = "0";

    /// <summary>When the client gives up and cancels the query itself.</summary>
    internal TimeSpan ClientTimeout { get; init; } = TimeSpan.FromSeconds(TableReaderLogin.StatementTimeoutSeconds + 2);

    internal async Task<TableQueryResult> RunAsync(CompiledTableQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ClientTimeout);
        try
        {
            return await ExecuteAsync(query, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TimedOut();
        }
    }

    private async Task<TableQueryResult> ExecuteAsync(CompiledTableQuery query, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await reader.OpenConnectionAsync(ct);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);

        await using (NpgsqlBatch setup = new(connection, transaction))
        {
            setup.BatchCommands.Add(new NpgsqlBatchCommand("SET TRANSACTION READ ONLY"));
            setup.BatchCommands.Add(new NpgsqlBatchCommand(
                "SELECT pg_catalog.set_config('statement_timeout', $1, true), pg_catalog.set_config('lock_timeout', $2, true), "
                + "pg_catalog.set_config('idle_in_transaction_session_timeout', $3, true), pg_catalog.set_config('work_mem', $4, true), "
                + "pg_catalog.set_config('max_parallel_workers_per_gather', $5, true), "
                + "pg_catalog.set_config('search_path', 'pg_catalog, pg_temp', true)")
            {
                Parameters =
                {
                    new NpgsqlParameter { Value = TableReaderLogin.StatementTimeout },
                    new NpgsqlParameter { Value = TableReaderLogin.LockTimeout },
                    new NpgsqlParameter { Value = TableReaderLogin.IdleInTransactionTimeout },
                    new NpgsqlParameter { Value = TableReaderLogin.WorkMem },
                    new NpgsqlParameter { Value = MaxParallelWorkersPerGather },
                },
            });
            await setup.ExecuteNonQueryAsync(ct);
        }

        List<IReadOnlyList<object?>> rows = [];
        IReadOnlyList<string> columns;
        string? truncated = null;
        await using (NpgsqlBatch batch = new(connection, transaction))
        {
            NpgsqlBatchCommand command = new(query.Sql);
            foreach (NpgsqlParameter parameter in query.Parameters)
                command.Parameters.Add(parameter.Clone());
            batch.BatchCommands.Add(command);

            await using NpgsqlDataReader data = await batch.ExecuteReaderAsync(ct);
            columns = query.Columns ?? Enumerable.Range(0, data.FieldCount).Select(data.GetName).ToList();
            int bytes = 0;
            while (await data.ReadAsync(ct))
            {
                if (rows.Count == query.Limit)
                {
                    truncated = $"More rows match; only the first {query.Limit} are shown. Use offset or a narrower filter to see more.";
                    break;
                }

                object?[] row = new object?[data.FieldCount];
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = Cell(data, i, ref truncated);
                    bytes += Size(row[i]);
                }
                rows.Add(row);

                if (bytes > MaxAnswerBytes)
                {
                    truncated = $"The answer was cut at {rows.Count} rows to keep it under {MaxAnswerBytes} bytes. Ask for fewer columns or rows.";
                    break;
                }
            }
        }

        await transaction.RollbackAsync(ct);
        return new TableQueryResult(columns, rows, truncated is not null, truncated);
    }

    /// <summary>A cell's size in the answer: its UTF-8 bytes, with null counted as the four letters agents see.</summary>
    internal static int Size(object? value) => value switch
    {
        null => 4,
        string text => Encoding.UTF8.GetByteCount(text),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Length ?? 0,
    };

    private static object? Cell(NpgsqlDataReader data, int ordinal, ref string? truncated)
    {
        if (data.IsDBNull(ordinal))
            return null;
        if (data.GetDataTypeName(ordinal) == "date")
            return data.GetFieldValue<DateOnly>(ordinal);

        object value = data.GetValue(ordinal);
        if (value is string text && text.Length > TableQueryCompiler.MaxCellCharacters)
        {
            truncated ??= $"Some text cells were longer than {TableQueryCompiler.MaxCellCharacters} characters and were cut.";
            return text[..TableQueryCompiler.MaxCellCharacters];
        }
        return value;
    }

    private static TableQueryException TimedOut() => new(
        $"The query took longer than {TableReaderLogin.StatementTimeout} and was stopped. Add a filter, group by fewer columns or ask for fewer rows.");

    /// <summary>A plain message for a database error, without the query text or any cell value.</summary>
    internal static TableQueryException Describe(PostgresException ex) => ex.SqlState switch
    {
        PostgresErrorCodes.QueryCanceled => TimedOut(),
        PostgresErrorCodes.LockNotAvailable => new TableQueryException("The table is being reloaded. Try again in a moment."),
        PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InsufficientPrivilege => new TableQueryException(
            "The table changed while it was being queried. Try again."),
        _ when ex.SqlState.StartsWith("53", StringComparison.Ordinal) || ex.SqlState.StartsWith("54", StringComparison.Ordinal) =>
            new TableQueryException("The query needed more memory or disk than a table query may use. Add a filter or group by fewer columns."),
        _ when ex.SqlState.StartsWith("22", StringComparison.Ordinal) =>
            new TableQueryException($"The query couldn't be computed: {ex.MessageText}."),
        _ => new TableQueryException("The query failed in the database."),
    };
}
