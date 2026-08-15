using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Storage.Backfill;

/// <summary>
/// Migrates legacy non-managed containers into connection + source pairs.
/// Each source reuses its container's GUID, so documents.owner_id — a generated
/// COALESCE(container_id, source_id) — is unchanged by the move. That is why no
/// chunk or vector rows are touched and search results cannot shift.
/// Transactional per container and safe to run repeatedly.
/// </summary>
public class SourceBackfillService(
    IDbContextFactory<KnowledgeDbContext> factory,
    IConnectorConfigMapper mapper,
    ILogger<SourceBackfillService> logger)
{
    /// <summary>
    /// Arbitrary but fixed key for the Postgres session-level advisory lock that serializes
    /// this backfill. Any constant works as long as it is unique within the application.
    /// </summary>
    private const long AdvisoryLockKey = 0x50485F32_4241434BL; // "PH_2BACK"

    public async Task<BackfillReport> RunAsync(CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        // Serialize across replicas. Every instance runs this at startup, and the
        // read-then-create sequence for connections is not concurrency-safe on its own:
        // two replicas migrating different containers that map to the same connection can
        // both see none and race to create it. pg_try_advisory_lock is non-blocking, so a
        // losing replica simply skips — the winner does the work, and the migration is
        // idempotent if anything is left over. The lock is session-scoped and released
        // when this connection returns to the pool.
        bool acquired = await TryAcquireLockAsync(context, ct);
        if (!acquired)
        {
            logger.LogInformation("Another instance is running the container-to-source backfill; skipping");
            return new BackfillReport(0, 0, 0, 0, []);
        }

        try
        {
            return await RunUnderLockAsync(context, ct);
        }
        finally
        {
            await ReleaseLockAsync(context, ct);
        }
    }

    private static async Task<bool> TryAcquireLockAsync(KnowledgeDbContext context, CancellationToken ct)
    {
        var conn = context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
        var p = cmd.CreateParameter();
        p.ParameterName = "key";
        p.Value = AdvisoryLockKey;
        cmd.Parameters.Add(p);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    private static async Task ReleaseLockAsync(KnowledgeDbContext context, CancellationToken ct)
    {
        var conn = context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) return;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_unlock(@key)";
        var p = cmd.CreateParameter();
        p.ParameterName = "key";
        p.Value = AdvisoryLockKey;
        cmd.Parameters.Add(p);

        await cmd.ExecuteScalarAsync(ct);
    }

    private async Task<BackfillReport> RunUnderLockAsync(KnowledgeDbContext context, CancellationToken ct)
    {
        var legacy = await context.Containers
            .AsNoTracking()
            .Where(c => c.ConnectorType != (int)ConnectorType.ManagedStorage)
            .Select(c => new { c.Id, c.Name, c.ConnectorType, c.ConnectorConfig })
            .ToListAsync(ct);

        if (legacy.Count == 0)
            return new BackfillReport(0, 0, 0, 0, []);

        logger.LogInformation("Backfill starting: {Count} legacy container(s) to migrate", legacy.Count);

        int migrated = 0, connectionsCreated = 0, documentsRepointed = 0, foldersDeleted = 0;
        var failures = new List<string>();

        foreach (var row in legacy)
        {
            try
            {
                var (connection, scopeJson) = mapper.Map(
                    (ConnectorType)row.ConnectorType,
                    row.ConnectorConfig?.RootElement.GetRawText(),
                    row.Name);

                // Fresh context per container so one failure cannot poison the next.
                await using var perItem = await factory.CreateDbContextAsync(ct);
                await using var tx = await perItem.Database.BeginTransactionAsync(ct);

                var (connectionId, wasCreated) = await EnsureConnectionAsync(perItem, connection, ct);
                if (wasCreated) connectionsCreated++;

                perItem.Sources.Add(new SourceEntity
                {
                    // Same GUID as the container it replaces — this is load-bearing.
                    Id = row.Id,
                    Name = row.Name,
                    ConnectionId = connectionId,
                    ScopeJson = JsonDocument.Parse(scopeJson),
                    Enabled = true,
                    LastSyncStatus = (int)SyncStatus.Never,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                await perItem.SaveChangesAsync(ct);

                // Repoint documents. owner_id is generated, so it recomputes to the same value.
                documentsRepointed += await perItem.Documents
                    .Where(d => d.ContainerId == row.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.SourceId, row.Id)
                        .SetProperty(d => d.ContainerId, (Guid?)null), ct);

                foldersDeleted += await perItem.Folders
                    .Where(f => f.ContainerId == row.Id)
                    .ExecuteDeleteAsync(ct);

                await perItem.Containers.Where(c => c.Id == row.Id).ExecuteDeleteAsync(ct);

                await tx.CommitAsync(ct);
                migrated++;

                logger.LogInformation("Migrated container {ContainerId} ({Name}) to a source", row.Id, Sanitize(row.Name));
            }
            catch (Exception ex)
            {
                // Record and continue: one malformed config must not block the rest.
                logger.LogError(ex, "Failed to migrate container {ContainerId}", row.Id);
                failures.Add($"{row.Id}: {ex.Message}");
            }
        }

        logger.LogInformation(
            "Backfill complete: {Migrated} migrated, {Connections} connection(s) created, {Docs} document(s) repointed, {Failures} failure(s)",
            migrated, connectionsCreated, documentsRepointed, failures.Count);

        return new BackfillReport(migrated, connectionsCreated, documentsRepointed, foldersDeleted, failures);
    }

    private static async Task<(Guid Id, bool WasCreated)> EnsureConnectionAsync(
        KnowledgeDbContext context, ConnectionIdentity identity, CancellationToken ct)
    {
        // Dedup by the deterministic name, which encodes the credential identity and is
        // backed by the unique index on connections.name. Comparing serialized ConfigJson
        // would be wrong: Postgres jsonb normalizes property order and whitespace, so the
        // stored text does not round-trip to what JsonSerializer emitted.
        Guid existingId = await context.Connections
            .Where(c => c.Name == identity.Name)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (existingId != Guid.Empty) return (existingId, false);

        var entity = new ConnectionEntity
        {
            Id = Guid.NewGuid(),
            Name = identity.Name,
            Provider = (int)identity.Provider,
            ConfigJson = identity.ConfigJson is null ? null : JsonDocument.Parse(identity.ConfigJson),
            // Backfilled connections never carry a secret: S3 uses DefaultAWSCredentials or an
            // assumed role, Azure uses managed identity, Filesystem needs none.
            SecretProtected = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Connections.Add(entity);
        await context.SaveChangesAsync(ct);
        return (entity.Id, true);
    }
}
