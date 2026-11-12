using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Connapse.Storage.Tables;

/// <summary>The queries admins saved for containers' tables (CON-311), in <c>table_saved_queries</c>.</summary>
public class PostgresSavedQueryStore(IDbContextFactory<KnowledgeDbContext> factory) : ISavedQueryStore
{
    internal static readonly JsonSerializerOptions ParameterJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public async Task<IReadOnlyList<SavedQuery>> ListAsync(Guid containerId, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        List<TableSavedQueryEntity> entities = await context.TableSavedQueries.AsNoTracking()
            .Where(q => q.ContainerId == containerId)
            .OrderBy(q => q.Name)
            .ToListAsync(ct);
        return entities.Select(ToModel).ToList();
    }

    public async Task<SavedQuery?> GetAsync(Guid containerId, string name, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        TableSavedQueryEntity? entity = await context.TableSavedQueries.AsNoTracking()
            .SingleOrDefaultAsync(q => q.ContainerId == containerId && q.Name == name, ct);
        return entity is null ? null : ToModel(entity);
    }

    public async Task<SavedQuery> SaveAsync(SavedQuery query, CancellationToken ct = default)
    {
        try
        {
            return await UpsertAsync(query, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another save of the same name inserted first; this one replaces it.
            return await UpsertAsync(query, ct);
        }
    }

    public async Task<bool> DeleteAsync(Guid containerId, string name, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        int deleted = await context.TableSavedQueries
            .Where(q => q.ContainerId == containerId && q.Name == name)
            .ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    private async Task<SavedQuery> UpsertAsync(SavedQuery query, CancellationToken ct)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        TableSavedQueryEntity? entity = await context.TableSavedQueries
            .SingleOrDefaultAsync(q => q.ContainerId == query.ContainerId && q.Name == query.Name, ct);
        if (entity is null)
        {
            entity = new TableSavedQueryEntity { Id = Guid.NewGuid(), ContainerId = query.ContainerId, Name = query.Name };
            context.TableSavedQueries.Add(entity);
        }
        SavedQueryDefinition definition = query.Definition;
        entity.Description = definition.Description;
        entity.Kind = Kind(definition.Kind);
        entity.Request = definition.Request;
        entity.ParametersJson = JsonSerializer.Serialize(definition.Parameters, ParameterJson);
        entity.SavedBy = query.SavedBy;
        entity.SavedAt = DateTime.SpecifyKind(query.SavedAt, DateTimeKind.Utc);
        await context.SaveChangesAsync(ct);
        return ToModel(entity);
    }

    private static string Kind(SavedQueryKind kind) => kind == SavedQueryKind.Aggregate ? "aggregate" : "read";

    private static SavedQuery ToModel(TableSavedQueryEntity entity) => new(
        entity.ContainerId,
        new SavedQueryDefinition(
            entity.Name,
            entity.Description,
            entity.Kind == "aggregate" ? SavedQueryKind.Aggregate : SavedQueryKind.Read,
            entity.Request,
            JsonSerializer.Deserialize<List<SavedQueryParameter>>(entity.ParametersJson, ParameterJson) ?? []),
        entity.SavedBy,
        DateTime.SpecifyKind(entity.SavedAt, DateTimeKind.Utc));
}
