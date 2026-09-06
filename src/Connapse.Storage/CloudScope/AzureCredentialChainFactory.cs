using Azure.Core;
using Azure.Identity;
using Connapse.Core;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Pure selection of Connapse's Azure credential from settings: a configured
/// service-principal certificate first, else the ambient managed identity, else fail closed.
/// Deterministic (an explicit ChainedTokenCredential) — never DefaultAzureCredential.
/// </summary>
public static class AzureCredentialChainFactory
{
    public static TokenCredential Create(
        AzureProviderSettings settings,
        Func<AzureProviderSettings, X509Certificate2?> certLoader)
    {
        var sources = new List<TokenCredential>();

        if (!string.IsNullOrWhiteSpace(settings.ClientId))
        {
            X509Certificate2 cert = certLoader(settings)
                ?? throw new InvalidOperationException(
                    "Azure ClientId is configured but no usable certificate was loaded "
                    + $"(ClientCertificatePath='{settings.ClientCertificatePath}'). "
                    + "Fix the certificate configuration; Connapse will not silently fall back to managed identity.");

            sources.Add(new ClientCertificateCredential(
                settings.TenantId, settings.ClientId, cert,
                new ClientCertificateCredentialOptions { SendCertificateChain = true }));
        }

        sources.Add(string.IsNullOrWhiteSpace(settings.UserAssignedManagedIdentityClientId)
            ? new ManagedIdentityCredential()
            : new ManagedIdentityCredential(
                ManagedIdentityId.FromUserAssignedClientId(settings.UserAssignedManagedIdentityClientId)));

        return new ChainedTokenCredential(sources.ToArray());
    }
}
