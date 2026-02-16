using System.Text.Json;
using AIKnowledge.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AIKnowledge.Storage.Settings;

/// <summary>
/// Configuration provider that loads settings from the database.
/// Database settings override appsettings.json values.
/// </summary>
public class DatabaseSettingsProvider : ConfigurationProvider
{
    private readonly Action<DbContextOptionsBuilder> _optionsAction;

    public DatabaseSettingsProvider(Action<DbContextOptionsBuilder> optionsAction)
    {
        _optionsAction = optionsAction;
    }

    public override void Load()
    {
        var builder = new DbContextOptionsBuilder<KnowledgeDbContext>();
        _optionsAction(builder);

        using var context = new KnowledgeDbContext(builder.Options);

        // Ensure database exists (migrations should have run by this point)
        if (!context.Database.CanConnect())
            return;

        try
        {
            var settings = context.Settings.AsNoTracking().ToList();

            Data.Clear();

            foreach (var setting in settings)
            {
                // Flatten JSONB values into configuration keys
                // E.g., category "Embedding" with { "Model": "nomic-embed-text" }
                // becomes "Knowledge:Embedding:Model" = "nomic-embed-text"
                FlattenJsonDocument($"Knowledge:{setting.Category}", setting.Values);
            }
        }
        catch (Exception)
        {
            // Silently ignore errors during load - this can happen if:
            // 1. Database schema not yet created (migrations haven't run)
            // 2. Settings table doesn't exist
            // 3. Database is being initialized
            // The application will fall back to appsettings.json values
            return;
        }
    }

    private void FlattenJsonDocument(string prefix, JsonDocument document)
    {
        FlattenJsonElement(prefix, document.RootElement);
    }

    private void FlattenJsonElement(string prefix, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    FlattenJsonElement($"{prefix}:{property.Name}", property.Value);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    FlattenJsonElement($"{prefix}:{index}", item);
                    index++;
                }
                break;

            case JsonValueKind.String:
                Data[prefix] = element.GetString() ?? string.Empty;
                break;

            case JsonValueKind.Number:
                Data[prefix] = element.GetRawText();
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                Data[prefix] = element.GetBoolean().ToString();
                break;

            case JsonValueKind.Null:
                Data[prefix] = string.Empty;
                break;
        }
    }

    /// <summary>
    /// Reloads settings from the database and triggers change tokens.
    /// Call this after updating settings to propagate changes to IOptionsMonitor.
    /// </summary>
    public void Reload()
    {
        Load();
        OnReload();
    }
}
