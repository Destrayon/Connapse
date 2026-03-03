namespace Connapse.Core.Interfaces;

/// <summary>
/// Abstraction for LLM text generation providers.
/// Implementations: OllamaLlmProvider, OpenAiLlmProvider, AzureOpenAiLlmProvider, AnthropicLlmProvider.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Provider name matching LlmSettings.Provider (e.g. "Ollama", "OpenAI").</summary>
    string Provider { get; }

    /// <summary>Model identifier from LlmSettings.Model.</summary>
    string ModelId { get; }

    /// <summary>
    /// Non-streaming completion. Returns the full response text.
    /// </summary>
    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        LlmCompletionOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Streaming completion. Yields text tokens as they arrive.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        string userPrompt,
        LlmCompletionOptions? options = null,
        CancellationToken ct = default);
}
