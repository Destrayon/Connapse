using System.Security.Claims;
using Connapse.Core;

namespace Connapse.Identity.Services;

public interface ITokenService
{
    string GenerateAccessToken(IEnumerable<Claim> claims, string? audience = null, string? issuer = null);
    Task<TokenResponse> GenerateTokenPairAsync(IEnumerable<Claim> claims, Guid userId, string? audience = null, string? issuer = null, CancellationToken cancellationToken = default);
    Task<TokenResponse?> RefreshTokenAsync(string refreshToken, string? issuer = null, CancellationToken cancellationToken = default);
    ClaimsPrincipal? ValidateToken(string token);
}
