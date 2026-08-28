using Connapse.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Identity.Services;

/// <summary>
/// Wraps <see cref="AwsIdentityLinkStore"/> with the one piece of logic a store should not own: the
/// live call to Cognito's <c>/oauth2/revoke</c> endpoint that a disconnect requires.
/// </summary>
/// <remarks>
/// Deleting the local row without revoking would leave the refresh token valid at Cognito — anything
/// that already holds a copy keeps working while Connapse reports the link gone. So the order here is
/// fixed: try to revoke first, then delete only the row just revoked (discriminated by its protected
/// token value, not its Id — see <see cref="AwsIdentityLinkStore.DeleteAsync(Guid, string, CancellationToken)"/>),
/// so a reconnect racing the disconnect can never lose a link it never revoked. A revocation failure
/// is reported back to the caller rather than swallowed, so the UI can say the token may still be live
/// instead of claiming a clean disconnect.
/// </remarks>
public sealed class AwsIdentityLinkService(
    AwsIdentityLinkStore linkStore,
    IOptionsMonitor<CognitoSettings> cognitoOptions,
    IHttpClientFactory httpClientFactory,
    ILogger<AwsIdentityLinkService> logger) : IAwsIdentityLinkService
{
    public async Task<AwsIdentityLinkDto?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var link = await linkStore.GetAsync(userId, ct);
        return link is null ? null : new AwsIdentityLinkDto(link.Email, link.ConnectedAt, link.LastUsedAt);
    }

    public async Task<AwsIdentityLinkDisconnectResult> DisconnectAsync(Guid userId, CancellationToken ct = default)
    {
        // One fetch serves both questions this needs answered — whether a row exists at all, and
        // whether its token can be read — so a caller that must tell those two apart (this one)
        // does not pay for a second round trip to do it. GetRefreshTokenAsync collapses both into
        // a single null and is right to for its own simpler callers, but "no link" and "link
        // present, token unreadable" are not the same case here: only the first has nothing to
        // revoke. The second is a real link Connapse simply lost the ability to speak for, and
        // that is a revocation failure, not a no-op.
        var link = await linkStore.GetAsync(userId, ct);

        if (link is null)
            return new AwsIdentityLinkDisconnectResult(Deleted: false, RevokedSuccessfully: true);

        var refreshToken = linkStore.TryUnprotectToken(link);
        var revoked = refreshToken is not null && await TryRevokeAsync(refreshToken, ct);

        // Delete only the row just revoked, not "whatever is there for this user now": a
        // reconnect that raced this call runs SaveAsync between the fetch above and this delete,
        // which updates the existing row in place and keeps its Id — so an Id-based delete would
        // remove the new link while its refresh token stays live at Cognito, precisely what
        // disconnect exists to prevent. The protected token value is the discriminator, since it
        // changes on every SaveAsync; a mismatch means the row changed underneath this call, and
        // the row is left alone for the caller to report and the user to retry.
        var deleted = await linkStore.DeleteAsync(userId, link.ProtectedRefreshToken, ct);
        var linkChanged = !deleted;

        if (linkChanged)
        {
            logger.LogWarning(
                "AWS identity link changed for a user during disconnect; the row was left in place");
        }

        return new AwsIdentityLinkDisconnectResult(deleted, revoked, linkChanged);
    }

    private async Task<bool> TryRevokeAsync(string refreshToken, CancellationToken ct)
    {
        var cognito = cognitoOptions.CurrentValue;
        if (!cognito.IsConfigured)
        {
            // Settings can be cleared by an admin after a link was already established. There is
            // no pool to call, so this cannot be treated as a successful revocation.
            logger.LogWarning("Cannot revoke an AWS identity link because Cognito is not configured");
            return false;
        }

        var httpClient = httpClientFactory.CreateClient();
        // A browser is waiting on this. Don't rely on HttpClient's 100-second default.
        httpClient.Timeout = TimeSpan.FromSeconds(10);

        var revokeParams = new Dictionary<string, string>
        {
            ["token"] = refreshToken,
            ["client_id"] = cognito.ClientId,
            ["client_secret"] = cognito.ClientSecret,
        };

        try
        {
            // Never log revokeParams — it carries the refresh token and the client secret.
            var response = await httpClient.PostAsync(
                $"{cognito.Domain.TrimEnd('/')}/oauth2/revoke",
                new FormUrlEncodedContent(revokeParams), ct);

            if (response.IsSuccessStatusCode)
                return true;

            logger.LogWarning("Cognito token revocation failed with status {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // TaskCanceledException is what HttpClient throws on its own timeout above (and what a
            // client-disconnect cancellation looks like) — neither carries the token or secret, so
            // nothing more than the exception type is worth knowing here.
            logger.LogWarning(ex, "Cognito token revocation could not reach the pool in time");
            return false;
        }
    }
}
