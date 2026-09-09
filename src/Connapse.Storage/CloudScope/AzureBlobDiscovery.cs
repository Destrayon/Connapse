using Azure;
using Azure.Core;
using Azure.Identity;
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

    /// <summary>The audience a token is requested for. Azure Resource Manager issues one to any
    /// identity in the tenant, so the check tests the credential and nothing else.</summary>
    private const string ArmScope = "https://management.azure.com/.default";

    public Task<AzureProbe<string>> CheckAccessAsync(CancellationToken ct = default) =>
        CheckAccessAsync(options.CurrentValue, ct);

    public async Task<AzureProbe<string>> CheckAccessAsync(AzureProviderSettings candidate, CancellationToken ct = default)
    {
        if (!IsConfigured(candidate))
            return AzureProbe<string>.NotConfigured(
                "No Azure identity is set up — configure it on the Azure provider page.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            // A credential of its own, never the shared cached one: ClientCertificateCredential
            // hands back a token it already holds without asking Entra, and a certificate removed
            // from the app after that would keep reading as accepted until the token expired.
            TokenCredential fresh = ConnapseAzureCredentials.Build(candidate);
            await fresh.GetTokenAsync(new TokenRequestContext([ArmScope]), timeout.Token);

            string verified = AzureCredentialChainFactory.IsUserAssignedManagedIdentity(candidate)
                ? $"Azure issued a token for managed identity {candidate.UserAssignedManagedIdentityClientId}."
                : $"Entra accepted the certificate for app {candidate.ClientId} in tenant {candidate.TenantId}.";
            return AzureProbe<string>.Ok(verified);
        }
        catch (AuthenticationFailedException ex) when (IsCredentialRefusal(ex))
        {
            // Entra answered and said no: the certificate is not registered on the app, has
            // expired, or the app or tenant no longer exists. Only a new setup fixes it.
            logger.LogWarning("Entra refused Connapse's Azure credential: {Reason}", ex.Message);
            return AzureProbe<string>.Denied(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Network faults, an unreadable certificate file, a partially configured identity, or
            // no managed identity on this host: the check could not be completed either way.
            logger.LogWarning(ex, "Checking Connapse's Azure identity failed");
            return AzureProbe<string>.Failed(Describe(ex));
        }
    }

    /// <summary>What to tell the operator when the check could not run, by what stopped it.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        CredentialUnavailableException => "No managed identity is available on this host: " + ex.Message,
        OperationCanceledException => $"Azure did not answer within {Timeout.TotalSeconds:F0} seconds.",
        _ => ex.Message,
    };

    /// <summary>
    /// Whether Entra rejected the credential itself — something only setting access up again fixes —
    /// as opposed to the request not reaching it or Entra being unable to answer. Only the codes that
    /// mean the certificate, app, or tenant is wrong count; every other <c>AADSTS</c> code (throttling,
    /// a transient fault, directory unavailability) is inconclusive, not a verdict.
    /// </summary>
    internal static bool IsCredentialRefusal(AuthenticationFailedException ex) =>
        ex is not CredentialUnavailableException
        && CredentialRefusalCodes.Any(code => ex.Message.Contains(code, StringComparison.Ordinal));

    /// <summary>Entra error codes that mean the credential is invalid: the certificate is not
    /// registered on the app or its assertion is not signed by a registered key (700027), the app's
    /// keys have expired (7000222), the client credential is wrong (7000215), the app has no service
    /// principal (7000229), the app is disabled (7000112), the app does not exist (700016), or the
    /// tenant does not (90002).</summary>
    internal static readonly IReadOnlyList<string> CredentialRefusalCodes =
    [
        "AADSTS700027", "AADSTS7000222", "AADSTS7000215", "AADSTS7000229", "AADSTS7000112",
        "AADSTS700016", "AADSTS90002",
    ];

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
