using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Storage.Containers;

public class PostgresContainerStore(
    KnowledgeDbContext context,
    ILogger<PostgresContainerStore> logger) : IContainerStore
{
    public async Task<Container> CreateAsync(CreateContainerRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name.Trim().ToLowerInvariant();

        if (!PathUtilities.IsValidContainerName(name))
            throw new ArgumentException(
                "Container name must be 2-128 characters, lowercase alphanumeric and hyphens, cannot start or end with a hyphen.",
                nameof(request));

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

        var result = await context.Containers
            .AsNoTracking()
            .Where(c => c.Name == normalized)
            .Select(c => new { Container = c, DocumentCount = c.Documents.Count })
            .FirstOrDefaultAsync(ct);

        return result is null ? null : MapToModel(result.Container, result.DocumentCount);
    }

    public async Task<IReadOnlyList<Container>> ListAsync(CancellationToken ct = default)
    {
        var results = await context.Containers
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new { Container = c, DocumentCount = c.Documents.Count })
            .ToListAsync(ct);

        return results.Select(r => MapToModel(r.Container, r.DocumentCount)).ToList();
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await context.Containers
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (entity is null)
            return false;

        if (entity.Documents.Count > 0)
            throw new InvalidOperationException(
                $"Container '{entity.Name}' is not empty ({entity.Documents.Count} documents). Delete all files first.");

        context.Containers.Remove(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Deleted container {ContainerId} ({Name})", id, entity.Name);
        return true;
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
        => await context.Containers.AnyAsync(c => c.Id == id, ct);

    private static Container MapToModel(ContainerEntity entity, int documentCount)
        => new(
            entity.Id.ToString(),
            entity.Name,
            entity.Description,
            entity.CreatedAt,
            entity.UpdatedAt,
            documentCount);
}
