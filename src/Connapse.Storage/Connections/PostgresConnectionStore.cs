using System.Security.Cryptography;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Storage.Connections;

/// <summary>
/// PostgreSQL-backed connection store. Secrets are encrypted at rest via
/// ASP.NET Core DataProtection and never surface on the Connection read model.
/// </summary>
public class PostgresConnectionStore(
    IDbContextFactory<KnowledgeDbContext> factory,
    IDataProtectionProvider dataProtection,
    ILogger<PostgresConnectionStore> logger,
    CloudScope.AtlassianConnectionPresence? atlassianPresence = null) : IConnectionStore
{
    private IDataProtector Protector => dataProtection.CreateProtector("Connection.v1");

    public async Task<Connection> CreateAsync(CreateConnectionRequest request, Guid? createdByUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 128)
            throw new ArgumentException("Connection name must be 1-128 characters.", nameof(request));

        await using var context = await factory.CreateDbContextAsync(ct);

        bool exists = await context.Connections.AnyAsync(c => c.Name == name, ct);
        if (exists)
            throw new InvalidOperationException($"A connection with the name '{name}' already exists.");

        var entity = new ConnectionEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Provider = (int)request.Provider,
            ConfigJson = string.IsNullOrEmpty(request.ConfigJson) ? null : JsonDocument.Parse(request.ConfigJson),
            SecretProtected = string.IsNullOrEmpty(request.Secret) ? null : Protector.Protect(request.Secret),
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Connections.Add(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Created connection {ConnectionId} ({Name})", entity.Id, Sanitize(entity.Name));

        // Search skips Confluence verification while no Atlassian connection is known; this ends
        // that at once rather than at the next refresh.
        if (request.Provider == ConnectionProvider.Atlassian)
            atlassianPresence?.MarkPresent();

        return MapToModel(entity, 0);
    }

    public async Task<Connection?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var result = await context.Connections
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { Connection = c, SourceCount = c.Sources.Count })
            .FirstOrDefaultAsync(ct);

        return result is null ? null : MapToModel(result.Connection, result.SourceCount);
    }

    public async Task<IReadOnlyList<Connection>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var results = await context.Connections
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Skip(skip)
            .Take(take)
            .Select(c => new { Connection = c, SourceCount = c.Sources.Count })
            .ToListAsync(ct);

        return results.Select(r => MapToModel(r.Connection, r.SourceCount)).ToList();
    }

    public async Task<Connection?> UpdateAsync(Guid id, UpdateConnectionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var context = await factory.CreateDbContextAsync(ct);

        // An update checked against a known version locks the row, so no other save lands between the compare and the write.
        await using var transaction = request.ExpectedUpdatedAt is null ? null : await context.Database.BeginTransactionAsync(ct);
        var entity = request.ExpectedUpdatedAt is null
            ? await context.Connections.FirstOrDefaultAsync(c => c.Id == id, ct)
            : await context.Connections.FromSql($"SELECT * FROM connections WHERE id = {id} FOR UPDATE").FirstOrDefaultAsync(ct);
        if (entity is null) return null;
        if (request.ExpectedUpdatedAt is { } expected && entity.UpdatedAt != expected)
            throw new ConnectionChangedException(id);

        if (!string.IsNullOrWhiteSpace(request.Name))
            entity.Name = request.Name.Trim();

        if (request.ConfigJson is not null)
        {
            // Sources send the connection's token to the address in their scope, so a content-only custom
            // connection's address cannot move while any source reads it.
            string? before = entity.ConfigJson?.RootElement.GetRawText();
            if (entity.Provider == (int)ConnectionProvider.Custom
                && (!Connapse.Core.Utilities.CustomSourceEndpoint.SameAddress(
                        CustomConnectionConfig.BaseUrlOf(before), CustomConnectionConfig.BaseUrlOf(request.ConfigJson))
                    || CustomConnectionConfig.ProviderIdOf(before) != CustomConnectionConfig.ProviderIdOf(request.ConfigJson))
                && await context.Sources.AnyAsync(s => s.ConnectionId == id, ct))
                throw new InvalidOperationException("Sources read this connection's source server, so its address cannot change.");

            entity.ConfigJson = string.IsNullOrEmpty(request.ConfigJson) ? null : JsonDocument.Parse(request.ConfigJson);
        }

        // A null or empty Secret means "leave it alone" — only a real value replaces it.
        // Without this, a settings-form save that omits the password field would wipe it.
        if (!string.IsNullOrEmpty(request.Secret))
            entity.SecretProtected = Protector.Protect(request.Secret);

        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        int sourceCount = await context.Sources.CountAsync(s => s.ConnectionId == id, ct);
        return MapToModel(entity, sourceCount);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return false;

        int sourceCount = await context.Sources.CountAsync(s => s.ConnectionId == id, ct);
        if (sourceCount > 0)
            throw new InvalidOperationException(
                $"Cannot delete this connection: {sourceCount} source(s) still use it. Remove or repoint them first.");

        context.Connections.Remove(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Deleted connection {ConnectionId}", id);
        return true;
    }

    public async Task<string?> GetSecretAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        string? ciphertext = await context.Connections
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => c.SecretProtected)
            .FirstOrDefaultAsync(ct);

        return Unprotect(id, ciphertext);
    }

    public async Task<string?> GetSecretAtAsync(Guid id, DateTime version, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var row = await context.Connections
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.SecretProtected, c.UpdatedAt })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;
        if (row.UpdatedAt != version) throw new ConnectionChangedException(id);
        return Unprotect(id, row.SecretProtected);
    }

    private string? Unprotect(Guid id, string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;

        try
        {
            return Protector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            // A raw CryptographicException here is opaque — it reads as a corrupt payload when
            // the real cause is almost always a lost or unshared DataProtection key ring. Wrap
            // it so callers can tell "re-enter this credential" apart from a transient fault.
            logger.LogError(ex,
                "Failed to decrypt secret for connection {ConnectionId}; the DataProtection key ring is unavailable",
                id);
            throw new ConnectionSecretUnavailableException(id, ex);
        }
    }

    public async Task<ConnectionHealth?> RecordHealthAsync(Guid id, ConnectionHealth health, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(health);

        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return null;

        ConnectionHealth stored = Merge(health, ReadHealth(entity.HealthJson));

        // UpdatedAt is left alone: a check changes nothing an admin configured.
        entity.HealthJson = JsonSerializer.Serialize(stored, HealthJsonOptions);
        await context.SaveChangesAsync(ct);
        return stored;
    }

    public async Task<ConnectionHealth?> RecordHealthAtAsync(
        Guid id, ConnectionHealth health, DateTime version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(health);

        await using var context = await factory.CreateDbContextAsync(ct);

        var row = await context.Connections
            .AsNoTracking()
            .Where(c => c.Id == id && c.UpdatedAt == version)
            .Select(c => new { c.HealthJson })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        ConnectionHealth stored = Merge(health, ReadHealth(row.HealthJson));
        string json = JsonSerializer.Serialize(stored, HealthJsonOptions);

        // The version is checked in the write itself, so a save between the read and here leaves health alone.
        int written = await context.Connections
            .Where(c => c.Id == id && c.UpdatedAt == version)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.HealthJson, json), ct);
        return written == 0 ? null : stored;
    }

    /// <summary>A failure keeps the last success time and confirmed permissions of the check before it.</summary>
    private static ConnectionHealth Merge(ConnectionHealth health, ConnectionHealth? previous) =>
        health.Succeeded
            ? health with { LastSucceededAt = health.CheckedAt }
            : health with
            {
                LastSucceededAt = health.LastSucceededAt ?? previous?.LastSucceededAt,
                ConfirmedScopes = health.ConfirmedScopes ?? previous?.ConfirmedScopes,
            };

    private static readonly JsonSerializerOptions HealthJsonOptions = new(JsonSerializerDefaults.Web);

    private static ConnectionHealth? ReadHealth(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ConnectionHealth>(json, HealthJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Connection MapToModel(ConnectionEntity entity, int sourceCount) => new(
        Id: entity.Id,
        Name: entity.Name,
        Provider: (ConnectionProvider)entity.Provider,
        ConfigJson: entity.ConfigJson?.RootElement.GetRawText(),
        CreatedByUserId: entity.CreatedByUserId,
        CreatedAt: entity.CreatedAt,
        UpdatedAt: entity.UpdatedAt,
        HasSecret: !string.IsNullOrEmpty(entity.SecretProtected),
        SourceCount: sourceCount,
        Health: ReadHealth(entity.HealthJson));
}
