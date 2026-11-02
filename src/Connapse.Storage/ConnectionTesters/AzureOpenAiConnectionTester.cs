using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using Azure.AI.OpenAI;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Vectors;
using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to Azure OpenAI embeddings API by generating a test embedding.
/// </summary>
public class AzureOpenAiConnectionTester : IConnectionTester
{
    private readonly ILogger<AzureOpenAiConnectionTester> _logger;
    private readonly PipelineTransport? _transport;

    public AzureOpenAiConnectionTester(ILogger<AzureOpenAiConnectionTester> logger)
        : this(logger, transport: null)
    {
    }

    /// <summary>Sends requests through <paramref name="transport"/>, so tests can answer them.</summary>
    internal AzureOpenAiConnectionTester(ILogger<AzureOpenAiConnectionTester> logger, PipelineTransport? transport)
    {
        _logger = logger;
        _transport = transport;
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

            var options = new AzureOpenAIClientOptions();
            if (_transport is not null)
                options.Transport = _transport;
            var azureClient = new AzureOpenAIClient(
                new Uri(endpoint),
                new ApiKeyCredential(apiKey),
                options);

            var client = azureClient.GetEmbeddingClient(deploymentName);

            _logger.LogDebug(
                "Testing Azure OpenAI embedding connection at {Endpoint} with deployment {Deployment}",
                endpoint, deploymentName);

            // Asks for the size the providers ask for, and refuses the same wrong sizes they refuse (#666).
            var result = await client.GenerateEmbeddingAsync(
                "connection test", EmbeddingDimensions.Options(embeddingSettings), ct);
            var vector = result.Value.ToFloats().ToArray();
            var dimensions = vector.Length;
            EmbeddingDimensions.Check(embeddingSettings, deploymentName, vector);

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
        catch (EmbeddingDimensionMismatchException ex)
        {
            stopwatch.Stop();
            return ConnectionTestResult.CreateFailure(
                ex.Message,
                new Dictionary<string, object>
                {
                    ["deployment"] = ex.ModelId,
                    ["dimensions"] = ex.Actual,
                    ["expectedDimensions"] = ex.Expected
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
