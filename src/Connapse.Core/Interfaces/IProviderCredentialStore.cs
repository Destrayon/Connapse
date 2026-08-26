namespace Connapse.Core.Interfaces;

/// <summary>
/// A stored credential, without its secret.
/// </summary>
/// <param name="Provider">Which cloud — "aws", "azure".</param>
/// <param name="PublicId">The access key id, or the equivalent identifier.</param>
/// <param name="PrincipalName">The IAM user or principal it belongs to.</param>
/// <param name="CreatedAt">
/// When it was stored. Shown so an administrator can see a key's age: guidance on static keys is to
/// rotate them, and a key whose age is invisible is one nobody rotates.
/// </param>
public record ProviderCredentialInfo(
    string Provider,
    string PublicId,
    string? PrincipalName,
    DateTime CreatedAt);

/// <summary>
/// The credential Connapse acts as against one cloud, replacing what the SDK would otherwise pick
/// up from the environment.
/// </summary>
/// <remarks>
/// One per provider. Connapse has a single identity per cloud — a connection narrows from it with
/// a role, it does not bring its own — so storing more than one would be an ambiguity nothing could
/// resolve.
/// <para>
/// The secret never appears on the read model. <see cref="GetSecretAsync"/> is the only way to it,
/// so a page listing credentials cannot accidentally render one.
/// </para>
/// </remarks>
public interface IProviderCredentialStore
{
    Task<ProviderCredentialInfo?> GetAsync(string provider, CancellationToken ct = default);

    /// <summary>The secret half, decrypted. Null when nothing is stored.</summary>
    /// <exception cref="ProviderCredentialUnavailableException">
    /// The row exists and cannot be decrypted, which means the DataProtection key ring that wrote
    /// it is gone. Distinct from "nothing stored": retrying will not help, and the caller should
    /// ask for the credential again rather than treat it as absent.
    /// </exception>
    Task<string?> GetSecretAsync(string provider, CancellationToken ct = default);

    /// <summary>Stores or replaces the credential for a provider.</summary>
    Task<ProviderCredentialInfo> SaveAsync(
        string provider, string publicId, string secret, string? principalName,
        Guid? createdByUserId, CancellationToken ct = default);

    /// <summary>Removes it, falling back to whatever the environment provides.</summary>
    Task<bool> DeleteAsync(string provider, CancellationToken ct = default);
}

/// <summary>
/// Thrown when a stored credential exists but cannot be decrypted.
/// </summary>
/// <remarks>
/// In practice the DataProtection key ring that encrypted it is gone — a replaced volume, or a
/// replica that does not share the key directory. Mirrors
/// <c>ConnectionSecretUnavailableException</c>, and for the same reason: silently reporting "no
/// credential" would send an administrator to set one up again without explaining why the last one
/// vanished.
/// </remarks>
public class ProviderCredentialUnavailableException(string provider, Exception inner)
    : Exception($"The stored credential for '{provider}' could not be decrypted. The DataProtection " +
                "key ring that encrypted it is unavailable, so the credential must be entered again.",
                inner)
{
    public string Provider { get; } = provider;
}
