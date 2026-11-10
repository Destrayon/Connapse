using System.Diagnostics;
using System.Globalization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Connapse.Storage.Tables;

/// <summary>
/// Runs agents' structured table requests (CON-303). The table is looked up in the catalog by container and id, so any
/// container that has tables can be queried whatever its kind. The request is compiled to one parameterized
/// <c>SELECT</c> and run on the reader login (<see cref="TableReaderLogin"/>) inside a read-only transaction with a
/// statement timeout, lock timeout, <c>work_mem</c> cap and a fixed <c>search_path</c>, which is always rolled back.
/// Answers are capped at the request's row limit, <see cref="TableQueryCompiler.MaxCellCharacters"/> per text cell
/// and <see cref="MaxAnswerCharacters"/> overall, and say when they were cut.
/// </summary>
public sealed class TableQueryService(
    ITableStore tables,
    TableReaderLogin reader,
    ILogger<TableQueryService> logger) : ITableQueryService
{
    /// <summary>Roughly how much text one answer may hold before later rows are dropped.</summary>
    internal const int MaxAnswerCharacters = 50_000;

    public Task<TableQueryResult> ReadAsync(Guid containerId, TableReadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(containerId, request.TableId, table => TableQueryCompiler.CompileRead(table, request), ct);
    }

    public Task<TableQueryResult> AggregateAsync(Guid containerId, TableAggregateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(containerId, request.TableId, table => TableQueryCompiler.CompileAggregate(table, request), ct);
    }

    private async Task<TableQueryResult> RunAsync(Guid containerId, Guid tableId, Func<TableInfo, CompiledTableQuery> compile, CancellationToken ct)
    {
        bool retried = false;
        while (true)
        {
            TableInfo table = await tables.GetAsync(containerId, tableId, ct)
                ?? throw new TableQueryException($"There is no table with id {tableId} in this container.");
            CompiledTableQuery query = compile(table);
            await reader.EnsureGrantsAsync(containerId, table.StorageSchema, ct);

            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                TableQueryResult result = await ExecuteAsync(query, ct);
                logger.LogDebug("Table query on {TableId} returned {Rows} rows in {Elapsed} ms", tableId, result.Rows.Count, elapsed.ElapsedMilliseconds);
                return result;
            }
            catch (PostgresException ex) when (!retried && ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InsufficientPrivilege)
            {
                // The table was reloaded (new storage name) or its schema recreated (grants gone) since we looked.
                retried = true;
                if (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
                    reader.ForgetGrants(table.StorageSchema);
            }
            catch (PostgresException ex)
            {
                logger.LogInformation("Table query on {TableId} failed with {SqlState} after {Elapsed} ms", tableId, ex.SqlState, elapsed.ElapsedMilliseconds);
                throw Friendly(ex);
            }
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
                + "pg_catalog.set_config('search_path', 'pg_catalog, pg_temp', true)")
            {
                Parameters =
                {
                    new NpgsqlParameter { Value = TableReaderLogin.StatementTimeout },
                    new NpgsqlParameter { Value = TableReaderLogin.LockTimeout },
                    new NpgsqlParameter { Value = TableReaderLogin.IdleInTransactionTimeout },
                    new NpgsqlParameter { Value = TableReaderLogin.WorkMem },
                },
            });
            await setup.ExecuteNonQueryAsync(ct);
        }

        List<IReadOnlyList<object?>> rows = [];
        string? truncated = null;
        await using (NpgsqlCommand command = new(query.Sql, connection, transaction))
        {
            foreach (NpgsqlParameter parameter in query.Parameters)
                command.Parameters.Add(parameter.Clone());

            await using NpgsqlDataReader data = await command.ExecuteReaderAsync(ct);
            int characters = 0;
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
                    characters += row[i] switch
                    {
                        null => 4,
                        string text => text.Length,
                        object value => Convert.ToString(value, CultureInfo.InvariantCulture)?.Length ?? 0,
                    };
                }
                rows.Add(row);

                if (characters > MaxAnswerCharacters)
                {
                    truncated = $"The answer was cut at {rows.Count} rows to keep it under {MaxAnswerCharacters} characters. Ask for fewer columns or rows.";
                    break;
                }
            }
        }

        await transaction.RollbackAsync(ct);
        return new TableQueryResult(query.Columns, rows, truncated is not null, truncated);
    }

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

    private static TableQueryException Friendly(PostgresException ex) => ex.SqlState switch
    {
        PostgresErrorCodes.QueryCanceled => new TableQueryException(
            $"The query took longer than {TableReaderLogin.StatementTimeout} and was stopped. Add a filter, group by fewer columns or ask for fewer rows."),
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
