using System.Text.Json;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Connapse.Storage.Settings;

/// <summary>
/// Configuration provider that loads settings from the database.
/// Database settings override appsettings.json values.
/// </summary>
public class DatabaseSettingsProvider : ConfigurationProvider
{
    private readonly Action<DbContextOptionsBuilder> _optionsAction;

    /// <summary>One load or reload at a time: see <see cref="Load"/> and <see cref="Reload"/>.</summary>
    private readonly Lock _reloadGate = new();

    /// <summary>
    /// Maps DB category names to their configuration section prefixes.
    /// Categories not listed here default to "Knowledge:{category}".
    /// </summary>
    private static readonly Dictionary<string, string> CategoryPrefixMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["security"] = "Identity:Jwt",
        ["samlsignin"] = "Identity:SamlSignIn",
        ["identitycenter"] = "Identity:IdentityCenter",
        ["permissionenforcement"] = "Identity:PermissionEnforcement",
        ["azure"] = "Providers:Azure",
        ["azuread"] = "Identity:AzureAd",
    };

    public DatabaseSettingsProvider(Action<DbContextOptionsBuilder> optionsAction)
    {
        _optionsAction = optionsAction;
    }

    /// <summary>
    /// Whether the most recent <see cref="Load"/> read the settings table.
    /// </summary>
    /// <remarks>
    /// False after a load that could not connect or whose query failed. Those loads leave the
    /// configuration at whatever it held before — appsettings defaults on the first load — which
    /// is indistinguishable from "nothing stored" to every reader of IOptionsMonitor. Anything that
    /// treats "not configured" as a safe answer must check this first.
    /// </remarks>
    public bool LastLoadSucceeded { get; private set; }

    /// <remarks>
    /// Under the same lock as <see cref="Reload"/>, which it re-enters: the configuration root's own
    /// Reload calls this directly, and a load that read the table before a save must not publish
    /// after it. Loads take turns, so the later one reads the later table.
    /// </remarks>
    public override void Load()
    {
        lock (_reloadGate)
        {
            LoadUnderLock();
        }
    }

    private void LoadUnderLock()
    {
        LastLoadSucceeded = false;

        var builder = new DbContextOptionsBuilder<KnowledgeDbContext>();
        _optionsAction(builder);

        using var context = new KnowledgeDbContext(builder.Options);

        // Ensure database exists (migrations should have run by this point)
        if (!context.Database.CanConnect())
            return;

        try
        {
            var settings = context.Settings.AsNoTracking().ToList();

            // Built aside and swapped in with one assignment (#632): readers -- change-token
            // callbacks of an earlier reload among them -- see the old set or the new one, never
            // one being cleared and refilled under them.
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var setting in settings)
            {
                // Flatten JSONB values into configuration keys
                // E.g., category "Embedding" with { "Model": "nomic-embed-text" }
                // becomes "Knowledge:Embedding:Model" = "nomic-embed-text"
                // Some categories map to non-Knowledge prefixes (e.g. "security" → "Identity:Jwt")
                var prefix = CategoryPrefixMap.TryGetValue(setting.Category, out var mapped)
                    ? mapped
                    : $"Knowledge:{setting.Category}";
                FlattenJsonElement(data, prefix, setting.Values.RootElement);
            }

            Data = data;
            LastLoadSucceeded = true;
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

    private static void FlattenJsonElement(Dictionary<string, string?> data, string prefix, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    FlattenJsonElement(data, $"{prefix}:{property.Name}", property.Value);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    FlattenJsonElement(data, $"{prefix}:{index}", item);
                    index++;
                }
                break;

            case JsonValueKind.String:
                data[prefix] = element.GetString() ?? string.Empty;
                break;

            case JsonValueKind.Number:
                data[prefix] = element.GetRawText();
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                data[prefix] = element.GetBoolean().ToString();
                break;

            case JsonValueKind.Null:
                data[prefix] = string.Empty;
                break;
        }
    }

    /// <summary>
    /// Reloads settings from the database and triggers change tokens.
    /// Call this after updating settings to propagate changes to IOptionsMonitor.
    /// </summary>
    /// <remarks>
    /// Serialised (#632): two settings saved at once each reload, and one reload's change-token
    /// callbacks failed with "Collection was modified" while the other was loading. Holding the
    /// lock across the callbacks also keeps <see cref="LastLoadSucceeded"/> belonging to this load.
    /// </remarks>
    public bool Reload()
    {
        lock (_reloadGate)
        {
            Load();
            OnReload();
            return LastLoadSucceeded;
        }
    }
}
