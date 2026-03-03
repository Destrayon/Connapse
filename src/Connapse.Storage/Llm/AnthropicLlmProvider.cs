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

    private MessageCreateParams BuildParams(
        string systemPrompt, string userPrompt, LlmCompletionOptions? options)
    {
        var temperature = options?.Temperature ?? (float)_settings.Temperature;

        var parameters = new MessageCreateParams
        {
            Model = _settings.Model,
            MaxTokens = options?.MaxTokens ?? _settings.MaxTokens,
            Temperature = temperature,
            Messages =
            [
                new() { Role = Role.User, Content = userPrompt }
            ]
        };

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            parameters = parameters with { System = systemPrompt };
        }

        return parameters;
    }

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
