using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Storage.Sources;

/// <summary>
/// PostgreSQL-backed source store. A source is a read-only scope inside a
/// connection; its sync cursor is persisted here so incremental sync survives
/// a process restart.
/// </summary>
public class PostgresSourceStore(
    IDbContextFactory<KnowledgeDbContext> factory,
    ILogger<PostgresSourceStore> logger) : ISourceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<Source> CreateAsync(CreateSourceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 128)
            throw new ArgumentException("Source name must be 1-128 characters.", nameof(request));

        await using var context = await factory.CreateDbContextAsync(ct);

        bool connectionExists = await context.Connections.AnyAsync(c => c.Id == request.ConnectionId, ct);
        if (!connectionExists)
            throw new InvalidOperationException($"Connection '{request.ConnectionId}' does not exist.");

        bool nameTaken = await context.Sources.AnyAsync(s => s.Name == name, ct);
        if (nameTaken)
            throw new InvalidOperationException($"A source with the name '{name}' already exists.");

        var entity = new SourceEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = request.Description?.Trim(),
            ConnectionId = request.ConnectionId,
            ScopeJson = JsonDocument.Parse(string.IsNullOrEmpty(request.ScopeJson) ? "{}" : request.ScopeJson),
            SyncIntervalSeconds = request.SyncIntervalSeconds,
            Enabled = true,
            LastSyncStatus = (int)SyncStatus.Never,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Sources.Add(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Created source {SourceId} ({Name})", entity.Id, Sanitize(entity.Name));

        return MapToModel(entity, 0);
    }

    public async Task<Source?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var result = await context.Sources
            .AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { Source = s, DocumentCount = s.Documents.Count })
            .FirstOrDefaultAsync(ct);

        return result is null ? null : MapToModel(result.Source, result.DocumentCount);
    }

    public async Task<Source?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var normalized = name.Trim();

        await using var context = await factory.CreateDbContextAsync(ct);

        var result = await context.Sources
            .AsNoTracking()
            .Where(s => s.Name == normalized)
            .Select(s => new { Source = s, DocumentCount = s.Documents.Count })
            .FirstOrDefaultAsync(ct);

        return result is null ? null : MapToModel(result.Source, result.DocumentCount);
    }

    public async Task<IReadOnlyList<Source>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var results = await context.Sources
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .Skip(skip)
            .Take(take)
            .Select(s => new { Source = s, DocumentCount = s.Documents.Count })
            .ToListAsync(ct);

        return results.Select(r => MapToModel(r.Source, r.DocumentCount)).ToList();
    }

    public async Task<IReadOnlyList<Source>> ListByConnectionAsync(Guid connectionId, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var results = await context.Sources
            .AsNoTracking()
            .Where(s => s.ConnectionId == connectionId)
            .OrderBy(s => s.Name)
            .Select(s => new { Source = s, DocumentCount = s.Documents.Count })
            .ToListAsync(ct);

        return results.Select(r => MapToModel(r.Source, r.DocumentCount)).ToList();
    }

    public async Task<Source?> UpdateAsync(Guid id, UpdateSourceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return null;

        if (!string.IsNullOrWhiteSpace(request.Name))
            entity.Name = request.Name.Trim();

        if (request.Description is not null)
            entity.Description = request.Description.Trim();

        if (request.ScopeJson is not null)
            entity.ScopeJson = JsonDocument.Parse(string.IsNullOrEmpty(request.ScopeJson) ? "{}" : request.ScopeJson);

        if (request.SyncIntervalSeconds.HasValue)
            entity.SyncIntervalSeconds = request.SyncIntervalSeconds;

        if (request.Enabled.HasValue)
            entity.Enabled = request.Enabled.Value;

        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);

        int documentCount = await context.Documents.CountAsync(d => d.SourceId == id, ct);
        return MapToModel(entity, documentCount);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return false;

        context.Sources.Remove(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Deleted source {SourceId}", id);
        return true;
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        return await context.Sources.AnyAsync(s => s.Id == id, ct);
    }

    public async Task UpdateSyncStateAsync(Guid id, string? cursor, SyncStatus status, string? error, DateTime? syncedAt, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return;

        // A null cursor is meaningful, not "no change": it is how a RequiresFullResync
        // response clears a stale delta token so the next cycle re-lists from scratch.
        entity.SyncCursor = cursor;
        entity.LastSyncStatus = (int)status;
        entity.LastSyncError = error;
        entity.LastSyncedAt = syncedAt;
        entity.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);
    }

    public async Task<bool> TryAdvanceSyncStateAsync(
        Guid id, string? expectedCursor, string? newCursor, SyncStatus status, string? error, DateTime? syncedAt, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        // Build the predicate explicitly rather than comparing against a nullable parameter:
        // `s.SyncCursor == expectedCursor` with a null parameter translates to `sync_cursor = NULL`,
        // which is never true in SQL, so a first-ever advance (expected null) would always report
        // a conflict.
        var matching = expectedCursor is null
            ? context.Sources.Where(s => s.Id == id && s.SyncCursor == null)
            : context.Sources.Where(s => s.Id == id && s.SyncCursor == expectedCursor);

        // Single atomic UPDATE ... WHERE — no read-modify-write window for a concurrent sync
        // to slip through.
        int affected = await matching.ExecuteUpdateAsync(setters => setters
            .SetProperty(s => s.SyncCursor, newCursor)
            .SetProperty(s => s.LastSyncStatus, (int)status)
            .SetProperty(s => s.LastSyncError, error)
            .SetProperty(s => s.LastSyncedAt, syncedAt)
            .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), ct);

        if (affected == 0)
        {
            logger.LogWarning(
                "Sync cursor for source {SourceId} was advanced by another run; discarding this result",
                id);
            return false;
        }

        return true;
    }

    public async Task<ContainerSettingsOverrides?> GetSettingsOverridesAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var json = await context.Sources
            .AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => s.SettingsOverridesJson)
            .FirstOrDefaultAsync(ct);

        return json is null
            ? null
            : JsonSerializer.Deserialize<ContainerSettingsOverrides>(json.RootElement.GetRawText(), SerializerOptions);
    }

    public async Task SaveSettingsOverridesAsync(Guid id, ContainerSettingsOverrides overrides, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return;

        entity.SettingsOverridesJson = JsonDocument.Parse(JsonSerializer.Serialize(overrides, SerializerOptions));
        entity.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);
    }

    public async Task UpdateSummaryAsync(Guid id, string? summary, DateTime? generatedAt, string? docSetHash, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return;

        entity.Summary = summary;
        entity.SummaryGeneratedAt = generatedAt;
        entity.SummaryDocSetHash = docSetHash;
        entity.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);
    }

    private static Source MapToModel(SourceEntity entity, int documentCount) => new(
        Id: entity.Id,
        Name: entity.Name,
        Description: entity.Description,
        ConnectionId: entity.ConnectionId,
        ScopeJson: entity.ScopeJson.RootElement.GetRawText(),
        CreatedAt: entity.CreatedAt,
        UpdatedAt: entity.UpdatedAt,
        Enabled: entity.Enabled,
        SyncCursor: entity.SyncCursor,
        LastSyncedAt: entity.LastSyncedAt,
        LastSyncStatus: (SyncStatus)entity.LastSyncStatus,
        LastSyncError: entity.LastSyncError,
        SyncIntervalSeconds: entity.SyncIntervalSeconds,
        SettingsOverrides: entity.SettingsOverridesJson is null
            ? null
            : JsonSerializer.Deserialize<ContainerSettingsOverrides>(entity.SettingsOverridesJson.RootElement.GetRawText(), SerializerOptions),
        Summary: entity.Summary,
        SummaryGeneratedAt: entity.SummaryGeneratedAt,
        SummaryDocSetHash: entity.SummaryDocSetHash,
        DocumentCount: documentCount,
        WithheldDeletions: entity.WithheldDeletions);

    public async Task UpdateWithheldDeletionsAsync(Guid id, int? withheld, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return;

        entity.WithheldDeletions = withheld;
        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
    }
}
