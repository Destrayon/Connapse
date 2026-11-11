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
using NpgsqlTypes;
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
        var entity = await CreateEntryAsync(context, connection, containerId, load, ct);
        await transaction.CommitAsync(ct);
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

        await ReloadEntryAsync(context, connection, containerId, entity, load, ct);
        await transaction.CommitAsync(ct);
        return ToInfo(entity);
    }

    public async Task<IReadOnlyList<TableInfo>> SyncDocumentTablesAsync(
        Guid containerId, Guid documentId, IReadOnlyList<TableLoad> loads, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loads);
        foreach (var load in loads)
        {
            Validate(load);
            if (load.Placement is null || load.DocumentId != documentId || load.IsMerged)
                throw new ArgumentException("Each table found in a document needs its placement and that document.", nameof(loads));
        }
        if (loads.Select(l => l.Placement!.Number).Distinct().Count() != loads.Count)
            throw new ArgumentException("Each table found in a document needs its own number.", nameof(loads));

        await using var context = await factory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var connection = context.Database.GetDbConnection();

        await PrepareLoadAsync(context, connection, containerId, ct);
        // Locked until commit, so two syncs of one document queue rather than both create its tables.
        var existing = await context.ContainerTables
            .FromSql($"SELECT * FROM container_tables WHERE container_id = {containerId} AND document_id = {documentId} AND header_hash IS NOT NULL ORDER BY table_number, id FOR UPDATE")
            .ToListAsync(ct);

        var matches = TablePlacements.Match(
            loads.Select(l => l.Placement!).ToList(),
            existing.Select(e => new TablePlacement(e.Page, e.TableNumber ?? 0, e.HeaderHash!)).ToList());

        var result = new List<TableInfo>(loads.Count);
        for (int i = 0; i < loads.Count; i++)
        {
            var entity = matches[i] is int m
                ? await ReloadEntryAsync(context, connection, containerId, existing[m], loads[i], ct)
                : await CreateEntryAsync(context, connection, containerId, loads[i], ct);
            result.Add(ToInfo(entity));
        }

        HashSet<int> kept = [.. matches.OfType<int>()];
        var gone = existing.Where((_, n) => !kept.Contains(n)).ToList();
        foreach (var entity in gone)
        {
            context.ContainerTables.Remove(entity);
            context.RetiredTables.Add(new RetiredTableEntity
            {
                ContainerId = containerId,
                StorageName = entity.StorageName,
                RetiredAt = DateTime.UtcNow,
            });
        }
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Synced the tables of document {DocumentId} in container {ContainerId}: {Kept} kept, {Created} created, {Deleted} deleted",
            documentId, containerId, kept.Count, loads.Count - kept.Count, gone.Count);
        return result;
    }

    public async Task<IReadOnlyList<TableInfo>> ListByDocumentAsync(Guid containerId, Guid documentId, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        var entities = await context.ContainerTables.AsNoTracking()
            .Where(t => t.ContainerId == containerId && t.DocumentId == documentId)
            .OrderBy(t => t.TableNumber).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .ToListAsync(ct);
        return entities.Select(ToInfo).ToList();
    }

    public async Task<TableInfo?> SetHiddenAsync(Guid containerId, Guid tableId, bool hidden, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var entity = await LockEntryAsync(context, containerId, tableId, ct);
        if (entity is null)
            return null;

        entity.Hidden = hidden;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("Table {TableId} in container {ContainerId} is now {State}",
            tableId, containerId, hidden ? "hidden" : "shown");
        return ToInfo(entity);
    }

    /// <summary>Loads <paramref name="load"/> into a new table and adds its entry, inside the caller's transaction.</summary>
    private async Task<ContainerTableEntity> CreateEntryAsync(
        KnowledgeDbContext context, DbConnection connection, Guid containerId, TableLoad load, CancellationToken ct)
    {
        DateTime now = await SeenTimeAsync(connection, ct);
        string storageName = TableDdl.NewTableName();
        var candidates = TableHistory.KeyCandidates(load.Columns, load.IsMerged);
        long rows = await BuildTableAsync(connection, containerId, storageName, load, candidates.Count > 0 ? now : null, ct);
        int? key = FirstValid(candidates, await ValidKeysAsync(connection, containerId, storageName, candidates, ct));
        if (key is int k)
            await TableDdl.ExecuteAsync(connection, TableHistory.CurrentKeyIndexSql(containerId, storageName, k), ct);

        var entity = new ContainerTableEntity
        {
            Id = Guid.NewGuid(),
            ContainerId = containerId,
            CreatedAt = now,
        };
        Apply(entity, load, storageName, rows, now, key is int i ? load.Columns[i].Name : null, key is null ? null : now);
        entity.Version = 1;
        context.ContainerTables.Add(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Created table {TableId} ({Name}) in container {ContainerId}: {Rows} rows in {StorageName}",
            entity.Id, Sanitize(entity.Name), containerId, rows, storageName);
        return entity;
    }

    /// <summary>Reloads the locked <paramref name="entity"/> with <paramref name="load"/>, inside the caller's transaction.</summary>
    private async Task<ContainerTableEntity> ReloadEntryAsync(
        KnowledgeDbContext context, DbConnection connection, Guid containerId, ContainerTableEntity entity, TableLoad load,
        CancellationToken ct)
    {
        Guid tableId = entity.Id;

        // One seen time for every version this sync writes, never before the last sync's even if the clock stepped back.
        DateTime now = await SeenTimeAsync(connection, ct);
        DateTime last = DateTime.SpecifyKind(entity.LoadedAt, DateTimeKind.Utc);
        if (now <= last)
            now = last.AddTicks(10);

        var existing = ToInfo(entity);
        string oldName = entity.StorageName;
        string storageName = TableDdl.NewTableName();
        bool merged = load.IsMerged;
        var candidates = TableHistory.KeyCandidates(load.Columns, merged);
        long rows = await BuildTableAsync(connection, containerId, storageName, load, candidates.Count > 0 ? now : null, ct);
        var valid = await ValidKeysAsync(connection, containerId, storageName, candidates, ct);

        // Same columns and the key still holds: close the versions that changed or went away and add the new ones.
        // A table that became merged or stopped being one has a different shape, so it starts again instead.
        int existingKey = existing.HasHistory ? IndexOf(load.Columns, existing.Key!) : -1;
        if (existing.HasHistory && existing.IsMerged == merged && SameColumns(existing.Columns, load.Columns)
            && candidates.Select((c, n) => (c, n)).Any(x => x.c == existingKey && valid[x.n]))
        {
            string current = TableDdl.QualifiedName(containerId, oldName);
            string incoming = TableDdl.QualifiedName(containerId, storageName);
            int closed = await ExecuteAtAsync(connection,
                TableHistory.CloseSql(current, incoming, existingKey, load.Columns.Count, merged), now, ct);
            int added = await ExecuteAtAsync(connection,
                TableHistory.InsertSql(current, incoming, existingKey, load.Columns.Count, merged), now, ct);
            await TableDdl.ExecuteAsync(connection, $"DROP TABLE {incoming}", ct);

            Apply(entity, load, oldName, rows, now, existing.Key, existing.HistoryFrom);
            entity.Version++;
            await context.SaveChangesAsync(ct);

            logger.LogInformation(
                "Re-synced table {TableId} in container {ContainerId} with history: {Rows} rows, {Closed} versions closed, {Added} added",
                tableId, containerId, rows, closed, added);
            return entity;
        }

        // Otherwise the new table replaces the old one, and history, if the new one has a key, starts again here.
        int? key = FirstValid(candidates, valid);
        if (key is int k)
            await TableDdl.ExecuteAsync(connection, TableHistory.CurrentKeyIndexSql(containerId, storageName, k), ct);

        // The swap: the entry now names the new table. Readers resolve the name from here, so none of them waits on a
        // lock or sees the new table before this commits.
        Apply(entity, load, storageName, rows, now, key is int i ? load.Columns[i].Name : null, key is null ? null : now);
        entity.Version++;
        context.RetiredTables.Add(new RetiredTableEntity
        {
            ContainerId = containerId,
            StorageName = oldName,
            RetiredAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Reloaded table {TableId} in container {ContainerId}: {Rows} rows in {StorageName}, replacing {OldName}",
            tableId, containerId, rows, storageName, oldName);
        return entity;
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

    public async Task<IReadOnlyList<TableInfo>> ListByDocumentAsync(Guid containerId, Guid documentId, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        var entities = await context.ContainerTables.AsNoTracking()
            .Where(t => t.ContainerId == containerId && t.DocumentId == documentId)
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .ToListAsync(ct);
        return entities.Select(ToInfo).ToList();
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

    /// <summary>
    /// Creates <paramref name="storageName"/> and binary-copies every row of the load into it. With
    /// <paramref name="seenFrom"/> the table has the history columns and every row is seen from then.
    /// </summary>
    private static async Task<long> BuildTableAsync(
        DbConnection connection, Guid containerId, string storageName, TableLoad load, DateTime? seenFrom, CancellationToken ct)
    {
        bool history = seenFrom is not null;
        bool merged = load.IsMerged;
        await TableDdl.ExecuteAsync(connection, TableDdl.CreateTableSql(containerId, storageName, load.Columns, history, merged), ct);

        var npgsql = (NpgsqlConnection)connection;
        var types = load.Columns.Select(c => c.Type).ToArray();
        HashSet<Guid> sources = merged ? [.. load.SourceDocuments!] : [];
        int width = types.Length + (merged ? 1 : 0);
        long rows = 0;
        await using (var writer = await npgsql.BeginBinaryImportAsync(TableDdl.CopySql(containerId, storageName, types.Length, history, merged), ct))
        {
            await foreach (var row in load.Rows.WithCancellation(ct))
            {
                if (row.Count != width)
                    throw new ArgumentException(merged
                        ? $"Row {rows + 1} has {row.Count} values; a merged table's rows need {types.Length} columns and the source document."
                        : $"Row {rows + 1} has {row.Count} values; the table has {types.Length} columns.", nameof(load));
                await writer.StartRowAsync(ct);
                for (int i = 0; i < types.Length; i++)
                {
                    object? value = TableValues.Convert(row[i], types[i], rows + 1, i);
                    if (value is null)
                        await writer.WriteNullAsync(ct);
                    else
                        await writer.WriteAsync(value, TableDdl.CopyType(types[i]), ct);
                }
                if (merged)
                {
                    // Every row must name one of the table's own documents, or it would be shown by the wrong one's permissions.
                    if (row[types.Length] is not Guid document || !sources.Contains(document))
                        throw new ArgumentException(
                            $"Row {rows + 1} doesn't name one of the table's source documents.", nameof(load));
                    await writer.WriteAsync(document, NpgsqlTypes.NpgsqlDbType.Uuid, ct);
                }
                if (seenFrom is DateTime from)
                    await writer.WriteAsync(from, NpgsqlDbType.TimestampTz, ct);
                rows++;
            }
            await writer.CompleteAsync(ct);
        }

        await TableDdl.EnsureTokenStoreAsync(connection, ct);
        foreach (string sql in TableDdl.ProtectSql(containerId, storageName, load.DocumentId, merged))
            await TableDdl.ExecuteAsync(connection, sql, ct);
        return rows;
    }

    /// <summary>The database's clock, read once the load's locks are held, so syncs of a table get increasing times.</summary>
    private static async Task<DateTime> SeenTimeAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.clock_timestamp()";
        var value = (DateTime)(await command.ExecuteScalarAsync(ct))!;
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    /// <summary>Whether each candidate key column of the new table is unique and never empty.</summary>
    private static async Task<IReadOnlyList<bool>> ValidKeysAsync(
        DbConnection connection, Guid containerId, string storageName, IReadOnlyList<int> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0)
            return [];
        await using var command = connection.CreateCommand();
        command.CommandText = TableHistory.ValidKeysSql(TableDdl.QualifiedName(containerId, storageName), candidates);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Enumerable.Range(0, candidates.Count).Select(reader.GetBoolean).ToList();
    }

    private static int? FirstValid(IReadOnlyList<int> candidates, IReadOnlyList<bool> valid)
    {
        for (int n = 0; n < candidates.Count; n++)
        {
            if (valid[n])
                return candidates[n];
        }
        return null;
    }

    /// <summary>Runs <paramref name="sql"/> with <paramref name="seen"/> as its <c>$1</c>.</summary>
    private static async Task<int> ExecuteAtAsync(DbConnection connection, string sql, DateTime seen, CancellationToken ct)
    {
        await using var command = (NpgsqlCommand)connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter { Value = seen, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static int IndexOf(IReadOnlyList<TableColumn> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static bool SameColumns(IReadOnlyList<TableColumn> a, IReadOnlyList<TableColumn> b) =>
        a.Count == b.Count && a.Zip(b).All(p => string.Equals(p.First.Name, p.Second.Name, StringComparison.Ordinal)
            && p.First.Type == p.Second.Type);

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
        if (load.IsMerged)
        {
            if (load.DocumentId is not null)
                throw new ArgumentException("A merged table takes its documents from its rows, so it has no single document.", nameof(load));
            if (load.SourceDocuments!.Contains(Guid.Empty))
                throw new ArgumentException("A merged table's source documents must be real document ids.", nameof(load));
            if (load.Rejects?.Any(r => r.DocumentId is not Guid d || !load.SourceDocuments!.Contains(d)) == true)
                throw new ArgumentException("Every reject of a merged table must name one of its source documents.", nameof(load));
        }
    }

    public async Task<TableInfo?> SetHiddenColumnsAsync(
        Guid containerId, Guid tableId, IReadOnlyList<string> columns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        await using var context = await factory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var entity = await LockEntryAsync(context, containerId, tableId, ct);
        if (entity is null)
            return null;

        var headers = ToInfo(entity).Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        string[] unknown = columns.Where(c => !headers.Contains(c)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException($"The table has no column named {string.Join(", ", unknown.Select(u => $"'{u}'"))}.", nameof(columns));

        entity.HiddenColumns = columns.Distinct(StringComparer.Ordinal).ToArray();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("Table {TableId} in container {ContainerId} now hides {Count} columns",
            tableId, containerId, entity.HiddenColumns.Length);
        return ToInfo(entity);
    }

    private static void Apply(
        ContainerTableEntity entity, TableLoad load, string storageName, long rows, DateTime loadedAt, string? key, DateTime? historyFrom)
    {
        entity.Name = load.Name.Trim();
        entity.Description = load.Description;
        entity.DocumentId = load.DocumentId;
        // Hidden columns are kept by name: a reload that keeps a header keeps it hidden.
        entity.SourceDocuments = load.IsMerged ? load.SourceDocuments!.Distinct().ToArray() : null;
        entity.StorageName = storageName;
        entity.SchemaJson = JsonSerializer.Serialize(
            new FrictionlessSchema(load.Columns.Select((c, i) =>
                new FrictionlessField(c.Name, TableValues.FrictionlessType(c.Type), c.Description, TableDdl.ColumnName(i),
                    c.Format?.Pattern, c.Format?.DecimalChar, c.Format?.GroupChar, c.Format?.TrueValues, c.Format?.FalseValues)).ToList(),
                key is null ? null : [key],
                historyFrom),
            Json);
        entity.RowCount = rows;
        entity.NeedsReview = load.NeedsReview;
        entity.RejectsJson = JsonSerializer.Serialize(load.Rejects ?? [], Json);
        entity.LoadedAt = loadedAt;
        // The placement is the table's key across re-ingests; an admin's hide of the whole table is kept as it is.
        entity.Page = load.Placement?.Page;
        entity.TableNumber = load.Placement?.Number;
        entity.HeaderHash = load.Placement?.HeaderHash;
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
            entity.Version, entity.CreatedAt, entity.LoadedAt,
            schema.PrimaryKey?.FirstOrDefault(),
            schema.HistoryFrom is DateTime from ? DateTime.SpecifyKind(from, DateTimeKind.Utc) : null,
            entity.SourceDocuments is { Length: > 0 } sources ? sources : null,
            entity.HiddenColumns ?? [],
            entity.HeaderHash is string hash ? new TablePlacement(entity.Page, entity.TableNumber ?? 0, hash) : null,
            entity.Hidden);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// A Frictionless Table Schema; <c>column</c> is a Connapse extension naming the field's real column. A table with
    /// history has a <c>primaryKey</c>, and <c>historyFrom</c>, another extension, is when its current history began.
    /// </summary>
    private sealed record FrictionlessSchema(
        IReadOnlyList<FrictionlessField> Fields, IReadOnlyList<string>? PrimaryKey = null, DateTime? HistoryFrom = null);

    /// <summary>A field, with the Frictionless properties that say how the source file wrote its values.</summary>
    private sealed record FrictionlessField(
        string Name, string Type, string? Description, string Column, string? Format = null, string? DecimalChar = null,
        string? GroupChar = null, IReadOnlyList<string>? TrueValues = null, IReadOnlyList<string>? FalseValues = null);
}

/// <summary>Matches the tables found in a re-ingested document to the ones it had (CON-312).</summary>
internal static class TablePlacements
{
    /// <summary>
    /// For each of <paramref name="incoming"/>, the index into <paramref name="existing"/> of the table it reloads, or
    /// null for a new one. A table on the same page with the same number and header matches first; then one left over
    /// with the same header, in order, so a table that moved, or had another inserted before it, keeps its id and
    /// hides. Each existing table matches at most once.
    /// </summary>
    internal static IReadOnlyList<int?> Match(IReadOnlyList<TablePlacement> incoming, IReadOnlyList<TablePlacement> existing)
    {
        var result = new int?[incoming.Count];
        var used = new bool[existing.Count];
        MatchWhere((a, b) => a == b);
        MatchWhere((a, b) => string.Equals(a.HeaderHash, b.HeaderHash, StringComparison.Ordinal));
        return result;

        void MatchWhere(Func<TablePlacement, TablePlacement, bool> same)
        {
            for (int i = 0; i < incoming.Count; i++)
            {
                if (result[i] is not null)
                    continue;
                for (int e = 0; e < existing.Count; e++)
                {
                    if (!used[e] && same(incoming[i], existing[e]))
                    {
                        result[i] = e;
                        used[e] = true;
                        break;
                    }
                }
            }
        }
    }
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
