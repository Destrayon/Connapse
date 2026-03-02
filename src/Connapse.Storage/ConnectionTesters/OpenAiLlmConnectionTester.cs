using System.ClientModel;
using System.Diagnostics;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to OpenAI (or OpenAI-compatible) LLM endpoints
/// by sending a minimal chat completion request.
/// </summary>
public class OpenAiLlmConnectionTester : IConnectionTester
{
    private readonly ILogger<OpenAiLlmConnectionTester> _logger;

    public OpenAiLlmConnectionTester(ILogger<OpenAiLlmConnectionTester> logger)
    {
        _logger = logger;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (settings is not LlmSettings llmSettings)
            return ConnectionTestResult.CreateFailure("Expected LlmSettings");

        if (string.IsNullOrWhiteSpace(llmSettings.ApiKey))
            return ConnectionTestResult.CreateFailure("API key is required");

        try
        {
            var credential = new ApiKeyCredential(llmSettings.ApiKey);
            ChatClient client;

            if (!string.IsNullOrWhiteSpace(llmSettings.BaseUrl))
            {
                var options = new OpenAIClientOptions { Endpoint = new Uri(llmSettings.BaseUrl) };
                client = new OpenAIClient(credential, options).GetChatClient(llmSettings.Model);
            }
            else
            {
                client = new ChatClient(llmSettings.Model, credential);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

            var chatOptions = new ChatCompletionOptions
            {
                Temperature = 0f,
                MaxOutputTokenCount = 5
            };

            var result = await client.CompleteChatAsync(
                [new UserChatMessage("Say OK")], chatOptions, cts.Token);

            stopwatch.Stop();

            return ConnectionTestResult.CreateSuccess(
                $"Connected to OpenAI LLM (model: {llmSettings.Model})",
                new Dictionary<string, object>
                {
                    ["model"] = llmSettings.Model,
                    ["provider"] = "OpenAI"
                },
                stopwatch.Elapsed);
        }
        catch (ClientResultException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "OpenAI LLM connection test failed (status {Status})", ex.Status);
            return ConnectionTestResult.CreateFailure(
                $"Connection failed (HTTP {ex.Status}): {ex.Message}",
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["status"] = ex.Status
                },
                stopwatch.Elapsed);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ConnectionTestResult.CreateFailure(
                $"Connection timed out after {(timeout ?? TimeSpan.FromSeconds(10)).TotalSeconds:F1}s",
                new Dictionary<string, object> { ["error"] = "Timeout" },
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "OpenAI LLM connection test failed");
            return ConnectionTestResult.CreateFailure(
                $"Unexpected error: {ex.Message}",
                new Dictionary<string, object> { ["error"] = ex.Message },
                stopwatch.Elapsed);
        }
    }
}
