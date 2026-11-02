using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Storage.Containers;

public class PostgresContainerStore(
    IDbContextFactory<KnowledgeDbContext> factory,
    ILogger<PostgresContainerStore> logger) : IContainerStore
{
    public async Task<Container> CreateAsync(CreateContainerRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name.Trim();

        if (!PathUtilities.IsValidContainerName(name))
            throw new ArgumentException(
                "Container name must be 2-128 characters, lowercase alphanumeric and hyphens, cannot start or end with a hyphen.",
                nameof(request));

        await using var context = await factory.CreateDbContextAsync(ct);

        var exists = await context.Containers.AnyAsync(c => c.Name == name, ct);
        if (exists)
            throw new InvalidOperationException($"A container with the name '{name}' already exists.");

        var entity = new ContainerEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = request.Description?.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Containers.Add(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Created container {ContainerId} ({Name})", entity.Id, Sanitize(entity.Name));

        return MapToModel(entity, 0);
    }

    public async Task<Container?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var result = await context.Containers
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { Container = c, DocumentCount = c.Documents.Count })
            .FirstOrDefaultAsync(ct);

        return result is null ? null : MapToModel(result.Container, result.DocumentCount);
    }

    public async Task<Container?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var normalized = name.Trim().ToLowerInvariant();

        await using var context = await factory.CreateDbContextAsync(ct);

        var result = await context.Containers
            .AsNoTracking()
            .Where(c => c.Name == normalized)
            .Select(c => new { Container = c, DocumentCount = c.Documents.Count })
            .FirstOrDefaultAsync(ct);

        return result is null ? null : MapToModel(result.Container, result.DocumentCount);
    }

    public async Task<IReadOnlyList<Container>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var results = await context.Containers
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Skip(skip)
            .Take(take)
            .Select(c => new { Container = c, DocumentCount = c.Documents.Count })
            .ToListAsync(ct);

        return results.Select(r => MapToModel(r.Container, r.DocumentCount)).ToList();
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Containers
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (entity is null)
            return false;

        // Every container is managed storage now, so the "just stop watching" exemption that
        // used to apply to Filesystem and S3 rows has nothing left to apply to.
        // Deleting a managed container deletes real bytes, which is why it must be empty first.
        if (entity.Documents.Count > 0)
            throw new InvalidOperationException(
                $"Container '{entity.Name}' is not empty ({entity.Documents.Count} documents). Delete all files first.");

        context.Containers.Remove(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Deleted container {ContainerId} ({Name})", id, Sanitize(entity.Name));
        return true;
    }

    public async Task<Container?> UpdateAsync(Guid id, string? name, string? description, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Containers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null)
            return null;

        if (name is not null)
        {
            name = name.Trim();
            if (!PathUtilities.IsValidContainerName(name))
                throw new InvalidOperationException($"'{name}' is not a valid container name.");
            if (name != entity.Name && await context.Containers.AnyAsync(c => c.Name == name && c.Id != id, ct))
                throw new InvalidOperationException($"Container '{name}' already exists");
            entity.Name = name;
        }
        if (description is not null)
            entity.Description = description.Length == 0 ? null : description;
        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Updated container {ContainerId} ({Name})", id, Sanitize(entity.Name));
        return await GetAsync(id, ct);
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        return await context.Containers.AnyAsync(c => c.Id == id, ct);
    }

    public async Task<ContainerSettingsOverrides?> GetSettingsOverridesAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var doc = await context.Containers
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => c.SettingsOverridesJson)
            .FirstOrDefaultAsync(ct);

        var json = doc?.RootElement.GetRawText();
        return string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<ContainerSettingsOverrides>(json, JsonOptions);
    }

    public Task SaveSettingsOverridesAsync(Guid id, ContainerSettingsOverrides overrides, CancellationToken ct = default) =>
        SaveSettingsOverridesAsync(id, SettingsOverridesJson.Serialize(overrides, JsonOptions), ct);

    public Task SaveSettingsOverridesAsync(
        Guid id, ContainerSettingsOverrides overrides, IEnumerable<string> searchSettingsNamed,
        CancellationToken ct = default) =>
        SaveSettingsOverridesAsync(id, SettingsOverridesJson.Serialize(overrides, JsonOptions, searchSettingsNamed), ct);

    public async Task<JsonElement?> GetSettingsOverridesJsonAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var doc = await context.Containers
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => c.SettingsOverridesJson)
            .FirstOrDefaultAsync(ct);

        return doc?.RootElement.Clone();
    }

    private async Task SaveSettingsOverridesAsync(Guid id, JsonDocument json, CancellationToken ct)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var entity = await context.Containers.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new InvalidOperationException($"Container {id} not found.");

        entity.SettingsOverridesJson = json;
        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
    }

    public Task<ContainerSettingsOverrides?> UpdateSettingsOverridesAsync(
        Guid id, Func<ContainerSettingsOverrides?, ContainerSettingsOverrides> update, CancellationToken ct = default) =>
        UpdateSettingsOverridesCoreAsync(id, update, searchSettingsNamed: null, ct);

    public Task<ContainerSettingsOverrides?> UpdateSettingsOverridesAsync(
        Guid id, Func<ContainerSettingsOverrides?, ContainerSettingsOverrides> update, IEnumerable<string> searchSettingsNamed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(searchSettingsNamed);
        return UpdateSettingsOverridesCoreAsync(id, update, searchSettingsNamed, ct);
    }

    private async Task<ContainerSettingsOverrides?> UpdateSettingsOverridesCoreAsync(
        Guid id, Func<ContainerSettingsOverrides?, ContainerSettingsOverrides> update, IEnumerable<string>? searchSettingsNamed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var context = await factory.CreateDbContextAsync(ct);

        // The row stays locked from the read to the write, so a save made in between can't be overwritten with what was read.
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var entity = await context.Containers.FromSql($"SELECT * FROM containers WHERE id = {id} FOR UPDATE").FirstOrDefaultAsync(ct);
        if (entity is null) return null;

        var json = entity.SettingsOverridesJson?.RootElement.GetRawText();
        var stored = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<ContainerSettingsOverrides>(json, JsonOptions);
        var saved = update(stored);
        entity.SettingsOverridesJson = SettingsOverridesJson.Serialize(saved, JsonOptions, searchSettingsNamed);
        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return saved;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static Container MapToModel(ContainerEntity entity, int documentCount)
    {
        ContainerSettingsOverrides? overrides = null;
        if (entity.SettingsOverridesJson != null)
        {
            try
            {
                var settingsJson = entity.SettingsOverridesJson.RootElement.GetRawText();
                // Without secrets: a container is read by anyone who can see it, and nothing reads its keys from here.
                overrides = JsonSerializer.Deserialize<ContainerSettingsOverrides>(settingsJson, JsonOptions)?.WithoutSecrets();
            }
            catch { /* malformed override JSON — treat as no overrides */ }
        }

        return new(
            entity.Id.ToString(),
            entity.Name,
            entity.Description,
            entity.CreatedAt,
            entity.UpdatedAt,
            documentCount,
            overrides);
    }
}
