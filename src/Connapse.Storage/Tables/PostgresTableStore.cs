using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Storage.Tables;

/// <summary>
/// Containers' tables (CON-301), held beside their files (CON-319). A load builds a new table and points the catalog entry at it in one
/// transaction, so the table appears, or its new version replaces the old one, only once every row is in. The table a
/// reload replaces stays until <see cref="DropRetiredTablesAsync"/> drops it after a grace period, so a reader that
/// resolved the old name just before the reload can still finish.
/// </summary>
public class PostgresTableStore(
    IDbContextFactory<KnowledgeDbContext> factory,
    ILogger<PostgresTableStore> logger) : ITableStore
{
    /// <summary>How long a replaced or deleted table is kept for readers that already resolved its name.</summary>
    public static readonly TimeSpan RetiredTableGrace = TimeSpan.FromMinutes(15);

    private const int MaxNameLength = 256;

    public async Task<TableInfo> CreateAsync(Guid containerId, TableLoad load, CancellationToken ct = default)
    {
        Validate(load);
        await using var context = await factory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var connection = context.Database.GetDbConnection();

        await PrepareLoadAsync(context, connection, containerId, ct);
        string storageName = TableDdl.NewTableName();
        long rows = await BuildTableAsync(connection, containerId, storageName, load, ct);

        DateTime now = DateTime.UtcNow;
        var entity = new ContainerTableEntity
        {
            Id = Guid.NewGuid(),
            ContainerId = containerId,
            CreatedAt = now,
        };
        Apply(entity, load, storageName, rows, now);
        entity.Version = 1;
        context.ContainerTables.Add(entity);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("Created table {TableId} ({Name}) in container {ContainerId}: {Rows} rows in {StorageName}",
            entity.Id, Sanitize(entity.Name), containerId, rows, storageName);
        return ToInfo(entity);
    }

    public async Task<TableInfo?> ReloadAsync(Guid containerId, Guid tableId, TableLoad load, CancellationToken ct = default)
    {
        Validate(load);
        await using var context = await factory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var connection = context.Database.GetDbConnection();

        await PrepareLoadAsync(context, connection, containerId, ct);
        // Locked until commit, so two reloads of one table queue rather than both retire the same old table.
        var entity = await LockEntryAsync(context, containerId, tableId, ct);
        if (entity is null)
            return null;

        string oldName = entity.StorageName;
        string storageName = TableDdl.NewTableName();
        long rows = await BuildTableAsync(connection, containerId, storageName, load, ct);

        // The swap: the entry now names the new table. Readers resolve the name from here, so none of them waits on a
        // lock or sees the new table before this commits.
        Apply(entity, load, storageName, rows, DateTime.UtcNow);
        entity.Version++;
        context.RetiredTables.Add(new RetiredTableEntity
        {
            ContainerId = containerId,
            StorageName = oldName,
            RetiredAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("Reloaded table {TableId} in container {ContainerId}: {Rows} rows in {StorageName}, replacing {OldName}",
            tableId, containerId, rows, storageName, oldName);
        return ToInfo(entity);
    }

    public async Task<TableInfo?> GetAsync(Guid containerId, Guid tableId, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        var entity = await context.ContainerTables.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tableId && t.ContainerId == containerId, ct);
        return entity is null ? null : ToInfo(entity);
    }

    public async Task<IReadOnlyList<TableInfo>> ListAsync(Guid containerId, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        var entities = await context.ContainerTables.AsNoTracking()
            .Where(t => t.ContainerId == containerId)
            .OrderBy(t => t.Name).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .ToListAsync(ct);
        return entities.Select(ToInfo).ToList();
    }

    public async Task<TablePage> ListAsync(Guid containerId, int skip, int take, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        await using var context = await factory.CreateDbContextAsync(ct);
        var inContainer = context.ContainerTables.AsNoTracking().Where(t => t.ContainerId == containerId);
        int total = await inContainer.CountAsync(ct);
        List<ContainerTableEntity> entities = take == 0 || skip >= total
            ? []
            : await inContainer
                .OrderBy(t => t.Name).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id)
                .Skip(skip).Take(take)
                .ToListAsync(ct);
        return new TablePage(entities.Select(ToInfo).ToList(), total);
    }

    public async Task<TableInfo?> FindByDocumentAsync(Guid containerId, Guid documentId, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        var entity = await context.ContainerTables.AsNoTracking()
            .Where(t => t.ContainerId == containerId && t.DocumentId == documentId)
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : ToInfo(entity);
    }

    public async Task<bool> DeleteAsync(Guid containerId, Guid tableId, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var entity = await LockEntryAsync(context, containerId, tableId, ct);
        if (entity is null)
            return false;

        context.ContainerTables.Remove(entity);
        context.RetiredTables.Add(new RetiredTableEntity
        {
            ContainerId = containerId,
            StorageName = entity.StorageName,
            RetiredAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("Deleted table {TableId} from container {ContainerId}", tableId, containerId);
        return true;
    }

    /// <summary>
    /// Drops the tables retired more than <paramref name="grace"/> ago and returns how many it dropped. A table still
    /// in use is skipped after a short wait and tried again on the next run, so the drop never queues readers.
    /// </summary>
    public async Task<int> DropRetiredTablesAsync(TimeSpan grace, CancellationToken ct = default)
    {
        DateTime cutoff = DateTime.UtcNow - grace;
        await using var context = await factory.CreateDbContextAsync(ct);
        var due = await context.RetiredTables.AsNoTracking()
            .Where(r => r.RetiredAt <= cutoff)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        int dropped = 0;
        foreach (var retired in due)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            var connection = context.Database.GetDbConnection();
            try
            {
                await TableDdl.ExecuteAsync(connection, "SET LOCAL lock_timeout = '2s'", ct);
                await TableDdl.ExecuteAsync(connection,
                    $"DROP TABLE IF EXISTS {TableDdl.QualifiedName(retired.ContainerId, retired.StorageName)}", ct);
                await context.RetiredTables.Where(r => r.Id == retired.Id).ExecuteDeleteAsync(ct);
                await transaction.CommitAsync(ct);
                dropped++;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                await transaction.RollbackAsync(ct);
                logger.LogInformation("Retired table {StorageName} is still in use; dropping it on a later run", retired.StorageName);
            }
        }
        return dropped;
    }

    /// <summary>
    /// Holds the container's shared load lock, checks it can hold tables, and makes sure its schema exists: a
    /// container's schema is made by its first table load.
    /// </summary>
    private static async Task PrepareLoadAsync(
        KnowledgeDbContext context, DbConnection connection, Guid containerId, CancellationToken ct)
    {
        await TableDdl.LockContainerAsync(connection, containerId, shared: true, ct);
        var kind = await context.Containers.AsNoTracking()
            .Where(c => c.Id == containerId)
            .Select(c => (ContainerKind?)c.Kind)
            .FirstOrDefaultAsync(ct);
        if (kind is null)
            throw new InvalidOperationException($"Container {containerId} not found.");
        // A documents container holds any number of tables beside its files (CON-319); a memory container holds none.
        if (kind is not ContainerKind.Documents)
            throw new InvalidOperationException($"Container {containerId} can't hold tables.");

        // Two first loads can race to create the schema; the loser's statement fails, so it runs under a savepoint.
        var transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        await transaction.SaveAsync("ensure_schema", ct);
        try
        {
            await TableDdl.CreateSchemaAsync(connection, containerId, ct);
            await transaction.ReleaseAsync("ensure_schema", ct);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DuplicateSchema)
        {
            await transaction.RollbackAsync("ensure_schema", ct);
        }
    }

    private static Task<ContainerTableEntity?> LockEntryAsync(
        KnowledgeDbContext context, Guid containerId, Guid tableId, CancellationToken ct) =>
        context.ContainerTables
            .FromSql($"SELECT * FROM container_tables WHERE id = {tableId} AND container_id = {containerId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

    /// <summary>Creates <paramref name="storageName"/> and binary-copies every row of the load into it.</summary>
    private static async Task<long> BuildTableAsync(
        DbConnection connection, Guid containerId, string storageName, TableLoad load, CancellationToken ct)
    {
        await TableDdl.ExecuteAsync(connection, TableDdl.CreateTableSql(containerId, storageName, load.Columns), ct);

        var npgsql = (NpgsqlConnection)connection;
        var types = load.Columns.Select(c => c.Type).ToArray();
        long rows = 0;
        await using (var writer = await npgsql.BeginBinaryImportAsync(TableDdl.CopySql(containerId, storageName, types.Length), ct))
        {
            await foreach (var row in load.Rows.WithCancellation(ct))
            {
                if (row.Count != types.Length)
                    throw new ArgumentException(
                        $"Row {rows + 1} has {row.Count} values; the table has {types.Length} columns.", nameof(load));
                await writer.StartRowAsync(ct);
                for (int i = 0; i < types.Length; i++)
                {
                    object? value = TableValues.Convert(row[i], types[i], rows + 1, i);
                    if (value is null)
                        await writer.WriteNullAsync(ct);
                    else
                        await writer.WriteAsync(value, TableDdl.CopyType(types[i]), ct);
                }
                rows++;
            }
            await writer.CompleteAsync(ct);
        }

        foreach (string sql in TableDdl.ProtectSql(containerId, storageName, load.DocumentId))
            await TableDdl.ExecuteAsync(connection, sql, ct);
        return rows;
    }

    private static void Validate(TableLoad load)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(load.Columns);
        ArgumentNullException.ThrowIfNull(load.Rows);
        if (string.IsNullOrWhiteSpace(load.Name) || load.Name.Length > MaxNameLength)
            throw new ArgumentException($"A table name must be 1-{MaxNameLength} characters.", nameof(load));
        if (load.Columns.Count is 0 or > TableDdl.MaxColumns)
            throw new ArgumentException($"A table must have 1-{TableDdl.MaxColumns} columns.", nameof(load));
        if (load.Columns.Any(c => c is null || c.Name is null || !Enum.IsDefined(c.Type)))
            throw new ArgumentException("Every column needs a name and a known type.", nameof(load));
    }

    private static void Apply(ContainerTableEntity entity, TableLoad load, string storageName, long rows, DateTime loadedAt)
    {
        entity.Name = load.Name.Trim();
        entity.Description = load.Description;
        entity.DocumentId = load.DocumentId;
        entity.StorageName = storageName;
        entity.SchemaJson = JsonSerializer.Serialize(
            new FrictionlessSchema(load.Columns.Select((c, i) =>
                new FrictionlessField(c.Name, TableValues.FrictionlessType(c.Type), c.Description, TableDdl.ColumnName(i),
                    c.Format?.Pattern, c.Format?.DecimalChar, c.Format?.GroupChar, c.Format?.TrueValues, c.Format?.FalseValues)).ToList()),
            Json);
        entity.RowCount = rows;
        entity.NeedsReview = load.NeedsReview;
        entity.RejectsJson = JsonSerializer.Serialize(load.Rejects ?? [], Json);
        entity.LoadedAt = loadedAt;
    }

    private static TableInfo ToInfo(ContainerTableEntity entity)
    {
        var schema = JsonSerializer.Deserialize<FrictionlessSchema>(entity.SchemaJson, Json)
            ?? new FrictionlessSchema([]);
        var columns = schema.Fields
            .Select(f => new TableColumn(f.Name, TableValues.FromFrictionlessType(f.Type), f.Description,
                f.Format is null && f.DecimalChar is null && f.GroupChar is null && f.TrueValues is null && f.FalseValues is null
                    ? null
                    : new TableColumnFormat(f.Format, f.DecimalChar, f.GroupChar, f.TrueValues, f.FalseValues)))
            .ToList();
        var rejects = JsonSerializer.Deserialize<List<TableReject>>(entity.RejectsJson, Json) ?? [];
        return new TableInfo(
            entity.Id, entity.ContainerId, entity.Name, entity.Description, entity.DocumentId, columns,
            entity.RowCount, entity.NeedsReview, rejects, TableDdl.SchemaName(entity.ContainerId), entity.StorageName,
            entity.Version, entity.CreatedAt, entity.LoadedAt);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A Frictionless Table Schema; <c>column</c> is a Connapse extension naming the field's real column.</summary>
    private sealed record FrictionlessSchema(IReadOnlyList<FrictionlessField> Fields);

    /// <summary>A field, with the Frictionless properties that say how the source file wrote its values.</summary>
    private sealed record FrictionlessField(
        string Name, string Type, string? Description, string Column, string? Format = null, string? DecimalChar = null,
        string? GroupChar = null, IReadOnlyList<string>? TrueValues = null, IReadOnlyList<string>? FalseValues = null);
}

/// <summary>Turns load values into what binary COPY writes for each column type, and names the types for Frictionless.</summary>
internal static class TableValues
{
    internal static string FrictionlessType(TableColumnType type) => type switch
    {
        TableColumnType.Text => "string",
        TableColumnType.WholeNumber => "integer",
        TableColumnType.Number => "number",
        TableColumnType.Boolean => "boolean",
        TableColumnType.Date => "date",
        TableColumnType.DateTime => "datetime",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    internal static TableColumnType FromFrictionlessType(string type) => type switch
    {
        "string" => TableColumnType.Text,
        "integer" => TableColumnType.WholeNumber,
        "number" => TableColumnType.Number,
        "boolean" => TableColumnType.Boolean,
        "date" => TableColumnType.Date,
        "datetime" => TableColumnType.DateTime,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// <paramref name="value"/> as its column's type wants it, or null for an empty cell. A value of the wrong type
    /// fails the load: parsing and rejecting cells is the loader's job, before rows get here.
    /// </summary>
    internal static object? Convert(object? value, TableColumnType type, long row, int column) => (type, value) switch
    {
        (_, null or DBNull) => null,
        (TableColumnType.Text, string s) => s,
        (TableColumnType.WholeNumber, long or int or short or byte or sbyte or ushort or uint) =>
            System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
        (TableColumnType.Number, decimal d) => d,
        (TableColumnType.Number, long or int or short or byte) => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        (TableColumnType.Number, double x) when double.IsFinite(x) => (decimal)x,
        (TableColumnType.Boolean, bool b) => b,
        (TableColumnType.Date, DateOnly d) => d,
        (TableColumnType.Date, DateTime d) => DateOnly.FromDateTime(d),
        (TableColumnType.DateTime, DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Unspecified),
        _ => throw new ArgumentException(
            $"Row {row}, column {column + 1}: a {value.GetType().Name} can't go in a {type} column."),
    };
}
