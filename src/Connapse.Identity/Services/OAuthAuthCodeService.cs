using System.Security.Cryptography;
using System.Text;
using Connapse.Core.Utilities;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Identity.Services;

public class OAuthAuthCodeService(
    ConnapseIdentityDbContext dbContext,
    ILogger<OAuthAuthCodeService> logger)
{
    private static readonly TimeSpan CodeExpiry = TimeSpan.FromMinutes(5);

    public async Task<string> CreateAsync(
        Guid userId,
        string clientId,
        string codeChallenge,
        string redirectUri,
        string scope,
        string? resource = null,
        CancellationToken ct = default)
    {
        var rawCode = GenerateCode();
        var codeHash = ComputeSha256Hex(rawCode);

        var entity = new OAuthAuthCodeEntity
        {
            UserId = userId,
            ClientId = clientId,
            CodeHash = codeHash,
            CodeChallenge = codeChallenge,
            RedirectUri = redirectUri,
            Scope = scope,
            Resource = resource,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(CodeExpiry),
        };

        dbContext.OAuthAuthCodes.Add(entity);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("OAuth auth code created for user {UserId}, client {ClientId}", userId, LogSanitizer.Sanitize(clientId));
        return rawCode;
    }

    public async Task<OAuthCodeExchangeResult?> ExchangeAsync(
        string rawCode,
        string codeVerifier,
        string redirectUri,
        string clientId,
        CancellationToken ct = default)
    {
        var codeHash = ComputeSha256Hex(rawCode);

        var entity = await dbContext.OAuthAuthCodes
            .Include(e => e.User)
            .FirstOrDefaultAsync(e => e.CodeHash == codeHash, ct);

        if (entity is null)
        {
            logger.LogWarning("OAuth code exchange failed: code not found");
            return null;
        }

        if (entity.UsedAt is not null)
        {
            logger.LogWarning("OAuth code exchange failed: code already used (id={Id})", entity.Id);
            return null;
        }

        if (entity.ExpiresAt < DateTime.UtcNow)
        {
            logger.LogWarning("OAuth code exchange failed: code expired (id={Id})", entity.Id);
            return null;
        }

        if (!string.Equals(entity.ClientId, clientId, StringComparison.Ordinal))
        {
            logger.LogWarning("OAuth code exchange failed: client_id mismatch (id={Id})", entity.Id);
            return null;
        }

        if (!string.Equals(entity.RedirectUri, redirectUri, StringComparison.Ordinal))
        {
            logger.LogWarning("OAuth code exchange failed: redirect_uri mismatch (id={Id})", entity.Id);
            return null;
        }

        var expectedChallenge = ComputeS256Challenge(codeVerifier);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedChallenge),
                Encoding.UTF8.GetBytes(entity.CodeChallenge)))
        {
            logger.LogWarning("OAuth code exchange failed: PKCE verification failed (id={Id})", entity.Id);
            return null;
        }

        entity.UsedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("OAuth code exchange succeeded for user {UserId}, client {ClientId}", entity.UserId, LogSanitizer.Sanitize(clientId));

        return new OAuthCodeExchangeResult(entity.UserId, entity.Scope, entity.User.Email ?? entity.User.UserName ?? "", entity.Resource);
    }

    private static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    private static string ComputeS256Challenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}

public record OAuthCodeExchangeResult(Guid UserId, string Scope, string UserEmail, string? Resource);
