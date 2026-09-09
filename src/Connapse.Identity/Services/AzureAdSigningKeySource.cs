using System.Collections.Concurrent;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.Identity.Services;

/// <summary>
/// Fetches Entra ID's published JWKS signing keys per tenant from its OIDC discovery document,
/// via <see cref="ConfigurationManager{T}"/> — which already caches the document (default
/// refresh interval) and dedupes concurrent refreshes, so no separate cache is needed here.
/// </summary>
/// <remarks>
/// <c>Microsoft.IdentityModel.Protocols.OpenIdConnect</c> is already a direct dependency of this
/// project (pinned alongside the rest of the Microsoft.IdentityModel set — see the .csproj
/// comment), so this uses <see cref="ConfigurationManager{T}"/> rather than hand-rolling the
/// discovery + JWKS fetch, per the task brief's fallback allowance.
/// </remarks>
public sealed class AzureAdSigningKeySource(HttpClient httpClient) : IAzureSigningKeySource
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new();

    public async Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(string tenantId, CancellationToken ct)
    {
        ConfigurationManager<OpenIdConnectConfiguration> manager = _managers.GetOrAdd(tenantId, tid =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                $"https://login.microsoftonline.com/{tid}/v2.0/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(httpClient) { RequireHttps = true }));

        OpenIdConnectConfiguration config = await manager.GetConfigurationAsync(ct);
        return config.SigningKeys.ToList();
    }
}
