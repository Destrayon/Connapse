using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Settings;
using Connapse.Storage.Tables;
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

        var exists = await context.Containers.AnyAsync(c => c.Name == name && c.Audience == MemoryScope.Team, ct);
        if (exists)
            throw new InvalidOperationException($"A container with the name '{name}' already exists.");

        var entity = new ContainerEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = request.Description?.Trim(),
            Kind = request.Kind,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // A tables container's schema is made with its row, so neither exists without the other.
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        context.Containers.Add(entity);
        await context.SaveChangesAsync(ct);
        if (entity.Kind == ContainerKind.Tables)
            await TableDdl.CreateSchemaAsync(context.Database.GetDbConnection(), entity.Id, ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("Created container {ContainerId} ({Name})", entity.Id, Sanitize(entity.Name));

        return MapToModel(entity, 0);
    }

    public Task<Container?> GetAsync(Guid id, CancellationToken ct = default) => GetAsync(id, viewer: null, ct);

    public async Task<Container?> GetAsync(Guid id, MemoryCaller? viewer, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var result = await VisibleTo(context.Containers.AsNoTracking(), viewer)
            .Where(c => c.Id == id)
            .Select(c => new { Container = c, DocumentCount = c.Documents.Count })
            .FirstOrDefaultAsync(ct);

        return result is null ? null : MapToModel(result.Container, result.DocumentCount);
    }

    public async Task<Container?> GetByNameAsync(string name, CancellationToken ct = default) =>
        (await FindByNameAsync(name, viewer: null, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<Container>> FindByNameAsync(string name, MemoryCaller? viewer, CancellationToken ct = default)
    {
        var normalized = name.Trim().ToLowerInvariant();

        await using var context = await factory.CreateDbContextAsync(ct);

        var results = await VisibleTo(context.Containers.AsNoTracking(), viewer)
            .Where(c => c.Name == normalized)
            .OrderBy(c => c.Audience == MemoryScope.Personal)
            .Select(c => new { Container = c, DocumentCount = c.Documents.Count })
            .ToListAsync(ct);

        return results.Select(r => MapToModel(r.Container, r.DocumentCount)).ToList();
    }

    public Task<IReadOnlyList<Container>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default) =>
        ListAsync(skip, take, viewer: null, ct);

    public Task<IReadOnlyList<Container>> ListAsync(int skip, int take, MemoryCaller? viewer, CancellationToken ct = default) =>
        ListOfKindsAsync(skip, take, viewer, kinds: null, ct);

    public Task<IReadOnlyList<Container>> ListAsync(int skip, int take, MemoryCaller? viewer, IReadOnlyList<ContainerKind> kinds, CancellationToken ct = default) =>
        ListOfKindsAsync(skip, take, viewer, kinds, ct);

    private async Task<IReadOnlyList<Container>> ListOfKindsAsync(int skip, int take, MemoryCaller? viewer, IReadOnlyList<ContainerKind>? kinds, CancellationToken ct)
    {
        await using var context = await factory.CreateDbContextAsync(ct);

        var visible = VisibleTo(context.Containers.AsNoTracking(), viewer);
        if (kinds is not null)
        {
            List<ContainerKind> only = [.. kinds];
            visible = visible.Where(c => only.Contains(c.Kind));
        }
        var results = await visible
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Audience == MemoryScope.Personal)
            .Skip(skip)
            .Take(take)
            .Select(c => new { Container = c, DocumentCount = c.Documents.Count })
            .ToListAsync(ct);

        return results.Select(r => MapToModel(r.Container, r.DocumentCount)).ToList();
    }

    public async Task<Container> EnsurePersonalSpaceAsync(
        MemoryCaller owner, string name, string? description, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        (Guid? ownerUser, Guid? ownerAgent) = OwnerOf(owner);
        if (ownerUser is null && ownerAgent is null)
            throw new ArgumentException("A personal space needs a user or an agent to belong to.", nameof(owner));

        name = name.Trim();
        if (!PathUtilities.IsValidContainerName(name))
            throw new ArgumentException($"'{name}' is not a valid space name.", nameof(name));

        await using var context = await factory.CreateDbContextAsync(ct);

        // Insert-or-nothing, so two first saves racing make one space between them, not a unique-index failure.
        DateTime now = DateTime.UtcNow;
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO containers (id, name, description, kind, audience, owner_user_id, owner_agent_id, created_at, updated_at)
            VALUES ({Guid.NewGuid()}, {name}, {description?.Trim()}, 'memory', 'personal', {ownerUser}, {ownerAgent}, {now}, {now})
            ON CONFLICT (owner_user_id, owner_agent_id, name) WHERE audience = 'personal' DO NOTHING
            """, ct);

        var entity = await context.Containers.AsNoTracking()
            .FirstAsync(c => c.Audience == MemoryScope.Personal && c.Name == name
                && c.OwnerUserId == ownerUser && c.OwnerAgentId == ownerAgent, ct);
        return MapToModel(entity, 0);
    }

    /// <summary>
    /// Only the containers <paramref name="viewer"/> may know exist: every team container, and the personal spaces the
    /// viewer owns. With no viewer, team containers only, so a lookup that forgets to say who is asking fails closed.
    /// </summary>
    private static IQueryable<ContainerEntity> VisibleTo(IQueryable<ContainerEntity> containers, MemoryCaller? viewer)
    {
        if (viewer is null)
            return containers.Where(c => c.Audience == MemoryScope.Team);
        (Guid? ownerUser, Guid? ownerAgent) = OwnerOf(viewer);
        if (ownerUser is { } user)
            return containers.Where(c => c.Audience == MemoryScope.Team || c.OwnerUserId == user);
        if (ownerAgent is { } agent)
            return containers.Where(c => c.Audience == MemoryScope.Team || (c.OwnerUserId == null && c.OwnerAgentId == agent));
        return containers.Where(c => c.Audience == MemoryScope.Team);
    }

    /// <summary>The owner a personal space made for <paramref name="caller"/> has: its user, or its agent when it acts for no user.</summary>
    private static (Guid? User, Guid? Agent) OwnerOf(MemoryCaller caller) =>
        caller.UserId is { } user ? (user, null) : (null, caller.AgentId);

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

        // A container's tables go with it in one transaction: its schema is dropped, and its catalog entries
        // cascade from the row.
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        if (entity.Kind is ContainerKind.Tables or ContainerKind.Documents)
            await TableDdl.DropSchemaAsync(context.Database.GetDbConnection(), id, ct);
        context.Containers.Remove(entity);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

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
            if (name != entity.Name && await context.Containers.AnyAsync(
                    c => c.Name == name && c.Id != id && c.Audience == entity.Audience
                        && c.OwnerUserId == entity.OwnerUserId && c.OwnerAgentId == entity.OwnerAgentId, ct))
                throw new InvalidOperationException($"Container '{name}' already exists");
            entity.Name = name;
        }
        if (description is not null)
            entity.Description = description.Length == 0 ? null : description;
        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Updated container {ContainerId} ({Name})", id, Sanitize(entity.Name));
        int documentCount = await context.Documents.CountAsync(d => d.ContainerId == id, ct);
        return MapToModel(entity, documentCount);
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
            overrides,
            entity.Kind,
            entity.Audience)
        {
            OwnerUserId = entity.OwnerUserId,
            OwnerAgentId = entity.OwnerAgentId,
        };
    }
}
