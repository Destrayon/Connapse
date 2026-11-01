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

    public async Task<(T? Value, string? Revision)> GetWithRevisionAsync<T>(string category, CancellationToken cancellationToken = default) where T : class
    {
        // xmin is the id of the transaction that last wrote the row, so it changes on every write; read together
        // with the values, in one statement, so the two always belong to the same write.
        var row = await context.Database
            .SqlQuery<RevisionedRow>($"""SELECT "values"::text AS "Values", xmin::text AS "Revision" FROM settings WHERE category = {category}""")
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? (null, null) : (JsonSerializer.Deserialize<T>(row.Values, JsonOptions), row.Revision);
    }

    public async Task<T> UpdateAsync<T>(string category, Func<T?, T> update, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(update);
        return (await UpdateWithRevisionAsync<T>(category, (current, _) => update(current), cancellationToken)).Value;
    }

    public async Task<(T Value, string Revision)> UpdateWithRevisionAsync<T>(string category, Func<T?, string?, T> update, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(update);

        T updated;
        string revision;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Make sure there is a row to lock, then lock it: the read and the write below happen
            // with every other updater of this category waiting, in this process or any other.
            DateTime now = DateTime.UtcNow;
            await context.Database.ExecuteSqlAsync(
                $$"""INSERT INTO settings (category, "values", updated_at) VALUES ({{category}}, '{}'::jsonb, {{now}}) ON CONFLICT (category) DO NOTHING""",
                cancellationToken);

            var row = await context.Database
                .SqlQuery<RevisionedRow>($"""SELECT "values"::text AS "Values", xmin::text AS "Revision" FROM settings WHERE category = {category} FOR UPDATE""")
                .FirstAsync(cancellationToken);

            T? current = row.Values == "{}" ? null : JsonSerializer.Deserialize<T>(row.Values, JsonOptions);

            updated = update(current, current is null ? null : row.Revision);
            string json = JsonSerializer.Serialize(updated, JsonOptions);
            await context.Database.ExecuteSqlAsync(
                $"""UPDATE settings SET "values" = {json}::jsonb, updated_at = {now} WHERE category = {category}""",
                cancellationToken);
            revision = await context.Database
                .SqlQuery<string>($"""SELECT xmin::text AS "Value" FROM settings WHERE category = {category}""")
                .FirstAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }

        settingsReloader.Reload();
        return (updated, revision);
    }

    private sealed class RevisionedRow
    {
        public string Values { get; set; } = "";
        public string Revision { get; set; } = "";
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
