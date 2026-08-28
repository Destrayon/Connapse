using Connapse.Core;

namespace Connapse.Identity.Services;

/// <summary>
/// The read/disconnect side of a user's Cognito-based AWS identity link, for the integrations page
/// and any other caller that needs the link's state without touching <see cref="AwsIdentityLinkStore"/>
/// directly.
/// </summary>
public interface IAwsIdentityLinkService
{
    /// <summary>The link's display state, or null when the user has not connected one.</summary>
    Task<AwsIdentityLinkDto?> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Revokes the refresh token at Cognito, then deletes the local row regardless of whether
    /// revocation succeeded — see <see cref="AwsIdentityLinkDisconnectResult"/>.
    /// </summary>
    Task<AwsIdentityLinkDisconnectResult> DisconnectAsync(Guid userId, CancellationToken ct = default);
}
