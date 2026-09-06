using Connapse.Core;

namespace Connapse.Identity.Services;

/// <summary>
/// Reads, stores, and removes a user's connected Microsoft Entra identity.
/// </summary>
/// <remarks>
/// Thin, mirroring <see cref="AwsIdentityLinkService"/>: the link holds an attested identity
/// rather than a credential, so there is nothing at Entra to tell on disconnect — this simply
/// delegates to <see cref="AzureIdentityLinkStore"/>.
/// </remarks>
public sealed class AzureIdentityLinkService(AzureIdentityLinkStore linkStore) : IAzureIdentityLinkService
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

    public Task<bool> DisconnectAsync(Guid userId, CancellationToken ct = default) =>
        linkStore.DeleteAsync(userId, ct);
}
