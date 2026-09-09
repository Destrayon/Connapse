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
/// <remarks>
/// The context is scoped and shared with everything else in the request, so a write that fails
/// must not leave its mutation tracked: the next successful save of any category would carry it
/// along and commit the change that was reported as failed. Every write path clears the tracker
/// on failure before rethrowing.
/// </remarks>
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

        try
        {
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
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }

        // Reload configuration to notify IOptionsMonitor subscribers of the change
        settingsReloader.Reload();
    }

    public async Task<T> UpdateAsync<T>(string category, Func<T?, T> update, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(update);

        T updated;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Make sure there is a row to lock, then lock it: the read and the write below happen
            // with every other updater of this category waiting, in this process or any other.
            DateTime now = DateTime.UtcNow;
            await context.Database.ExecuteSqlAsync(
                $$"""INSERT INTO settings (category, "values", updated_at) VALUES ({{category}}, '{}'::jsonb, {{now}}) ON CONFLICT (category) DO NOTHING""",
                cancellationToken);

            var entity = await context.Settings
                .FromSql($"SELECT * FROM settings WHERE category = {category} FOR UPDATE")
                .AsNoTracking()
                .FirstAsync(cancellationToken);

            string currentJson = entity.Values.RootElement.GetRawText();
            T? current = currentJson == "{}" ? null : JsonSerializer.Deserialize<T>(currentJson, JsonOptions);

            updated = update(current);
            string json = JsonSerializer.Serialize(updated, JsonOptions);
            await context.Database.ExecuteSqlAsync(
                $"""UPDATE settings SET "values" = {json}::jsonb, updated_at = {now} WHERE category = {category}""",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }

        settingsReloader.Reload();
        return updated;
    }

    public async Task ResetAsync(string category, CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = await context.Settings
                .FirstOrDefaultAsync(s => s.Category == category, cancellationToken);

            if (entity is null)
                return;

            context.Settings.Remove(entity);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }

        // Reload configuration to notify IOptionsMonitor subscribers of the change
        settingsReloader.Reload();
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        return await context.Settings
            .AsNoTracking()
            .Select(s => s.Category)
            .ToListAsync(cancellationToken);
    }
}
