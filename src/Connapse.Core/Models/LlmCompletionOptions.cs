namespace Connapse.Core;

/// <summary>
/// Per-call overrides for LLM completion requests.
/// When null/default, providers read from LlmSettings.
/// </summary>
public record LlmCompletionOptions(
    float? Temperature = null,
    int? MaxTokens = null);
