using System.ClientModel;
using System.Diagnostics;
using Azure.AI.OpenAI;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to Azure OpenAI LLM endpoints
/// by sending a minimal chat completion request.
/// </summary>
public class AzureOpenAiLlmConnectionTester : IConnectionTester
{
    private readonly ILogger<AzureOpenAiLlmConnectionTester> _logger;

    public AzureOpenAiLlmConnectionTester(ILogger<AzureOpenAiLlmConnectionTester> logger)
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

        var endpoint = llmSettings.AzureEndpoint ?? llmSettings.BaseUrl;
        if (string.IsNullOrWhiteSpace(endpoint))
            return ConnectionTestResult.CreateFailure("Azure OpenAI endpoint URL is required");

        var apiKey = llmSettings.AzureApiKey ?? llmSettings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return ConnectionTestResult.CreateFailure("API key is required");

        var deploymentName = !string.IsNullOrWhiteSpace(llmSettings.AzureDeploymentName)
            ? llmSettings.AzureDeploymentName
            : llmSettings.Model;

        try
        {
            var credential = new ApiKeyCredential(apiKey);
            var azureClient = new AzureOpenAIClient(new Uri(endpoint), credential);
            var client = azureClient.GetChatClient(deploymentName);

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
                $"Connected to Azure OpenAI LLM (deployment: {deploymentName})",
                new Dictionary<string, object>
                {
                    ["deployment"] = deploymentName,
                    ["endpoint"] = endpoint,
                    ["provider"] = "AzureOpenAI"
                },
                stopwatch.Elapsed);
        }
        catch (ClientResultException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Azure OpenAI LLM connection test failed (status {Status})", ex.Status);
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
            _logger.LogError(ex, "Azure OpenAI LLM connection test failed");
            return ConnectionTestResult.CreateFailure(
                $"Unexpected error: {ex.Message}",
                new Dictionary<string, object> { ["error"] = ex.Message },
                stopwatch.Elapsed);
        }
    }
}
