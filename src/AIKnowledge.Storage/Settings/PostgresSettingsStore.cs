using System.Text.Json;
using AIKnowledge.Core.Interfaces;
using AIKnowledge.Storage.Data;
using AIKnowledge.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AIKnowledge.Storage.Settings;

/// <summary>
/// PostgreSQL-backed settings store using JSONB for flexible schema.
/// Settings are stored by category and can be updated at runtime.
/// </summary>
public class PostgresSettingsStore(KnowledgeDbContext context) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task<T?> GetAsync<T>(string category, CancellationToken cancellationToken = default) where T : class
    {
        var entity = await context.Settings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Category == category, cancellationToken);

        if (entity is null)
            return null;

        // Convert Dictionary<string, object> to T via JSON round-trip
        var json = JsonSerializer.Serialize(entity.Values, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public async Task SaveAsync<T>(string category, T settings, CancellationToken cancellationToken = default) where T : class
    {
        // Convert T to Dictionary<string, object> via JSON round-trip
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var values = JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions)
            ?? new Dictionary<string, object>();

        var entity = await context.Settings
            .FirstOrDefaultAsync(s => s.Category == category, cancellationToken);

        if (entity is null)
        {
            // Create new entry
            entity = new SettingEntity
            {
                Category = category,
                Values = values,
                UpdatedAt = DateTime.UtcNow
            };
            context.Settings.Add(entity);
        }
        else
        {
            // Update existing entry
            entity.Values = values;
            entity.UpdatedAt = DateTime.UtcNow;
            context.Settings.Update(entity);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ResetAsync(string category, CancellationToken cancellationToken = default)
    {
        var entity = await context.Settings
            .FirstOrDefaultAsync(s => s.Category == category, cancellationToken);

        if (entity is not null)
        {
            context.Settings.Remove(entity);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        return await context.Settings
            .AsNoTracking()
            .Select(s => s.Category)
            .ToListAsync(cancellationToken);
    }
}
