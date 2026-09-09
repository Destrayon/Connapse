using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Azure.Storage.Blobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Asks Azure what Connapse's own identity can see. Mirrors <see cref="S3Discovery"/>: the same
/// credential every Azure client uses (<see cref="ConnapseAzureCredentials"/>), read-only calls,
/// and a denial reported as advice rather than a fault.
/// </summary>
public sealed class AzureBlobDiscovery(
    ConnapseAzureCredentials credentials,
    IOptionsMonitor<AzureProviderSettings> options,
    ILogger<AzureBlobDiscovery> logger) : IAzureBlobDiscovery
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<AzureProbe<IReadOnlyList<AzureStorageAccountInfo>>> ListStorageAccountsAsync(
        CancellationToken ct = default)
    {
        AzureProviderSettings settings = options.CurrentValue;
        if (!IsConfigured(settings))
            return AzureProbe<IReadOnlyList<AzureStorageAccountInfo>>.NotConfigured(
                "No Azure identity is set up — configure it on the Azure provider page.");
        if (string.IsNullOrWhiteSpace(settings.SubscriptionId))
            return AzureProbe<IReadOnlyList<AzureStorageAccountInfo>>.NotConfigured(
                "The Azure subscription is not set, so there is nowhere to list accounts from.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            var arm = new ArmClient(credentials);
            SubscriptionResource subscription = arm.GetSubscriptionResource(
                SubscriptionResource.CreateResourceIdentifier(settings.SubscriptionId.Trim()));

            var accounts = new List<AzureStorageAccountInfo>();
            await foreach (StorageAccountResource account in subscription.GetStorageAccountsAsync(timeout.Token))
            {
                StorageAccountData data = account.Data;
                Uri? blob = data.PrimaryEndpoints?.BlobUri;
                if (blob is null) continue; // no blob service (e.g. a files-only account)

                accounts.Add(new AzureStorageAccountInfo(
                    data.Name,
                    blob.ToString().TrimEnd('/'),
                    account.Id.ResourceGroupName ?? string.Empty,
                    account.Id.ToString(),
                    data.IsHnsEnabled == true));
            }

            accounts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return AzureProbe<IReadOnlyList<AzureStorageAccountInfo>>.Ok(accounts);
        }
        catch (RequestFailedException ex) when (IsDenial(ex))
        {
            // Expected when the subscription-wide reader role has not been assigned — the form
            // falls back to asking for the account name.
            logger.LogDebug("Listing storage accounts was denied — the identity has no subscription read");
            return AzureProbe<IReadOnlyList<AzureStorageAccountInfo>>.Denied(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Listing storage accounts failed");
            return AzureProbe<IReadOnlyList<AzureStorageAccountInfo>>.Failed(ex.Message);
        }
    }

    public async Task<AzureProbe<IReadOnlyList<string>>> ListContainersAsync(
        string blobEndpoint, CancellationToken ct = default)
    {
        if (!IsConfigured(options.CurrentValue))
            return AzureProbe<IReadOnlyList<string>>.NotConfigured(
                "No Azure identity is set up — configure it on the Azure provider page.");
        if (!Uri.TryCreate(blobEndpoint?.Trim(), UriKind.Absolute, out Uri? endpoint))
            return AzureProbe<IReadOnlyList<string>>.Failed("No storage account chosen.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            var service = new BlobServiceClient(endpoint, credentials);
            var names = new List<string>();
            await foreach (var container in service.GetBlobContainersAsync(cancellationToken: timeout.Token))
                names.Add(container.Name);

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return AzureProbe<IReadOnlyList<string>>.Ok(names);
        }
        catch (RequestFailedException ex) when (IsDenial(ex))
        {
            logger.LogDebug("Listing containers on {Endpoint} was denied", endpoint);
            return AzureProbe<IReadOnlyList<string>>.Denied(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Listing containers on {Endpoint} failed", endpoint);
            return AzureProbe<IReadOnlyList<string>>.Failed(ex.Message);
        }
    }

    /// <summary>The same rule the Providers page uses for "Access is set up".</summary>
    internal static bool IsConfigured(AzureProviderSettings s) =>
        !string.IsNullOrWhiteSpace(s.TenantId)
        && ((!string.IsNullOrWhiteSpace(s.ClientId) && !string.IsNullOrWhiteSpace(s.ClientCertificatePath))
            || !string.IsNullOrWhiteSpace(s.UserAssignedManagedIdentityClientId));

    internal static bool IsDenial(RequestFailedException ex) => ex.Status is 401 or 403;
}
