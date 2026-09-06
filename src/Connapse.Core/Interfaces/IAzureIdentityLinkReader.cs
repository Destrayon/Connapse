namespace Connapse.Core.Interfaces;

/// <summary>
/// Which Microsoft Entra identity a Connapse user proved they were.
/// </summary>
/// <remarks>
/// A Core interface so the scope resolver need not reference the identity layer to ask the one
/// question it has of it — mirrors <c>IAwsIdentityLinkReader</c>. The answer is the oid+tid pair
/// rather than a credential, because there is no credential to hold: Entra attests the identity
/// once at link time, and permissions are read later with Connapse's own identity.
/// </remarks>
public interface IAzureIdentityLinkReader
{
    /// <summary>
    /// The Entra identity linked to <paramref name="userId"/>, or null when they have connected
    /// none.
    /// </summary>
    Task<AzureIdentityRef?> GetLinkAsync(Guid userId, CancellationToken ct = default);
}
