using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Connapse.Core;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.Identity.Services;

public class JwtTokenService(
    IOptionsMonitor<JwtSettings> jwtSettings,
    ConnapseIdentityDbContext dbContext,
    ILogger<JwtTokenService> logger) : ITokenService
{
    public string GenerateAccessToken(IEnumerable<Claim> claims)
    {
        var settings = jwtSettings.CurrentValue;
        var key = GetSigningKey(settings);
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(settings.AccessTokenLifetimeMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<TokenResponse> GenerateTokenPairAsync(
        IEnumerable<Claim> claims,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var settings = jwtSettings.CurrentValue;

        var accessToken = GenerateAccessToken(claims);
        var refreshToken = GenerateRefreshToken();
        var refreshTokenHash = ComputeSha256Hash(refreshToken);

        var refreshTokenEntity = new RefreshTokenEntity
        {
            UserId = userId,
            TokenHash = refreshTokenHash,
            ExpiresAt = DateTime.UtcNow.AddDays(settings.RefreshTokenLifetimeDays),
            CreatedAt = DateTime.UtcNow,
        };

        dbContext.RefreshTokens.Add(refreshTokenEntity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new TokenResponse(
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresAt: DateTime.UtcNow.AddMinutes(settings.AccessTokenLifetimeMinutes));
    }

    public async Task<TokenResponse?> RefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var tokenHash = ComputeSha256Hash(refreshToken);

        var existingToken = await dbContext.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (existingToken is null)
        {
            logger.LogWarning("Refresh token not found");
            return null;
        }

        if (existingToken.RevokedAt is not null)
        {
            logger.LogWarning("Attempted to use revoked refresh token for user {UserId}", existingToken.UserId);
            return null;
        }

        if (existingToken.ExpiresAt < DateTime.UtcNow)
        {
            logger.LogWarning("Attempted to use expired refresh token for user {UserId}", existingToken.UserId);
            return null;
        }

        // Revoke old token
        existingToken.RevokedAt = DateTime.UtcNow;

        // Generate new token pair
        var newRefreshToken = GenerateRefreshToken();
        var newRefreshTokenHash = ComputeSha256Hash(newRefreshToken);

        existingToken.ReplacedByTokenHash = newRefreshTokenHash;

        var settings = jwtSettings.CurrentValue;

        // Build claims for the user
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, existingToken.UserId.ToString()),
            new(ClaimTypes.Name, existingToken.User.UserName ?? existingToken.User.Email ?? ""),
            new(ClaimTypes.Email, existingToken.User.Email ?? ""),
        };

        // Get user roles
        var userRoles = await dbContext.UserRoles
            .Where(ur => ur.UserId == existingToken.UserId)
            .Join(dbContext.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name!)
            .ToListAsync(cancellationToken);

        foreach (var role in userRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var accessToken = GenerateAccessToken(claims);

        var newRefreshTokenEntity = new RefreshTokenEntity
        {
            UserId = existingToken.UserId,
            TokenHash = newRefreshTokenHash,
            ExpiresAt = DateTime.UtcNow.AddDays(settings.RefreshTokenLifetimeDays),
            CreatedAt = DateTime.UtcNow,
        };

        dbContext.RefreshTokens.Add(newRefreshTokenEntity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new TokenResponse(
            AccessToken: accessToken,
            RefreshToken: newRefreshToken,
            ExpiresAt: DateTime.UtcNow.AddMinutes(settings.AccessTokenLifetimeMinutes));
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var settings = jwtSettings.CurrentValue;
        var key = GetSigningKey(settings);

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        try
        {
            var handler = new JwtSecurityTokenHandler();
            return handler.ValidateToken(token, validationParameters, out _);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "JWT validation failed");
            return null;
        }
    }

    private static SymmetricSecurityKey GetSigningKey(JwtSettings settings)
    {
        var secret = settings.Secret
            ?? throw new InvalidOperationException(
                "JWT secret is not configured. Set CONNAPSE_JWT_SECRET environment variable or Identity:Jwt:Secret in configuration.");

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    private static string GenerateRefreshToken()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(randomBytes);
    }

    private static string ComputeSha256Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
