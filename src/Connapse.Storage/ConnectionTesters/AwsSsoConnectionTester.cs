using System.Diagnostics;
using Amazon;
using Amazon.SSOOIDC;
using Amazon.SSOOIDC.Model;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to an AWS IAM Identity Center instance by calling the
/// SSO OIDC RegisterClient API, which validates the issuer URL and region.
/// </summary>
public class AwsSsoConnectionTester(
    ILogger<AwsSsoConnectionTester> logger) : IConnectionTester
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
            var (issuerUrl, region) = ExtractSettings(settings);

            if (string.IsNullOrWhiteSpace(issuerUrl))
            {
                return ConnectionTestResult.CreateFailure(
                    "Issuer URL is required",
                    new Dictionary<string, object> { ["error"] = "Missing IssuerUrl" });
            }

            if (string.IsNullOrWhiteSpace(region))
            {
                return ConnectionTestResult.CreateFailure(
                    "Region is required",
                    new Dictionary<string, object> { ["error"] = "Missing Region" });
            }

            logger.LogDebug("Testing AWS SSO connection to {IssuerUrl} in {Region}", issuerUrl, region);

            using var oidcClient = new AmazonSSOOIDCClient(new AmazonSSOOIDCConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(region),
                Timeout = timeout
            });

            // RegisterClient with device_code grant validates the issuer URL without
            // requiring redirect URIs. The registered client is ephemeral and unused.
            var response = await oidcClient.RegisterClientAsync(new RegisterClientRequest
            {
                ClientName = "Connapse-ConnectionTest",
                ClientType = "public",
                IssuerUrl = issuerUrl
            }, ct);

            stopwatch.Stop();

            return ConnectionTestResult.CreateSuccess(
                $"Connected to AWS IAM Identity Center ({issuerUrl})",
                new Dictionary<string, object>
                {
                    ["issuerUrl"] = issuerUrl,
                    ["region"] = region,
                    ["hasAuthorizationEndpoint"] = !string.IsNullOrEmpty(response.AuthorizationEndpoint),
                    ["hasTokenEndpoint"] = !string.IsNullOrEmpty(response.TokenEndpoint)
                },
                stopwatch.Elapsed);
        }
        catch (InvalidClientMetadataException ex)
        {
            stopwatch.Stop();
            logger.LogWarning(ex, "AWS SSO connection test failed — invalid issuer URL or region");

            return ConnectionTestResult.CreateFailure(
                "Invalid configuration — verify the issuer URL and region match your IAM Identity Center instance",
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorCode"] = ex.ErrorCode ?? "Unknown"
                },
                stopwatch.Elapsed);
        }
        catch (AmazonSSOOIDCException ex)
        {
            stopwatch.Stop();
            logger.LogWarning(ex, "AWS SSO connection test failed with SSO OIDC error");

            return ConnectionTestResult.CreateFailure(
                $"AWS SSO OIDC error: {ex.Message}",
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorCode"] = ex.ErrorCode ?? "Unknown",
                    ["statusCode"] = (int)ex.StatusCode
                },
                stopwatch.Elapsed);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            logger.LogWarning(ex, "AWS SSO connection test timed out");

            return ConnectionTestResult.CreateFailure(
                $"Connection timed out after {timeout.Value.TotalSeconds:F1}s — check the region and network access",
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
            logger.LogError(ex, "AWS SSO connection test failed with unexpected error");

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

    private static (string? IssuerUrl, string? Region) ExtractSettings(object settings)
    {
        if (settings is AwsSsoSettings sso)
            return (sso.IssuerUrl, sso.Region);

        var type = settings.GetType();
        var issuerUrl = type.GetProperty("IssuerUrl")?.GetValue(settings)?.ToString();
        var region = type.GetProperty("Region")?.GetValue(settings)?.ToString();
        return (issuerUrl, region);
    }
}
