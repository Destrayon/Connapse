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
/// fixed: try to revoke first, then delete the row unconditionally. A revocation failure is reported
/// back to the caller rather than swallowed, so the UI can say the token may still be live instead of
/// claiming a clean disconnect.
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
        var refreshToken = await linkStore.GetRefreshTokenAsync(userId, ct);

        // Nothing to revoke either because there was never a link, or because the stored token
        // could no longer be decrypted — either way there is no live token this call can affect.
        var revoked = refreshToken is null || await TryRevokeAsync(refreshToken, ct);

        // Delete unconditionally: a user who clicks Disconnect must end up disconnected locally
        // regardless of what AWS reports.
        var deleted = await linkStore.DeleteAsync(userId, ct);

        return new AwsIdentityLinkDisconnectResult(deleted, revoked);
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
