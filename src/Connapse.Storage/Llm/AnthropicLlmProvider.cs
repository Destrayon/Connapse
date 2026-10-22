using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Messages;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Llm;

/// <summary>
/// LLM provider using the Anthropic Messages API via the official .NET SDK.
/// </summary>
public class AnthropicLlmProvider : ILlmProvider
{
    private readonly AnthropicClient _client;
    private readonly ILogger<AnthropicLlmProvider> _logger;
    private readonly LlmSettings _settings;

    public AnthropicLlmProvider(
        IOptionsSnapshot<LlmSettings> settings,
        ILogger<AnthropicLlmProvider> logger)
    {
        _logger = logger;
        _settings = settings.Value;

        var apiKey = _settings.AnthropicApiKey ?? _settings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Anthropic API key is required. Configure it in Settings > LLM > API Key.");

        var baseUrl = _settings.AnthropicBaseUrl ?? _settings.BaseUrl;

        if (!string.IsNullOrWhiteSpace(baseUrl) && baseUrl != "http://localhost:11434")
        {
            _client = new AnthropicClient
            {
                ApiKey = apiKey,
                BaseUrl = baseUrl
            };
        }
        else
        {
            _client = new AnthropicClient { ApiKey = apiKey };
        }
    }

    public string Provider => "Anthropic";
    public string ModelId => _settings.Model;

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        LlmCompletionOptions? options = null,
        CancellationToken ct = default)
    {
        var parameters = BuildParams(systemPrompt, userPrompt, options);

        try
        {
            var message = await _client.Messages.Create(parameters, ct);

            var text = ExtractText(message);
            _logger.LogDebug("Anthropic completion: {TokenCount} chars", text.Length);
            return text;
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Anthropic API call failed");
            throw new InvalidOperationException(
                $"Anthropic API call failed: {ex.Message}. " +
                $"Verify your API key and model '{_settings.Model}' are correct.", ex);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        string userPrompt,
        LlmCompletionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parameters = BuildParams(systemPrompt, userPrompt, options);

        var stream = _client.Messages.CreateStreaming(parameters, ct);

        await foreach (var evt in stream)
        {
            if (evt.TryPickContentBlockDelta(out var delta))
            {
                if (delta.Delta.TryPickText(out var textDelta))
                {
                    if (!string.IsNullOrEmpty(textDelta.Text))
                        yield return textDelta.Text;
                }
            }
        }
    }

    internal MessageCreateParams BuildParams(
        string systemPrompt, string userPrompt, LlmCompletionOptions? options)
    {
        var model = options?.Model ?? _settings.Model;

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = options?.MaxTokens ?? _settings.MaxTokens,
            Messages =
            [
                new() { Role = Role.User, Content = userPrompt }
            ]
        };

        if (AcceptsTemperature(model))
        {
            // Obsolete only because newer models reject it; AcceptsTemperature keeps it from them.
#pragma warning disable CS0618
            parameters = parameters with { Temperature = options?.Temperature ?? (float)_settings.Temperature };
#pragma warning restore CS0618
        }

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            parameters = parameters with { System = systemPrompt };
        }

        return parameters;
    }

    // Anthropic's models released after Claude Opus 4.6 reject any temperature but 1.0 with a 400.
    // These families came out before then; a model not listed gets no temperature, so a new one
    // runs at its default rather than failing every call.
    private static readonly string[] TemperatureModelPrefixes =
    [
        "claude-instant", "claude-2", "claude-3",
        "claude-opus-4-0", "claude-sonnet-4-0", "claude-opus-4-2", "claude-sonnet-4-2",
        "claude-opus-4-1", "claude-opus-4-5", "claude-sonnet-4-5", "claude-haiku-4-5", "claude-opus-4-6",
    ];

    /// <summary>Whether the model still accepts a temperature other than 1.0.</summary>
    internal static bool AcceptsTemperature(string model) =>
        TemperatureModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string ExtractText(Message message)
    {
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var textBlock))
                return textBlock.Text;
        }

        return string.Empty;
    }
}
