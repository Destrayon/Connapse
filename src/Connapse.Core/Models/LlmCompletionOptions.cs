namespace Connapse.Core;

/// <summary>
/// Per-call overrides for LLM completion requests.
/// When null/default, providers read from LlmSettings (constructor-injected).
/// </summary>
public record LlmCompletionOptions(
    float? Temperature = null,
    int? MaxTokens = null,
    string? Model = null);
