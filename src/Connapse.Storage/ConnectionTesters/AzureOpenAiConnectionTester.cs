using System.ClientModel;
using System.Diagnostics;
using Azure.AI.OpenAI;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to Azure OpenAI embeddings API by generating a test embedding.
/// </summary>
public class AzureOpenAiConnectionTester : IConnectionTester
{
    private readonly ILogger<AzureOpenAiConnectionTester> _logger;

    public AzureOpenAiConnectionTester(ILogger<AzureOpenAiConnectionTester> logger)
    {
        _logger = logger;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var embeddingSettings = settings as EmbeddingSettings;
            if (embeddingSettings == null)
                return ConnectionTestResult.CreateFailure("Invalid settings type");

            var endpoint = embeddingSettings.AzureEndpoint ?? embeddingSettings.BaseUrl;
            if (string.IsNullOrWhiteSpace(endpoint))
                return ConnectionTestResult.CreateFailure(
                    "Azure OpenAI endpoint URL is required (e.g., https://your-resource.openai.azure.com)");

            var apiKey = embeddingSettings.AzureApiKey ?? embeddingSettings.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                return ConnectionTestResult.CreateFailure("API Key is required for Azure OpenAI");

            var deploymentName = !string.IsNullOrWhiteSpace(embeddingSettings.AzureDeploymentName)
                ? embeddingSettings.AzureDeploymentName
                : embeddingSettings.Model;

            if (string.IsNullOrWhiteSpace(deploymentName))
                return ConnectionTestResult.CreateFailure("Deployment name is required for Azure OpenAI");

            var azureClient = new AzureOpenAIClient(
                new Uri(endpoint),
                new ApiKeyCredential(apiKey));

            var client = azureClient.GetEmbeddingClient(deploymentName);

            _logger.LogDebug(
                "Testing Azure OpenAI embedding connection at {Endpoint} with deployment {Deployment}",
                endpoint, deploymentName);

            var result = await client.GenerateEmbeddingAsync("connection test", cancellationToken: ct);
            var dimensions = result.Value.ToFloats().Length;

            stopwatch.Stop();

            return ConnectionTestResult.CreateSuccess(
                $"Connected to Azure OpenAI (deployment: {deploymentName}, dimensions: {dimensions})",
                new Dictionary<string, object>
                {
                    ["endpoint"] = endpoint,
                    ["deployment"] = deploymentName,
                    ["dimensions"] = dimensions
                },
                stopwatch.Elapsed);
        }
        catch (ClientResultException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Azure OpenAI connection test failed (status {Status})", ex.Status);

            return ConnectionTestResult.CreateFailure(
                $"Azure OpenAI API error (HTTP {ex.Status}): {ex.Message}",
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["status"] = ex.Status
                },
                stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Azure OpenAI connection test failed");

            return ConnectionTestResult.CreateFailure(
                $"Connection failed: {ex.Message}",
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorType"] = ex.GetType().Name
                },
                stopwatch.Elapsed);
        }
    }
}
