using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Eval.Model;

namespace Connapse.Eval.Systems;

/// <summary>
/// A named configuration: the search mode passed on each query plus Connapse configuration keys
/// (for example "Knowledge:Search:FusionAlpha") applied when the host starts.
/// Loaded from eval/systems/{system}/{name}.json.
/// </summary>
public sealed record SystemConfig(string Name, SearchMode SearchMode, IReadOnlyDictionary<string, string> Settings)
{
    /// <summary>Setting-key prefixes that would let a config redirect the harness's throwaway
    /// infrastructure (database, storage, admin account, JWT secret, rate limits) instead of
    /// only tuning search/embedding/chunking behaviour.</summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "ConnectionStrings:", "Knowledge:Storage:", "Identity:", "CONNAPSE_ADMIN_", "RateLimiting:",
    ];

    public IReadOnlyDictionary<string, string> Settings { get; init; } = Validate(Settings);

    public string Hash
    {
        get
        {
            StringBuilder canonical = new($"mode={SearchMode}\n");
            foreach ((string key, string value) in Settings.OrderBy(p => p.Key, StringComparer.Ordinal))
                canonical.Append(key).Append('=').Append(value).Append('\n');
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..16];
        }
    }

    public static SystemConfig Load(string evalRoot, string system, string name)
    {
        string path = Path.Combine(evalRoot, "systems", system, name + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"No config '{name}' for system '{system}' (looked for {path}).");
        ConfigFile file = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(path), EvalJson.Options)
            ?? throw new InvalidOperationException($"{path} is empty.");
        return new SystemConfig(name, Enum.Parse<SearchMode>(file.SearchMode, ignoreCase: true),
            file.Settings ?? new Dictionary<string, string>());
    }

    private static IReadOnlyDictionary<string, string> Validate(IReadOnlyDictionary<string, string> settings)
    {
        foreach (string key in settings.Keys)
        {
            string? forbidden = ForbiddenPrefixes.FirstOrDefault(
                p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (forbidden is not null)
                throw new ArgumentException(
                    $"Config setting '{key}' is not allowed: configs may only set search/embedding/chunking "
                    + $"behaviour, not infrastructure settings (forbidden prefix '{forbidden}').");
        }
        return settings;
    }

    private sealed record ConfigFile(string SearchMode, Dictionary<string, string>? Settings);
}
