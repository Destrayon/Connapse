namespace Connapse.Core;

/// <summary>
/// Connapse's own Azure app-credential configuration, bound from the settings hierarchy.
/// When ClientId + a certificate are present, Connapse authenticates as that service principal;
/// otherwise it uses the ambient managed identity. Never a client secret.
/// </summary>
public record AzureProviderSettings
{
    public const string SectionName = "Providers:Azure";

    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientCertificatePath { get; init; }
    public string? ClientCertificatePassword { get; init; }
    public string? UserAssignedManagedIdentityClientId { get; init; }

    /// <summary>
    /// Sign in as the system-assigned managed identity of the host Connapse runs on. Explicit,
    /// because "nothing configured" and "use the host's identity" must never look alike: the
    /// fail-closed rules treat the first as not set up, and only the second as a credential.
    /// </summary>
    public bool UseHostManagedIdentity { get; init; }

    /// <summary>The managed identity's service principal object id, recorded by the guided setup
    /// so the page can name it and the permissions script can grant to it. Informational: the
    /// credential itself comes from the host.</summary>
    public string? ManagedIdentityPrincipalId { get; init; }

    /// <summary>The subscription whose role/deny assignments the RBAC resolver queries. Required
    /// for per-user Azure permission filtering; absent → the resolver fails closed.</summary>
    public string? SubscriptionId { get; init; }
}
