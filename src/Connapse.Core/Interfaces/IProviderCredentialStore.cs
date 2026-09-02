namespace Connapse.Core.Interfaces;

/// <summary>
/// A stored credential, without its secret.
/// </summary>
/// <param name="Provider">Which cloud — "aws", "azure".</param>
/// <param name="PrincipalName">The IAM principal it belongs to.</param>
/// <param name="CreatedAt">
/// When it was stored. Shown so an administrator can see a credential's age.
/// </param>
/// <param name="VerifiedAt">
/// The last time a call made with it was honoured, or null if that has never happened.
/// <para>
/// What separates "not working yet" from "not working any more". A new credential is refused for a
/// while because IAM is eventually consistent; one that worked and then stopped has been revoked, and
/// waiting will not bring it back.
/// </para>
/// </param>
public record ProviderCredentialInfo(
    string Provider,
    string? PrincipalName,
    DateTime CreatedAt,
    DateTime? VerifiedAt = null);

/// <summary>
/// A stored IAM Roles Anywhere configuration (non-secret). The private key is fetched separately via
/// <see cref="IProviderCredentialStore.GetRolesAnywhereMaterialAsync"/>, so a listing can never
/// render it.
/// </summary>
public record RolesAnywhereConfig(
    string CertificatePem,
    string TrustAnchorArn,
    string ProfileArn,
    string RoleArn,
    string Region);

/// <summary>The complete Roles Anywhere material (config + decrypted private key) read from one row snapshot, or null when the provider is not using Roles Anywhere.</summary>
public record RolesAnywhereCredentialMaterial(RolesAnywhereConfig Config, string PrivateKeyPem);

/// <summary>
/// Existence and timing of a stored credential, independent of its shape.
/// </summary>
/// <remarks>
/// All the status page needs to judge a credential without caring how it authenticates: that one
/// exists, when it was stored, and when a call made with it was last honoured. <see cref="VerifiedAt"/>
/// is what separates "not working yet" (freshly stored, still propagating) from "not working any more"
/// (worked once, then revoked). Carries no secret and no identifier.
/// </remarks>
public record ProviderCredentialStatus(DateTime CreatedAt, DateTime? VerifiedAt);

/// <summary>
/// The credential Connapse acts as against one cloud, replacing what the SDK would otherwise pick
/// up from the environment.
/// </summary>
/// <remarks>
/// One per provider. Connapse has a single identity per cloud — a connection narrows from it with
/// a role, it does not bring its own — so storing more than one would be an ambiguity nothing could
/// resolve.
/// <para>
/// The secret never appears on the read model. <see cref="GetRolesAnywhereMaterialAsync"/> is the
/// only way to it, so a page listing credentials cannot accidentally render one.
/// </para>
/// </remarks>
public interface IProviderCredentialStore
{
    /// <summary>
    /// Records that a call made with this credential was honoured.
    /// </summary>
    /// <remarks>
    /// The only write on this interface that is not about the credential itself, and it exists so a
    /// later failure can be told apart from a slow start. Callers may invoke it on every success;
    /// implementations should not treat that as a reason to write on every success.
    /// </remarks>
    /// <returns>False when there is no stored credential for the provider.</returns>
    Task<bool> MarkVerifiedAsync(string provider, DateTime when, CancellationToken ct = default);

    /// <summary>Removes it, falling back to whatever the environment provides.</summary>
    Task<bool> DeleteAsync(string provider, CancellationToken ct = default);

    /// <summary>Existence and timestamps of the stored credential, whatever its shape. Null when nothing is stored.</summary>
    Task<ProviderCredentialStatus?> GetStatusAsync(string provider, CancellationToken ct = default);

    /// <summary>The stored Roles Anywhere configuration, or null when the provider is not using one.</summary>
    Task<RolesAnywhereConfig?> GetRolesAnywhereAsync(string provider, CancellationToken ct = default);

    /// <summary>The complete Roles Anywhere material (config + decrypted private key) read from one row snapshot, or null when the provider is not using Roles Anywhere.</summary>
    /// <exception cref="ProviderCredentialUnavailableException">Stored but the key cannot be decrypted.</exception>
    Task<RolesAnywhereCredentialMaterial?> GetRolesAnywhereMaterialAsync(string provider, CancellationToken ct = default);

    /// <summary>
    /// Stores or replaces the provider's credential with a Roles Anywhere configuration, clearing any
    /// access-key fields so the runtime's mode choice stays unambiguous.
    /// </summary>
    Task<ProviderCredentialInfo> SaveRolesAnywhereAsync(
        string provider, RolesAnywhereConfig config, string privateKeyPem, string? principalName,
        Guid? createdByUserId, CancellationToken ct = default);
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

/// <summary>
/// Thrown when a Roles Anywhere credential is configured but cannot be used (missing key, unreadable
/// certificate/key, or a failed CreateSession). Signals fail-closed: the caller must surface the error,
/// never silently substitute a different identity such as the ambient chain.
/// </summary>
public class RolesAnywhereCredentialException(string provider, Exception inner)
    : Exception($"The Roles Anywhere credential for '{provider}' is configured but could not be used.", inner)
{
    public string Provider { get; } = provider;
}
