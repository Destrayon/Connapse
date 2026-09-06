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
        bool anyServicePrincipalFieldSet =
            !string.IsNullOrWhiteSpace(settings.TenantId)
            || !string.IsNullOrWhiteSpace(settings.ClientId)
            || !string.IsNullOrWhiteSpace(settings.ClientCertificatePath)
            || !string.IsNullOrWhiteSpace(settings.ClientCertificatePassword);

        if (!anyServicePrincipalFieldSet)
        {
            // No service-principal intent at all: managed-identity-only chain.
            TokenCredential managedIdentity = string.IsNullOrWhiteSpace(settings.UserAssignedManagedIdentityClientId)
                ? new ManagedIdentityCredential()
                : new ManagedIdentityCredential(
                    ManagedIdentityId.FromUserAssignedClientId(settings.UserAssignedManagedIdentityClientId));

            return new ChainedTokenCredential(managedIdentity);
        }

        // Any populated service-principal field is intent to use certificate auth.
        // Require a complete, usable set — never fall through to managed identity
        // (a broader, ambient identity) on a partial or broken configuration.
        if (string.IsNullOrWhiteSpace(settings.TenantId) || string.IsNullOrWhiteSpace(settings.ClientId))
        {
            throw new InvalidOperationException(
                "Azure service-principal fields are partially configured (some of TenantId, ClientId, "
                + "ClientCertificatePath, ClientCertificatePassword are set) but TenantId and ClientId are "
                + "both required. Fix the certificate configuration; Connapse will not silently fall back "
                + "to managed identity.");
        }

        X509Certificate2 cert = certLoader(settings)
            ?? throw new InvalidOperationException(
                "Azure ClientId is configured but no usable certificate was loaded "
                + $"(ClientCertificatePath='{settings.ClientCertificatePath}'). "
                + "Fix the certificate configuration; Connapse will not silently fall back to managed identity.");

        return new ChainedTokenCredential(new ClientCertificateCredential(
            settings.TenantId, settings.ClientId, cert,
            new ClientCertificateCredentialOptions { SendCertificateChain = true }));
    }
}
