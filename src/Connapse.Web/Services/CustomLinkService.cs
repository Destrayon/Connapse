using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors.Custom;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.Web.Services;

/// <summary>A custom provider a user can link to: its linking page and the secret link tokens are signed with.</summary>
public sealed record CustomLinkTarget(CustomProvider Provider, string LinkUrl, string Secret);

/// <summary>
/// Account linking for custom providers. The link itself lives only in the middle man: Connapse signs the
/// token that starts it, confirms it for the signed-in user, and reads or removes it live.
/// </summary>
public sealed class CustomLinkService(
    ICustomProviderStore providers,
    IHttpClientFactory httpClients,
    IOptionsMonitor<CustomSourceSettings> settings,
    TimeProvider time)
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StatusBudget = TimeSpan.FromSeconds(3);

    /// <summary>Providers users can link to: their middle man offers linking and a link secret was generated.</summary>
    public async Task<IReadOnlyList<CustomProvider>> LinkableAsync(CancellationToken ct) =>
        (await providers.ListAsync(ct)).Where(p => p.LinkUrl is not null && p.HasLinkSecret).ToList();

    /// <summary>Null when the provider is gone, offers no linking, has no link secret, or its linking page breaks the URL rules.</summary>
    public async Task<CustomLinkTarget?> TargetAsync(Guid providerId, CancellationToken ct)
    {
        CustomProvider? provider = await providers.GetAsync(providerId, ct);
        if (provider?.LinkUrl is null
            || !CustomSourceEndpoint.TryNormalize(provider.LinkUrl, settings.CurrentValue.AllowedHttpHosts, out string linkUrl, out _))
            return null;

        string? secret = await providers.GetLinkSecretAsync(providerId, ct);
        return secret is null ? null : new CustomLinkTarget(provider, linkUrl, secret);
    }

    /// <summary>The HS256 token the browser carries to the linking page (protocol: Linking, step 1).</summary>
    public string CreateLinkToken(CustomLinkTarget target, Guid userId, string issuer, string returnUrl)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = target.Provider.Id.ToString(),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(TokenLifetime),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["jti"] = Guid.NewGuid().ToString("N"),
                ["return_url"] = returnUrl,
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(target.Secret)), SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>The middle man's answer for this user and code; null when it refused the code.</summary>
    public async Task<CustomLinkStatus?> ConfirmAsync(Guid providerId, string code, Guid userId, CancellationToken ct) =>
        await (await ClientAsync(providerId, ct)).ConfirmLinkAsync(code, userId.ToString(), ct);

    /// <summary>The user's link read live, within a short budget. Throws when the middle man cannot say.</summary>
    public async Task<CustomLinkStatus> StatusAsync(Guid providerId, Guid userId, CancellationToken ct)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(StatusBudget);
        try
        {
            return await (await ClientAsync(providerId, budget.Token)).GetLinkAsync(userId.ToString(), budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CustomSourceException("The middle man did not answer in time.");
        }
    }

    public async Task UnlinkAsync(Guid providerId, Guid userId, CancellationToken ct) =>
        await (await ClientAsync(providerId, ct)).UnlinkAsync(userId.ToString(), ct);

    /// <summary>
    /// Sends an unlink queued for a deleted user to the middle man it was queued for. The provider's current
    /// token is used while the provider still points at that same address, so a rotated token still works;
    /// otherwise the token from when it was queued.
    /// </summary>
    public async Task UnlinkQueuedAsync(CustomLinkRemoval removal, CancellationToken ct)
    {
        IReadOnlyCollection<string> allowed = settings.CurrentValue.AllowedHttpHosts;
        if (!CustomSourceEndpoint.TryNormalize(removal.BaseUrl, allowed, out string baseUrl, out string? error))
            throw new CustomSourceException(error ?? "The middle man's address is not allowed.");

        string? token = removal.Token;
        CustomProvider? provider = await providers.GetAsync(removal.ProviderId, ct);
        if (provider is not null
            && CustomSourceEndpoint.TryNormalize(provider.BaseUrl, allowed, out string current, out _)
            && string.Equals(current, baseUrl, StringComparison.Ordinal))
        {
            try
            {
                token = await providers.GetTokenAsync(provider.Id, ct) ?? token;
            }
            catch (InvalidOperationException)
            {
                // The stored token cannot be decrypted; the one captured when the unlink was queued may still work.
            }
        }

        CustomSourceClient client = new(httpClients.CreateClient(CustomSourceClient.HttpClientName), baseUrl, token);
        await client.UnlinkAsync(removal.UserId.ToString(), ct);
    }

    private async Task<CustomSourceClient> ClientAsync(Guid providerId, CancellationToken ct)
    {
        CustomProvider provider = await providers.GetAsync(providerId, ct)
            ?? throw new CustomSourceException("This custom provider no longer exists.");
        if (!CustomSourceEndpoint.TryNormalize(provider.BaseUrl, settings.CurrentValue.AllowedHttpHosts, out string baseUrl, out string? error))
            throw new CustomSourceException(error ?? "The custom provider's address is not allowed.");
        string? token = await providers.GetTokenAsync(providerId, ct);
        return new CustomSourceClient(httpClients.CreateClient(CustomSourceClient.HttpClientName), baseUrl, token);
    }
}
