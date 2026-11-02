using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Vectors;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Embeddings;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to the OpenAI embeddings API by generating a test embedding.
/// </summary>
public class OpenAiConnectionTester : IConnectionTester
{
    private readonly ILogger<OpenAiConnectionTester> _logger;
    private readonly PipelineTransport? _transport;

    public OpenAiConnectionTester(ILogger<OpenAiConnectionTester> logger)
        : this(logger, transport: null)
    {
    }

    /// <summary>Sends requests through <paramref name="transport"/>, so tests can answer them.</summary>
    internal OpenAiConnectionTester(ILogger<OpenAiConnectionTester> logger, PipelineTransport? transport)
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

            var apiKey = embeddingSettings.OpenAiApiKey ?? embeddingSettings.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                return ConnectionTestResult.CreateFailure("API Key is required for OpenAI");

            var model = embeddingSettings.Model;
            if (string.IsNullOrWhiteSpace(model))
                model = "text-embedding-3-small";

            var credential = new ApiKeyCredential(apiKey);
            var baseUrl = embeddingSettings.OpenAiBaseUrl ?? embeddingSettings.BaseUrl;

            var options = new OpenAIClientOptions();
            if (_transport is not null)
                options.Transport = _transport;
            if (!string.IsNullOrWhiteSpace(baseUrl) && baseUrl != "http://localhost:11434")
                options.Endpoint = new Uri(baseUrl);
            var client = new EmbeddingClient(model, credential, options);

            _logger.LogDebug("Testing OpenAI embedding connection with model {Model}", model);

            // Asks for the size the providers ask for, and refuses the same wrong sizes they refuse (#666).
            var tested = embeddingSettings with { Model = model };
            var result = await client.GenerateEmbeddingAsync("connection test", EmbeddingDimensions.Options(tested), ct);
            var vector = result.Value.ToFloats().ToArray();
            var dimensions = vector.Length;
            EmbeddingDimensions.Check(tested, model, vector);

            stopwatch.Stop();

            return ConnectionTestResult.CreateSuccess(
                $"Connected to OpenAI (model: {model}, dimensions: {dimensions})",
                new Dictionary<string, object>
                {
                    ["model"] = model,
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
                    ["model"] = ex.ModelId,
                    ["dimensions"] = ex.Actual,
                    ["expectedDimensions"] = ex.Expected
                },
                stopwatch.Elapsed);
        }
        catch (ClientResultException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "OpenAI connection test failed (status {Status})", ex.Status);

            return ConnectionTestResult.CreateFailure(
                $"OpenAI API error (HTTP {ex.Status}): {ex.Message}",
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
            _logger.LogError(ex, "OpenAI connection test failed");

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
