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
    ILogger<PostgresConnectionStore> logger) : IConnectionStore
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

        var entity = await context.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return null;

        if (!string.IsNullOrWhiteSpace(request.Name))
            entity.Name = request.Name.Trim();

        if (request.ConfigJson is not null)
            entity.ConfigJson = string.IsNullOrEmpty(request.ConfigJson) ? null : JsonDocument.Parse(request.ConfigJson);

        // A null or empty Secret means "leave it alone" — only a real value replaces it.
        // Without this, a settings-form save that omits the password field would wipe it.
        if (!string.IsNullOrEmpty(request.Secret))
            entity.SecretProtected = Protector.Protect(request.Secret);

        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);

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

        return string.IsNullOrEmpty(ciphertext) ? null : Protector.Unprotect(ciphertext);
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
        SourceCount: sourceCount);
}
