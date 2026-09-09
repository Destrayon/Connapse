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
    /// <summary>
    /// Whether <paramref name="settings"/> name a user-assigned managed identity and nothing of a
    /// certificate app. The tenant is allowed alongside it — the provider page records the tenant
    /// for every identity, and a tenant alone is not certificate intent.
    /// </summary>
    public static bool IsUserAssignedManagedIdentity(AzureProviderSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.UserAssignedManagedIdentityClientId)
        && !settings.UseHostManagedIdentity
        && string.IsNullOrWhiteSpace(settings.ClientId)
        && string.IsNullOrWhiteSpace(settings.ClientCertificatePath)
        && string.IsNullOrWhiteSpace(settings.ClientCertificatePassword);

    /// <summary>
    /// Whether <paramref name="settings"/> name the host's own system-assigned managed identity and
    /// nothing of a certificate app. As with the user-assigned case, the tenant may sit beside it.
    /// </summary>
    public static bool IsHostManagedIdentity(AzureProviderSettings settings) =>
        settings.UseHostManagedIdentity
        && string.IsNullOrWhiteSpace(settings.UserAssignedManagedIdentityClientId)
        && string.IsNullOrWhiteSpace(settings.ClientId)
        && string.IsNullOrWhiteSpace(settings.ClientCertificatePath)
        && string.IsNullOrWhiteSpace(settings.ClientCertificatePassword);

    /// <summary>Either kind of managed identity: the host's own, or a user-assigned one by client id.</summary>
    public static bool IsManagedIdentity(AzureProviderSettings settings) =>
        IsHostManagedIdentity(settings) || IsUserAssignedManagedIdentity(settings);

    public static TokenCredential Create(
        AzureProviderSettings settings,
        Func<AzureProviderSettings, X509Certificate2?> certLoader)
    {
        // The host's identity named beside another identity is a mix the form cannot produce;
        // arriving from configuration it is a mistake, and picking either side silently would be
        // choosing an identity nobody asked for.
        if (settings.UseHostManagedIdentity
            && (!string.IsNullOrWhiteSpace(settings.UserAssignedManagedIdentityClientId)
                || !string.IsNullOrWhiteSpace(settings.ClientId)
                || !string.IsNullOrWhiteSpace(settings.ClientCertificatePath)
                || !string.IsNullOrWhiteSpace(settings.ClientCertificatePassword)))
        {
            throw new InvalidOperationException(
                "Azure settings name both the host's managed identity (UseHostManagedIdentity) and another "
                + "identity (a user-assigned managed identity or a certificate app). Keep one; Connapse will not choose.");
        }

        if (IsHostManagedIdentity(settings))
        {
            return new ChainedTokenCredential(new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
        }

        if (IsUserAssignedManagedIdentity(settings))
        {
            return new ChainedTokenCredential(new ManagedIdentityCredential(
                ManagedIdentityId.FromUserAssignedClientId(settings.UserAssignedManagedIdentityClientId)));
        }

        bool anyServicePrincipalFieldSet =
            !string.IsNullOrWhiteSpace(settings.TenantId)
            || !string.IsNullOrWhiteSpace(settings.ClientId)
            || !string.IsNullOrWhiteSpace(settings.ClientCertificatePath)
            || !string.IsNullOrWhiteSpace(settings.ClientCertificatePassword);

        if (!anyServicePrincipalFieldSet)
        {
            // Nothing configured is nothing, not the host's identity. Using the host's identity is
            // an explicit choice (UseHostManagedIdentity); reading it into an empty record would let
            // a reset, or a lost row, quietly continue as whatever identity the host happens to have.
            throw new InvalidOperationException(
                "No Azure identity is configured. Set access up on the Azure provider page: a certificate "
                + "app registration, a user-assigned managed identity, or the host's own managed identity.");
        }

        // A certificate app beside a user-assigned identity is the same kind of mix as the host's
        // identity beside either: nobody asked for a choice, so none is made.
        if (!string.IsNullOrWhiteSpace(settings.UserAssignedManagedIdentityClientId))
        {
            throw new InvalidOperationException(
                "Azure settings name both a certificate app registration and a user-assigned managed "
                + "identity (UserAssignedManagedIdentityClientId). Keep one; Connapse will not choose.");
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
