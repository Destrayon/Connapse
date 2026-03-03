using System.ClientModel;
using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Connapse.Storage.Llm;

/// <summary>
/// LLM provider using Azure OpenAI via the Azure.AI.OpenAI SDK.
/// </summary>
public class AzureOpenAiLlmProvider : ILlmProvider
{
    private readonly ChatClient _client;
    private readonly ILogger<AzureOpenAiLlmProvider> _logger;
    private readonly LlmSettings _settings;

    public AzureOpenAiLlmProvider(
        IOptionsSnapshot<LlmSettings> settings,
        ILogger<AzureOpenAiLlmProvider> logger)
    {
        _logger = logger;
        _settings = settings.Value;

        var endpoint = _settings.AzureEndpoint ?? _settings.BaseUrl;
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException(
                "Azure OpenAI endpoint URL is required. Configure it in Settings > LLM > Endpoint URL.");

        var apiKey = _settings.AzureApiKey ?? _settings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Azure OpenAI API key is required. Configure it in Settings > LLM > API Key.");

        var deploymentName = !string.IsNullOrWhiteSpace(_settings.AzureDeploymentName)
            ? _settings.AzureDeploymentName
            : _settings.Model;

        var credential = new ApiKeyCredential(apiKey);
        var azureClient = new AzureOpenAIClient(new Uri(endpoint), credential);
        _client = azureClient.GetChatClient(deploymentName);
    }

    public string Provider => "AzureOpenAI";
    public string ModelId => !string.IsNullOrWhiteSpace(_settings.AzureDeploymentName)
        ? _settings.AzureDeploymentName
        : _settings.Model;

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
            _logger.LogDebug("Azure OpenAI completion: {TokenCount} chars", text.Length);
            return text;
        }
        catch (ClientResultException ex)
        {
            _logger.LogError(ex, "Azure OpenAI chat completion failed (status {Status})", ex.Status);
            throw new InvalidOperationException(
                $"Azure OpenAI chat completion failed (HTTP {ex.Status}): {ex.Message}. " +
                $"Verify your endpoint, API key, and deployment '{ModelId}' are correct.", ex);
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
            _logger.LogError(ex, "Azure OpenAI streaming failed (status {Status})", ex.Status);
            throw new InvalidOperationException(
                $"Azure OpenAI streaming failed (HTTP {ex.Status}): {ex.Message}.", ex);
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
