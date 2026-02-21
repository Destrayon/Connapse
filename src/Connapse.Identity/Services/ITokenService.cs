using System.Security.Claims;
using Connapse.Core;

namespace Connapse.Identity.Services;

public interface ITokenService
{
    string GenerateAccessToken(IEnumerable<Claim> claims);
    Task<TokenResponse> GenerateTokenPairAsync(IEnumerable<Claim> claims, Guid userId, CancellationToken cancellationToken = default);
    Task<TokenResponse?> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
    ClaimsPrincipal? ValidateToken(string token);
}
