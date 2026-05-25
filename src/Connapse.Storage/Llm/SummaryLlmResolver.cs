using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Llm;

/// <summary>
/// Resolves the LLM provider to use for container roll-up summarization, applying
/// any per-container override from <see cref="SummarySettings.LlmProvider"/> before
/// falling back to the global <see cref="LlmSettings.Provider"/> value.
/// </summary>
public sealed class SummaryLlmResolver(
    IOptionsMonitor<LlmSettings> llmOptions,
    IServiceProvider serviceProvider)
{
    /// <summary>
    /// Returns the <see cref="ILlmProvider"/> for the given per-container overrides.
    /// Returns null when no LLM provider is configured (provider is null or empty).
    /// </summary>
    public ILlmProvider? Resolve(SummarySettings? summarySettings)
    {
        LlmSettings globalSettings = llmOptions.CurrentValue;
        string effectiveProvider = summarySettings?.LlmProvider ?? globalSettings.Provider;

        // Attempt to resolve the concrete provider type. If the concrete type is not registered
        // (e.g., in test environments), fall back to the ILlmProvider registration.
        ILlmProvider? resolved = effectiveProvider switch
        {
            "OpenAI" => serviceProvider.GetService<OpenAiLlmProvider>(),
            "AzureOpenAI" => serviceProvider.GetService<AzureOpenAiLlmProvider>(),
            "Anthropic" => serviceProvider.GetService<AnthropicLlmProvider>(),
            "Ollama" => serviceProvider.GetService<OllamaLlmProvider>(),
            _ => null
        };

        return resolved ?? serviceProvider.GetService<ILlmProvider>();
    }
}
