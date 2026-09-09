using Connapse.Core;

namespace Connapse.Identity.Services;

/// <summary>
/// Reads, stores, and removes a user's connected Microsoft Entra identity.
/// </summary>
/// <remarks>
/// Thin, mirroring <see cref="AwsIdentityLinkService"/>: the link holds an attested identity
/// rather than a credential, so there is nothing at Entra to tell on disconnect — this delegates
/// to <see cref="AzureIdentityLinkStore"/>. Disconnecting also refuses every sign-in or parked
/// confirmation the user started before it: a second tab that completes an older flow after the
/// disconnect must not put the link back.
/// </remarks>
public sealed class AzureIdentityLinkService(
    AzureIdentityLinkStore linkStore,
    AzureSignInRequests signIns,
    AzureLinkConfirmations confirmations) : IAzureIdentityLinkService
{
    public async Task<AzureIdentityLinkDto?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var link = await linkStore.GetAsync(userId, ct);
        return link is null
            ? null
            : new AzureIdentityLinkDto(link.ObjectId, link.TenantId, link.DisplayName, link.ConnectedAt);
    }

    public Task StoreAsync(
        Guid userId, string oid, string tid, string displayName, CancellationToken ct = default) =>
        linkStore.SaveAsync(userId, oid, tid, displayName, ct);

    public Task<bool> DisconnectAsync(Guid userId, CancellationToken ct = default)
    {
        // Revoked before the row goes, so a flow racing the delete finds the revocation whichever
        // order the two land in.
        signIns.RevokeFor(userId);
        confirmations.RevokeFor(userId);
        return linkStore.DeleteAsync(userId, ct);
    }
}
