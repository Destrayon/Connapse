using System.Diagnostics;
using Anthropic;
using Anthropic.Models.Messages;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to the Anthropic API by sending a minimal message.
/// </summary>
public class AnthropicConnectionTester : IConnectionTester
{
    private readonly ILogger<AnthropicConnectionTester> _logger;

    public AnthropicConnectionTester(ILogger<AnthropicConnectionTester> logger)
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
            AnthropicClient client;
            if (!string.IsNullOrWhiteSpace(llmSettings.BaseUrl))
            {
                client = new AnthropicClient
                {
                    ApiKey = llmSettings.ApiKey,
                    BaseUrl = llmSettings.BaseUrl
                };
            }
            else
            {
                client = new AnthropicClient { ApiKey = llmSettings.ApiKey };
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

            var parameters = new MessageCreateParams
            {
                Model = llmSettings.Model,
                MaxTokens = 5,
                Temperature = 0f,
                Messages =
                [
                    new() { Role = Role.User, Content = "Say OK" }
                ]
            };

            var message = await client.Messages.Create(parameters, cts.Token);

            stopwatch.Stop();

            return ConnectionTestResult.CreateSuccess(
                $"Connected to Anthropic API (model: {llmSettings.Model})",
                new Dictionary<string, object>
                {
                    ["model"] = llmSettings.Model,
                    ["provider"] = "Anthropic"
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
            _logger.LogError(ex, "Anthropic connection test failed");
            return ConnectionTestResult.CreateFailure(
                $"Connection failed: {ex.Message}",
                new Dictionary<string, object> { ["error"] = ex.Message },
                stopwatch.Elapsed);
        }
    }
}
