using System.Security.Cryptography;
using System.Text;
using Connapse.Core;
using Connapse.Core.Utilities;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Identity.Services;

public class CliAuthService(
    ConnapseIdentityDbContext dbContext,
    PatService patService,
    ILogger<CliAuthService> logger)
{
    private static readonly TimeSpan CodeExpiry = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates a short-lived CLI auth code bound to the given PKCE challenge and redirect URI.
    /// Returns the raw code (shown only once — never stored plain).
    /// </summary>
    public async Task<string> InitiateAsync(
        Guid userId,
        string codeChallenge,
        string redirectUri,
        string machineName,
        CancellationToken cancellationToken = default)
    {
        var rawCode = GenerateToken();
        var codeHash = HashToken(rawCode);

        var entity = new CliAuthCodeEntity
        {
            UserId = userId,
            CodeHash = codeHash,
            CodeChallenge = codeChallenge,
            RedirectUri = redirectUri,
            MachineName = machineName,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(CodeExpiry),
        };

        dbContext.Set<CliAuthCodeEntity>().Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("CLI auth code created for user {UserId} on machine {MachineName}",
            userId, LogSanitizer.Sanitize(machineName));

        return rawCode;
    }

    /// <summary>
    /// Validates PKCE and exchanges a CLI auth code for a new PAT.
    /// Returns null if the code is invalid, expired, already used, or PKCE fails.
    /// </summary>
    public async Task<(PatCreateResponse Pat, string UserEmail)?> ExchangeAsync(
        string rawCode,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        var codeHash = HashToken(rawCode);

        var entity = await dbContext.Set<CliAuthCodeEntity>()
            .Include(e => e.User)
            .FirstOrDefaultAsync(e => e.CodeHash == codeHash, cancellationToken);

        if (entity is null)
        {
            logger.LogWarning("CLI auth exchange failed: code not found");
            return null;
        }

        if (entity.UsedAt is not null)
        {
            logger.LogWarning("CLI auth exchange failed: code already used (id={Id})", entity.Id);
            return null;
        }

        if (entity.ExpiresAt < DateTime.UtcNow)
        {
            logger.LogWarning("CLI auth exchange failed: code expired (id={Id})", entity.Id);
            return null;
        }

        if (!string.Equals(entity.RedirectUri, redirectUri, StringComparison.Ordinal))
        {
            logger.LogWarning("CLI auth exchange failed: redirect_uri mismatch (id={Id})", entity.Id);
            return null;
        }

        // PKCE verification: BASE64URL(SHA256(UTF8(codeVerifier))) must match stored challenge
        var expectedChallenge = ComputeS256Challenge(codeVerifier);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedChallenge),
                Encoding.UTF8.GetBytes(entity.CodeChallenge)))
        {
            logger.LogWarning("CLI auth exchange failed: PKCE verification failed (id={Id})", entity.Id);
            return null;
        }

        // Mark as used before creating the PAT (prevents replay even if PAT creation fails)
        entity.UsedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var patRequest = new PatCreateRequest($"CLI ({entity.MachineName})");
        var pat = await patService.CreateAsync(entity.UserId, patRequest, cancellationToken);

        logger.LogInformation("CLI auth exchange succeeded for user {UserId}, PAT {PatId} created",
            entity.UserId, pat.Id);

        return (pat, entity.User.Email ?? entity.User.UserName ?? "");
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes);
    }

    private static string ComputeS256Challenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
