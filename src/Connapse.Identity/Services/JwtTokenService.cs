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
    public string GenerateAccessToken(IEnumerable<Claim> claims, string? audience = null, string? issuer = null)
    {
        var settings = jwtSettings.CurrentValue;
        var credentials = GetSigningCredentials(settings);

        var token = new JwtSecurityToken(
            issuer: issuer ?? settings.Issuer,
            audience: audience ?? settings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(settings.AccessTokenLifetimeMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<TokenResponse> GenerateTokenPairAsync(
        IEnumerable<Claim> claims,
        Guid userId,
        string? audience = null,
        string? issuer = null,
        CancellationToken cancellationToken = default)
    {
        var settings = jwtSettings.CurrentValue;

        var accessToken = GenerateAccessToken(claims, audience, issuer);
        var refreshToken = GenerateRefreshToken();
        var refreshTokenHash = ComputeSha256Hash(refreshToken);

        var refreshTokenEntity = new RefreshTokenEntity
        {
            UserId = userId,
            TokenHash = refreshTokenHash,
            ExpiresAt = DateTime.UtcNow.AddDays(settings.RefreshTokenLifetimeDays),
            FamilyId = Guid.NewGuid(),
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
        string? issuer = null,
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

        var settings = jwtSettings.CurrentValue;

        // A token rotated moments ago is redeemed again when two sessions sharing one sign-in refresh together, or
        // a client never got the response; while its family is still live that is not theft, and the caller gets
        // a token pair of its own in the same family (CON-360).
        bool redeemedAgain = existingToken.RevokedAt is not null;
        if (redeemedAgain && !await MayRedeemAgainAsync(existingToken, settings, cancellationToken))
        {
            logger.LogWarning("Attempted to use revoked refresh token for user {UserId} — revoking token family", existingToken.UserId);
            await RevokeTokenFamilyAsync(existingToken, cancellationToken);
            return null;
        }

        if (existingToken.ExpiresAt < DateTime.UtcNow)
        {
            logger.LogWarning("Attempted to use expired refresh token for user {UserId}", existingToken.UserId);
            return null;
        }

        // A client's refresh token stored before its scope was can't say what the client was granted, so it is
        // refused and the client signs in again (#934).
        if (existingToken.ClientId is not null && existingToken.Scope is null)
        {
            logger.LogWarning("Refused a refresh token for user {UserId} that holds no granted scope", existingToken.UserId);
            return null;
        }

        // Generate new token pair
        var newRefreshToken = GenerateRefreshToken();
        var newRefreshTokenHash = ComputeSha256Hash(newRefreshToken);

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

        // An OAuth client's token keeps the client and exactly the scopes it was granted; without them a
        // refreshed token reads as a sign-in, whose roles imply every scope they allow (#934).
        if (existingToken.ClientId is { } clientId)
            claims.Add(new Claim("client_id", clientId));
        foreach (var scope in (existingToken.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            claims.Add(new Claim("scope", scope));

        // Carry the RFC 8707 resource binding through the refresh chain so the
        // new access token keeps the same `aud` the MCP client originally asked
        // for. Without this, refreshed tokens would fall back to the static
        // server audience and the MCP client would reject them. The `iss` claim
        // is bound to the current request's scheme+authority (passed in by the
        // caller) so it matches the AS metadata document per RFC 9068 §2.2.
        var accessToken = GenerateAccessToken(claims, existingToken.Resource, issuer);

        // A token from before families were recorded starts one named after itself at its first rotation.
        Guid familyId = existingToken.FamilyId ?? existingToken.Id;
        var newRefreshTokenEntity = new RefreshTokenEntity
        {
            UserId = existingToken.UserId,
            TokenHash = newRefreshTokenHash,
            ClientId = existingToken.ClientId,
            Resource = existingToken.Resource,
            Scope = existingToken.Scope,
            ExpiresAt = DateTime.UtcNow.AddDays(settings.RefreshTokenLifetimeDays),
            FamilyId = familyId,
            CreatedAt = DateTime.UtcNow,
        };

        // The old token is retired only if it is still unrevoked when the replacement is saved: a revoke or another
        // refresh of it that committed after it was read wins, and this refresh issues nothing. The client lock makes
        // a revoke that starts before this commits wait for it, then revoke the replacement too (CON-189).
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (existingToken.ClientId is { } lockedClient)
            await OAuthClientLock.AcquireAsync(dbContext, lockedClient, cancellationToken);
        // Orders this refresh against a theft revoke of the same family, so the revoke sees the token saved here.
        await AcquireFamilyLockAsync(familyId, cancellationToken);
        if (redeemedAgain)
        {
            // Checked again under the lock: a revoke that committed since the check above leaves nothing live.
            if (!await FamilyIsLiveAsync(familyId, cancellationToken))
            {
                logger.LogWarning("Refresh token family for user {UserId} was revoked while it was being refreshed", existingToken.UserId);
                return null;
            }
        }
        else
        {
            DateTime revokedAt = DateTime.UtcNow;
            int retired = await dbContext.RefreshTokens
                .Where(t => t.Id == existingToken.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.RevokedAt, revokedAt)
                    .SetProperty(t => t.ReplacedByTokenHash, newRefreshTokenHash)
                    .SetProperty(t => t.FamilyId, familyId), cancellationToken);
            if (retired == 0)
            {
                // Another refresh of the same token committed first: the second of two at once is let through like a
                // reuse in the interval. A revoke that committed first leaves nothing to let through (CON-360).
                var current = await dbContext.RefreshTokens.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == existingToken.Id, cancellationToken);
                if (current is null || !await MayRedeemAgainAsync(current, settings, cancellationToken))
                {
                    logger.LogWarning("Refresh token for user {UserId} was revoked while it was being refreshed", existingToken.UserId);
                    return null;
                }
            }
        }

        dbContext.RefreshTokens.Add(newRefreshTokenEntity);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new TokenResponse(
            AccessToken: accessToken,
            RefreshToken: newRefreshToken,
            ExpiresAt: DateTime.UtcNow.AddMinutes(settings.AccessTokenLifetimeMinutes));
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var settings = jwtSettings.CurrentValue;

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = GetValidationKeys(settings),
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            // Audience is not validated here — this helper is standalone
            // and has no HTTP request context, so it can't apply the
            // RFC 8707 canonical-URI scheme+authority check that the
            // JwtBearer middleware performs for API/MCP requests. Callers
            // that need per-deployment audience checks must go through
            // the auth pipeline.
            ValidateAudience = false,
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

    private static SigningCredentials GetSigningCredentials(JwtSettings settings) =>
        new(GetHs256Key(settings), SecurityAlgorithms.HmacSha256);

    private static IEnumerable<SecurityKey> GetValidationKeys(JwtSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.Secret))
            yield return GetHs256Key(settings);
    }

    private static SymmetricSecurityKey GetHs256Key(JwtSettings settings)
    {
        var secret = settings.Secret
            ?? throw new InvalidOperationException(
                "JWT secret is not configured. Set Identity__Jwt__Secret environment variable.");

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    private static string GenerateRefreshToken()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(randomBytes);
    }

    /// <summary>
    /// Whether a revoked token may be redeemed again: it was retired by a rotation (not a revoke) within the reuse
    /// interval, and its family still holds a live token, which no revoke or theft detection leaves (CON-360).
    /// </summary>
    private async Task<bool> MayRedeemAgainAsync(RefreshTokenEntity token, JwtSettings settings, CancellationToken ct)
    {
        int seconds = Math.Clamp(settings.RefreshTokenReuseIntervalSeconds, 0, JwtSettings.MaxRefreshTokenReuseIntervalSeconds);
        return seconds > 0
            && token.ReplacedByTokenHash is not null
            && token.RevokedAt is { } revokedAt
            && DateTime.UtcNow - revokedAt <= TimeSpan.FromSeconds(seconds)
            && token.FamilyId is { } familyId
            && await FamilyIsLiveAsync(familyId, ct);
    }

    private Task AcquireFamilyLockAsync(Guid familyId, CancellationToken ct)
    {
        string key = "refresh-family:" + familyId.ToString("N");
        return dbContext.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }

    private Task<bool> FamilyIsLiveAsync(Guid familyId, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        return dbContext.RefreshTokens.AnyAsync(
            t => t.FamilyId == familyId && t.RevokedAt == null && t.ExpiresAt > now, ct);
    }

    /// <summary>
    /// Revokes every token descended from the same sign-in as a replayed token (potential token theft). A token
    /// from before families were recorded has its ReplacedByTokenHash chain walked forward instead.
    /// </summary>
    private async Task RevokeTokenFamilyAsync(RefreshTokenEntity replayed, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        if (replayed.FamilyId is { } familyId)
        {
            // Taken in the refresh's order, so a refresh of this family that is mid-way commits first and its new
            // token is revoked here, and one that starts later finds the family revoked.
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            if (replayed.ClientId is { } lockedClient)
                await OAuthClientLock.AcquireAsync(dbContext, lockedClient, ct);
            await AcquireFamilyLockAsync(familyId, ct);
            int revoked = await dbContext.RefreshTokens
                .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
            await transaction.CommitAsync(ct);
            logger.LogWarning("Revoked {Count} refresh tokens in family {FamilyId}", revoked, familyId);
            return;
        }

        // Rotated links are already revoked, so the walk passes them rather than stopping at the first.
        string? tokenHash = replayed.ReplacedByTokenHash;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(tokenHash) && seen.Add(tokenHash))
        {
            var token = await dbContext.RefreshTokens
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
            if (token is null)
                break;

            if (token.RevokedAt is null)
            {
                token.RevokedAt = now;
                logger.LogWarning("Revoked refresh token in family chain: {TokenHash}", tokenHash[..8]);
            }

            // A token rotated after families were recorded carries one: revoke the rest of it in one go.
            if (token.FamilyId is { } laterFamily)
            {
                await dbContext.SaveChangesAsync(ct);
                await dbContext.RefreshTokens
                    .Where(t => t.FamilyId == laterFamily && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
                return;
            }

            tokenHash = token.ReplacedByTokenHash;
        }

        await dbContext.SaveChangesAsync(ct);
    }

    private static string ComputeSha256Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
