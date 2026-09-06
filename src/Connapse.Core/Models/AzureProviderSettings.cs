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
}
