namespace Connapse.Core;

/// <summary>
/// When an address the deployment set by environment variable or command line takes the place of the saved one
/// (#399): a saved address goes stale when the server moves, as a value saved outside Docker does inside compose,
/// and nothing on the Settings page says so. Every other setting keeps its saved value, as the page is where they
/// are tuned at runtime.
/// </summary>
public static class DeploymentAddresses
{
    /// <summary>
    /// Addresses that several providers read, and the provider a deployment's value is meant for. Compose's reranker
    /// address is for its TEI service, and an Azure AI Foundry key must not follow that address there.
    /// </summary>
    private static readonly Dictionary<string, (string ProviderKey, string Provider)> SharedAddresses = new(StringComparer.OrdinalIgnoreCase)
    {
        [KeyOf("embedding", nameof(EmbeddingSettings.BaseUrl))] = (KeyOf("embedding", nameof(EmbeddingSettings.Provider)), "Ollama"),
        [KeyOf("llm", nameof(LlmSettings.BaseUrl))] = (KeyOf("llm", nameof(LlmSettings.Provider)), "Ollama"),
        [KeyOf("search", nameof(SearchSettings.CrossEncoderBaseUrl))] = (KeyOf("search", nameof(SearchSettings.CrossEncoderProvider)), "TEI"),
    };

    /// <summary>The configuration key of a setting saved under <paramref name="category"/>.</summary>
    public static string KeyOf(string category, string property) => $"Knowledge:{category}:{property}";

    /// <summary>
    /// The deployment's value when it takes the place of the saved value of <paramref name="key"/>, otherwise null.
    /// </summary>
    /// <param name="key">The setting's configuration key.</param>
    /// <param name="saved">The saved value of a key: only a provider is asked for.</param>
    /// <param name="deployment">The value of a key set by environment variable or command line, if any.</param>
    public static string? Replacing(string key, Func<string, string?> saved, Func<string, string?> deployment)
    {
        if (!key.StartsWith("Knowledge:", StringComparison.OrdinalIgnoreCase) || !SettingsSecrets.IsAddress(key))
            return null;

        string? address = deployment(key);
        if (string.IsNullOrWhiteSpace(address))
            return null;

        if (SharedAddresses.TryGetValue(key, out (string ProviderKey, string Provider) shared)
            && saved(shared.ProviderKey) is { Length: > 0 } provider
            && !string.Equals(provider, shared.Provider, StringComparison.OrdinalIgnoreCase))
            return null;

        return address;
    }
}
