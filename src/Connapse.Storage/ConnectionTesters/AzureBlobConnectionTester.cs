using System.Diagnostics;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Connectors;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>Validates an Azure Blob connection by listing a few blobs with Connapse's identity.</summary>
/// <remarks>
/// Every message says which layer failed — reaching the account, Entra accepting Connapse's
/// credential, the role assignment allowing the read, or the container existing — and what to
/// change. The raw reason travels in <c>Details["error"]</c> for the operator who wants it.
/// </remarks>
public sealed class AzureBlobConnectionTester(ConnapseAzureCredentials credentials) : IConnectionTester
{
    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (settings is not AzureBlobConnectorConfig cfg)
            return ConnectionTestResult.CreateFailure(
                "Invalid settings: expected AzureBlobConnectorConfig.");

        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);

        // The same rule the connector applies: the endpoint must be the named account's, or the
        // test would prove access to one account for a connection labelled as another.
        if (Core.Utilities.AzureBlobEndpoint.Validate(cfg.AccountName, cfg.BlobEndpoint, out Uri? endpointUri) is { } endpointProblem)
        {
            return ConnectionTestResult.CreateFailure(
                endpointProblem,
                new Dictionary<string, object> { ["error"] = "Invalid storage account or blob endpoint", ["endpoint"] = cfg.BlobEndpoint ?? "" });
        }
        endpointUri = endpointUri!;

        string where = string.IsNullOrEmpty(cfg.Prefix) ? "" : $" under prefix '{cfg.Prefix}'";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var service = new BlobServiceClient(endpointUri, credentials);
            var container = service.GetBlobContainerClient(cfg.ContainerName);

            int seen = 0;
            bool hasMore = false;
            await foreach (var _ in container.GetBlobsAsync(prefix: cfg.Prefix, cancellationToken: cts.Token))
            {
                if (++seen >= 5) { hasMore = true; break; }
            }

            stopwatch.Stop();
            string what = seen > 0
                ? $"{seen}{(hasMore ? "+" : "")} blob{(seen != 1 ? "s" : "")} found{where}"
                : $"it is empty{where}";

            return ConnectionTestResult.CreateSuccess(
                $"Reached account '{cfg.AccountName}', Entra accepted Connapse's identity, and listed "
                + $"container '{cfg.ContainerName}': {what}.",
                new Dictionary<string, object>
                {
                    ["accountName"] = cfg.AccountName,
                    ["containerName"] = cfg.ContainerName,
                    ["prefix"] = cfg.Prefix ?? "(none)",
                    ["blobsFound"] = seen,
                    ["hasMore"] = hasMore,
                },
                stopwatch.Elapsed);
        }
        catch (AuthenticationFailedException ex) when (AzureBlobDiscovery.IsCredentialRefusal(ex))
        {
            // Entra answered and would not issue a token: the certificate is not registered on the
            // app, has expired, or the app is gone. Nothing on the connection form fixes that.
            stopwatch.Stop();
            return Failure(
                "Entra rejected Connapse's identity, so nothing on this account can be read. Check "
                + "the Access step on the Azure provider page — Recheck there says why.",
                ex, stopwatch.Elapsed);
        }
        catch (CredentialUnavailableException ex)
        {
            stopwatch.Stop();
            return Failure(
                "No managed identity is available on this host, so Connapse has nothing to sign in "
                + "with. Check the Access step on the Azure provider page.",
                ex, stopwatch.Elapsed);
        }
        catch (AuthenticationFailedException ex) when (ex.Message.Contains("AADSTS", StringComparison.Ordinal))
        {
            // Entra answered, but with a code that is not a verdict on the credential — a transient
            // fault, throttling, clock skew. Retrying is the remedy, not re-setup or the network.
            stopwatch.Stop();
            return Failure(
                "Entra answered but did not judge Connapse's credential (a transient fault or throttling), "
                + "so the test is inconclusive. Try again in a moment; the raw reply is below.",
                ex, stopwatch.Elapsed);
        }
        catch (AuthenticationFailedException ex)
        {
            // The sign-in request never got an answer from Entra — a transport fault, not a
            // verdict on the credential.
            stopwatch.Stop();
            return Failure(
                "Could not reach Entra to sign Connapse in, so the credential was not checked. Confirm "
                + "this server can reach login.microsoftonline.com, then try again.",
                ex, stopwatch.Elapsed);
        }
        catch (InvalidOperationException ex)
        {
            // The credential chain refuses to build: the certificate file is missing or the
            // identity is only partly configured.
            stopwatch.Stop();
            return Failure(
                "Connapse's Azure identity is not usable: " + ex.Message
                + " Fix it on the Access step of the Azure provider page.",
                ex, stopwatch.Elapsed);
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            stopwatch.Stop();
            return Failure(
                $"Entra accepted Connapse's identity, but it is not allowed to list container "
                + $"'{cfg.ContainerName}' on account '{cfg.AccountName}'. Assign it the "
                + $"'{Core.Utilities.AzureCloudShellSetup.BlobDataRoleName}' role (or Storage Blob Data Reader) on "
                + "the account or container — the commands under the allowed locations do exactly that. "
                + "A new assignment can take a few minutes to apply.",
                ex, stopwatch.Elapsed);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            stopwatch.Stop();
            return Failure(
                $"Entra accepted Connapse's identity, but no container '{cfg.ContainerName}' exists on "
                + $"account '{cfg.AccountName}'. Check the name — Azure has no default container, so "
                + "the test needs one that exists.",
                ex, stopwatch.Elapsed);
        }
        catch (RequestFailedException ex)
        {
            stopwatch.Stop();
            return Failure(
                $"Azure refused the request ({ex.ErrorCode ?? "unknown code"}): {ex.Message}",
                ex, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ConnectionTestResult.CreateFailure(
                $"Azure did not answer within {limit.TotalSeconds:F0} seconds. Check that this server can "
                + $"reach {endpointUri.Host}, then try again.",
                new Dictionary<string, object>
                {
                    ["error"] = "Timeout",
                    ["timeoutSeconds"] = limit.TotalSeconds,
                },
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(
                $"Could not reach '{endpointUri.Host}': {ex.Message} Check the storage account name "
                + "and, if one is set, the blob endpoint.",
                ex, stopwatch.Elapsed);
        }
    }

    private static ConnectionTestResult Failure(string message, Exception ex, TimeSpan elapsed) =>
        ConnectionTestResult.CreateFailure(
            message,
            new Dictionary<string, object>
            {
                ["error"] = ex.Message,
                ["errorType"] = ex.GetType().Name,
            },
            elapsed);
}
