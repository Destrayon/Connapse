using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Settings;

/// <summary>
/// PostgreSQL-backed settings store using JSONB for flexible schema.
/// Settings are stored by category and can be updated at runtime.
/// </summary>
public class PostgresSettingsStore(KnowledgeDbContext context, ISettingsReloader settingsReloader) : ISettingsStore
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

        // Deserialize JsonDocument to T
        var json = entity.Values.RootElement.GetRawText();
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public async Task SaveAsync<T>(string category, T settings, CancellationToken cancellationToken = default) where T : class
    {
        // Serialize T to JsonDocument
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var jsonDocument = JsonDocument.Parse(json);

        var entity = await context.Settings
            .FirstOrDefaultAsync(s => s.Category == category, cancellationToken);

        if (entity is null)
        {
            // Create new entry
            entity = new SettingEntity
            {
                Category = category,
                Values = jsonDocument,
                UpdatedAt = DateTime.UtcNow
            };
            context.Settings.Add(entity);
        }
        else
        {
            // Update existing entry
            entity.Values = jsonDocument;
            entity.UpdatedAt = DateTime.UtcNow;
            context.Settings.Update(entity);
        }

        await context.SaveChangesAsync(cancellationToken);

        // Reload configuration to notify IOptionsMonitor subscribers of the change
        settingsReloader.Reload();
    }

    public async Task ResetAsync(string category, CancellationToken cancellationToken = default)
    {
        var entity = await context.Settings
            .FirstOrDefaultAsync(s => s.Category == category, cancellationToken);

        if (entity is not null)
        {
            context.Settings.Remove(entity);
            await context.SaveChangesAsync(cancellationToken);

            // Reload configuration to notify IOptionsMonitor subscribers of the change
            settingsReloader.Reload();
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
