namespace Connapse.Core;

/// <summary>
/// The Entra ID (Azure AD) application a person signs in through to prove which directory
/// user they are.
/// </summary>
/// <remarks>
/// Connapse authenticates to Entra with a client certificate rather than a client secret, so
/// there is nothing here for an attacker to steal out of configuration — <see
/// cref="ClientCertificatePath"/> points at a file on disk, and <see
/// cref="ClientCertificatePassword"/> only applies to password-protected PFX bundles. A PEM
/// certificate needs no password, which is why it is optional rather than required.
/// </remarks>
public record AzureAdSignInSettings
{
    public const string SectionName = "Identity:AzureAd";

    /// <summary>The Entra tenant (directory) id the application is registered under.</summary>
    public string? TenantId { get; init; }

    /// <summary>The application (client) id registered in Entra ID.</summary>
    public string? ClientId { get; init; }

    /// <summary>Where Entra redirects back to after sign-in, registered on the application.</summary>
    public string? RedirectUri { get; init; }

    /// <summary>
    /// Path to the client certificate (PEM or PFX) Connapse authenticates to Entra with.
    /// </summary>
    public string? ClientCertificatePath { get; init; }

    /// <summary>
    /// Password for the certificate at <see cref="ClientCertificatePath"/>, if it is a
    /// password-protected PFX. A PEM certificate needs none, so this is optional.
    /// </summary>
    public string? ClientCertificatePassword { get; init; }

    /// <summary>True once every field sign-in needs — other than the certificate password — is set.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(RedirectUri)
        && !string.IsNullOrWhiteSpace(ClientCertificatePath);
}
