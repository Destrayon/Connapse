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

    /// <summary>Chunking strategy passed on each upload; null leaves the product's upload default.</summary>
    public string? ChunkingStrategy { get; init; }

    /// <summary>
    /// When set, each query also records each side's top this-many chunks with both scores, in
    /// candidates/&lt;dataset&gt;.jsonl, so fusion settings can be replayed without re-running.
    /// </summary>
    public int? CaptureCandidates { get; init; }

    public string Hash
    {
        get
        {
            StringBuilder canonical = new($"mode={SearchMode}\n");
            // Only when set, so configs without a strategy keep the hashes their runs already carry.
            if (ChunkingStrategy is not null)
                canonical.Append("chunkingStrategy=").Append(ChunkingStrategy).Append('\n');
            if (CaptureCandidates is not null)
                canonical.Append("captureCandidates=").Append(CaptureCandidates).Append('\n');
            foreach ((string key, string value) in Settings.OrderBy(p => p.Key, StringComparer.Ordinal))
                canonical.Append(key).Append('=').Append(value).Append('\n');
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..16];
        }
    }

    /// <summary>
    /// <c>Knowledge:Search</c> settings read only when a query runs, so configs that differ only in
    /// these (and in search mode or candidate capture) can search one index (#667). Anything not
    /// listed — embedding, chunking, upload, <c>VectorIndexMinVectors</c>, a setting added later — is
    /// treated as shaping the index.
    /// </summary>
    public static readonly IReadOnlySet<string> SearchTimeKeys = new HashSet<string>(
        new[]
        {
            "Mode", "TopK", "Reranker", "FusionAlpha", "FusionMethod", "HybridCandidatePool", "VectorIndexEfSearch",
            "KeywordRanker", "Bm25K1", "Bm25B", "MinimumScore", "AutoCut", "CrossEncoderProvider",
            "CrossEncoderModel", "CrossEncoderBaseUrl", "CrossEncoderApiKey", "CrossEncoderTopN",
            "RerankCandidates", "CrossEncoderTimeoutSeconds", "EnableCrossModelSearch",
            "SentenceWindowSubstituteOnSearch",
        }.Select(name => "Knowledge:Search:" + name),
        StringComparer.OrdinalIgnoreCase);

    public static bool IsSearchTime(string key) => SearchTimeKeys.Contains(key);

    /// <summary>The settings applied per search pass; the rest shape the index.</summary>
    public IReadOnlyDictionary<string, string> SearchTimeSettings =>
        Settings.Where(p => IsSearchTime(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Identifies the index this config searches: equal keys mean one index serves both configs.</summary>
    public string IndexKey
    {
        get
        {
            StringBuilder canonical = new();
            canonical.Append("chunkingStrategy=").Append(ChunkingStrategy ?? "").Append('\n');
            foreach ((string key, string value) in Settings.Where(p => !IsSearchTime(p.Key))
                         .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                canonical.Append(key.ToLowerInvariant()).Append('=').Append(value).Append('\n');
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..16];
        }
    }

    /// <summary>This config with its search-time settings and candidate capture removed: what an index is built with.</summary>
    public SystemConfig IndexOnly() =>
        new(Name, SearchMode, Settings.Where(p => !IsSearchTime(p.Key)).ToDictionary(p => p.Key, p => p.Value))
        {
            ChunkingStrategy = ChunkingStrategy,
        };

    public static SystemConfig Load(string evalRoot, string system, string name)
    {
        string path = Path.Combine(evalRoot, "systems", system, name + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"No config '{name}' for system '{system}' (looked for {path}).");
        ConfigFile file = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(path), EvalJson.Options)
            ?? throw new InvalidOperationException($"{path} is empty.");
        return new SystemConfig(name, Enum.Parse<SearchMode>(file.SearchMode, ignoreCase: true),
            file.Settings ?? new Dictionary<string, string>())
        {
            ChunkingStrategy = file.ChunkingStrategy,
            CaptureCandidates = file.CaptureCandidates,
        };
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

    private sealed record ConfigFile(
        string SearchMode, Dictionary<string, string>? Settings, string? ChunkingStrategy = null, int? CaptureCandidates = null);
}
