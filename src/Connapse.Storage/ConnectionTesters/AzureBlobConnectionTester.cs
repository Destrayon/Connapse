using Azure;
using Azure.Storage.Blobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Connectors;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>Validates an Azure Blob connection by listing a few blobs with Connapse's identity.</summary>
public sealed class AzureBlobConnectionTester(ConnapseAzureCredentials credentials) : IConnectionTester
{
    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (settings is not AzureBlobConnectorConfig cfg)
            return ConnectionTestResult.CreateFailure(
                "Invalid settings: expected AzureBlobConnectorConfig.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

        try
        {
            var service = new BlobServiceClient(
                new Uri(cfg.BlobEndpoint ?? $"https://{cfg.AccountName}.blob.core.windows.net"),
                credentials);
            var container = service.GetBlobContainerClient(cfg.ContainerName);

            int seen = 0;
            await foreach (var _ in container.GetBlobsAsync(prefix: cfg.Prefix, cancellationToken: cts.Token))
                if (++seen >= 5) break;

            return ConnectionTestResult.CreateSuccess(
                $"Connected to container '{cfg.ContainerName}' on account '{cfg.AccountName}'.");
        }
        catch (RequestFailedException ex) when (ex.Status == 403)
        {
            return ConnectionTestResult.CreateFailure(
                "Authorization failed — Connapse's identity lacks read access to this container "
                + "(needs Storage Blob Data Reader).");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return ConnectionTestResult.CreateFailure(
                $"Container '{cfg.ContainerName}' or account '{cfg.AccountName}' not found.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ConnectionTestResult.CreateFailure("Connection test timed out.");
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.CreateFailure($"Connection test failed: {ex.Message}");
        }
    }
}
