using System.ClientModel;
using System.Runtime.CompilerServices;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Connapse.Storage.Llm;

/// <summary>
/// LLM provider using the OpenAI Chat Completions API via the official .NET SDK.
/// Supports OpenAI-compatible endpoints (Groq, Together, LM Studio, etc.) via BaseUrl override.
/// </summary>
public class OpenAiLlmProvider : ILlmProvider
{
    private readonly ChatClient _client;
    private readonly ILogger<OpenAiLlmProvider> _logger;
    private readonly LlmSettings _settings;

    public OpenAiLlmProvider(
        IOptions<LlmSettings> settings,
        ILogger<OpenAiLlmProvider> logger)
    {
        _logger = logger;
        _settings = settings.Value;

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new InvalidOperationException(
                "OpenAI API key is required. Configure it in Settings > LLM > API Key.");

        var credential = new ApiKeyCredential(_settings.ApiKey);

        if (!string.IsNullOrWhiteSpace(_settings.BaseUrl))
        {
            var options = new OpenAIClientOptions { Endpoint = new Uri(_settings.BaseUrl) };
            var client = new OpenAIClient(credential, options);
            _client = client.GetChatClient(_settings.Model);
        }
        else
        {
            _client = new ChatClient(_settings.Model, credential);
        }
    }

    public string Provider => "OpenAI";
    public string ModelId => _settings.Model;

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        LlmCompletionOptions? options = null,
        CancellationToken ct = default)
    {
        var messages = BuildMessages(systemPrompt, userPrompt);
        var chatOptions = BuildOptions(options);

        try
        {
            ClientResult<ChatCompletion> result =
                await _client.CompleteChatAsync(messages, chatOptions, ct);

            var text = result.Value.Content[0].Text;
            _logger.LogDebug("OpenAI completion: {TokenCount} chars", text.Length);
            return text;
        }
        catch (ClientResultException ex)
        {
            _logger.LogError(ex, "OpenAI chat completion failed (status {Status})", ex.Status);
            throw new InvalidOperationException(
                $"OpenAI chat completion failed (HTTP {ex.Status}): {ex.Message}. " +
                $"Verify your API key and model '{_settings.Model}' are correct.", ex);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        string userPrompt,
        LlmCompletionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = BuildMessages(systemPrompt, userPrompt);
        var chatOptions = BuildOptions(options);

        AsyncCollectionResult<StreamingChatCompletionUpdate> updates;
        try
        {
            updates = _client.CompleteChatStreamingAsync(messages, chatOptions, ct);
        }
        catch (ClientResultException ex)
        {
            _logger.LogError(ex, "OpenAI streaming failed (status {Status})", ex.Status);
            throw new InvalidOperationException(
                $"OpenAI streaming failed (HTTP {ex.Status}): {ex.Message}.", ex);
        }

        await foreach (var update in updates)
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                    yield return part.Text;
            }
        }
    }

    private List<ChatMessage> BuildMessages(string systemPrompt, string userPrompt)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new SystemChatMessage(systemPrompt));

        messages.Add(new UserChatMessage(userPrompt));
        return messages;
    }

    private ChatCompletionOptions BuildOptions(LlmCompletionOptions? options)
    {
        return new ChatCompletionOptions
        {
            Temperature = options?.Temperature ?? (float)_settings.Temperature,
            MaxOutputTokenCount = options?.MaxTokens ?? _settings.MaxTokens
        };
    }
}
