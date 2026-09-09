namespace Connapse.Core.Interfaces;

/// <summary>
/// The managed identity the host Connapse runs on can sign in as, described from a token Azure
/// issued to it.
/// </summary>
/// <param name="TenantId">The tenant the identity lives in (the token's <c>tid</c>).</param>
/// <param name="PrincipalId">The identity's service principal object id (<c>oid</c>) — what roles
/// and Graph app roles are assigned to.</param>
/// <param name="ClientId">The identity's application (client) id (<c>appid</c>).</param>
/// <param name="ResourceId">The Azure resource the identity belongs to (<c>xms_mirid</c>): for a
/// user-assigned identity the identity resource itself, for a system-assigned one the host.</param>
/// <param name="SubscriptionId">The subscription parsed out of <paramref name="ResourceId"/>, when present.</param>
/// <param name="IsUserAssigned">Whether the resource is a user-assigned identity rather than the host.</param>
public sealed record AzureHostIdentityInfo(
    string TenantId,
    string PrincipalId,
    string ClientId,
    string? ResourceId,
    string? SubscriptionId,
    bool IsUserAssigned);

/// <summary>
/// Finds out whether the host has a managed identity, so the guided Azure setup can grant it
/// access instead of creating a certificate app. Off Azure the answer is simply "no".
/// </summary>
public interface IAzureHostIdentity
{
    /// <summary>The host's managed identity, or null when the host has none (or the metadata
    /// service did not answer in time). Never throws; a found identity is remembered for the
    /// process, since a host's identity rarely changes while it runs — <paramref name="refresh"/>
    /// asks again regardless, for the cases where it has (an identity disabled and re-enabled).</summary>
    Task<AzureHostIdentityInfo?> DetectAsync(bool refresh = false, CancellationToken ct = default);
}
