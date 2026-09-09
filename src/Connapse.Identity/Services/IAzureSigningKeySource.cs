using Microsoft.IdentityModel.Tokens;

namespace Connapse.Identity.Services;

/// <summary>
/// Supplies the JSON Web Key Set (JWKS) signing keys Entra ID publishes for a tenant, so <see
/// cref="AzureIdTokenValidator"/> never has to know whether they came from the network or a test
/// double — validation itself must never make a network call.
/// </summary>
public interface IAzureSigningKeySource
{
    Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(string tenantId, CancellationToken ct);
}
