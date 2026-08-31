using Connapse.Core;
using Microsoft.Extensions.Logging;

namespace Connapse.Identity.Services;

/// <summary>
/// Reads and removes a user's connected IAM Identity Center identity.
/// </summary>
/// <remarks>
/// Thin, and much thinner than it was. Disconnecting used to revoke a stored refresh token at
/// Cognito before deleting the row, and could half-succeed. The link now holds an attested identity
/// rather than a credential, so there is nothing at AWS to tell and the only failure left is losing
/// a race with a reconnect.
/// </remarks>
public sealed class AwsIdentityLinkService(
    AwsIdentityLinkStore linkStore,
    ILogger<AwsIdentityLinkService> logger) : IAwsIdentityLinkService
{
    public async Task<AwsIdentityLinkDto?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var link = await linkStore.GetAsync(userId, ct);
        return link is null
            ? null
            : new AwsIdentityLinkDto(
                link.DirectoryUserName, link.Email, link.ConnectedAt, link.LastUsedAt);
    }

    public async Task<AwsIdentityLinkDisconnectResult> DisconnectAsync(Guid userId, CancellationToken ct = default)
    {
        var link = await linkStore.GetAsync(userId, ct);

        if (link is null)
            return new AwsIdentityLinkDisconnectResult(Deleted: false);

        // Delete only the row just read, not "whatever is there for this user now": a reconnect
        // that raced this call runs SaveAsync between the fetch above and this delete, which
        // updates the existing row in place and keeps its Id — so an Id-based delete would throw
        // away the link the user had just re-established. ConnectedAt is rewritten by every save,
        // so a mismatch means the row changed underneath this call and is left alone for the
        // caller to report and the user to retry.
        var deleted = await linkStore.DeleteAsync(userId, link.ConnectedAt, ct);
        var linkChanged = !deleted;

        if (linkChanged)
        {
            logger.LogWarning(
                "AWS identity link changed for a user during disconnect; the row was left in place");
        }

        return new AwsIdentityLinkDisconnectResult(deleted, linkChanged);
    }
}
