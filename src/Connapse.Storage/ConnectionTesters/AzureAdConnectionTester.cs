using System.Diagnostics;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to an Azure AD tenant by fetching the public OIDC
/// metadata endpoint, which validates the tenant ID and Azure AD availability.
/// </summary>
public class AzureAdConnectionTester(
    IHttpClientFactory httpClientFactory,
    ILogger<AzureAdConnectionTester> logger) : IConnectionTester
{
    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        timeout ??= TimeSpan.FromSeconds(15);

        try
        {
            var (clientId, tenantId) = ExtractSettings(settings);

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                return ConnectionTestResult.CreateFailure(
                    "Tenant ID is required",
                    new Dictionary<string, object> { ["error"] = "Missing TenantId" });
            }

            if (string.IsNullOrWhiteSpace(clientId))
            {
                return ConnectionTestResult.CreateFailure(
                    "Client ID is required",
                    new Dictionary<string, object> { ["error"] = "Missing ClientId" });
            }

            logger.LogDebug("Testing Azure AD connection for tenant {TenantId}", tenantId);

            var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = timeout.Value;

            var metadataUrl = $"https://login.microsoftonline.com/{tenantId}/v2.0/.well-known/openid-configuration";
            var response = await httpClient.GetAsync(metadataUrl, ct);

            if (!response.IsSuccessStatusCode)
            {
                stopwatch.Stop();
                return ConnectionTestResult.CreateFailure(
                    $"Azure AD returned {(int)response.StatusCode} — verify the Tenant ID is correct",
                    new Dictionary<string, object>
                    {
                        ["statusCode"] = (int)response.StatusCode,
                        ["tenantId"] = tenantId
                    },
                    stopwatch.Elapsed);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var metadata = JsonSerializer.Deserialize<JsonElement>(body);
            var issuer = metadata.GetProperty("issuer").GetString();

            stopwatch.Stop();
            return ConnectionTestResult.CreateSuccess(
                $"Connected to Azure AD tenant ({tenantId})",
                new Dictionary<string, object>
                {
                    ["tenantId"] = tenantId,
                    ["clientId"] = clientId,
                    ["issuer"] = issuer ?? "unknown",
                    ["hasTokenEndpoint"] = metadata.TryGetProperty("token_endpoint", out _)
                },
                stopwatch.Elapsed);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ConnectionTestResult.CreateFailure(
                $"Connection timed out after {timeout.Value.TotalSeconds:F1}s",
                new Dictionary<string, object>
                {
                    ["error"] = "Timeout",
                    ["timeoutSeconds"] = timeout.Value.TotalSeconds
                },
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "Azure AD connection test failed");

            return ConnectionTestResult.CreateFailure(
                $"Unexpected error: {ex.Message}",
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorType"] = ex.GetType().Name
                },
                stopwatch.Elapsed);
        }
    }

    private static (string? ClientId, string? TenantId) ExtractSettings(object settings)
    {
        if (settings is AzureAdSettings azureAd)
            return (azureAd.ClientId, azureAd.TenantId);

        var type = settings.GetType();
        var clientId = type.GetProperty("ClientId")?.GetValue(settings)?.ToString();
        var tenantId = type.GetProperty("TenantId")?.GetValue(settings)?.ToString();
        return (clientId, tenantId);
    }
}
