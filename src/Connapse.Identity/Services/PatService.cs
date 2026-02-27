using System.Security.Cryptography;
using System.Text;
using Connapse.Core;
using Connapse.Identity.Authentication;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Identity.Services;

public class PatService(
    ConnapseIdentityDbContext dbContext,
    ILogger<PatService> logger)
{
    public async Task<PatCreateResponse> CreateAsync(
        Guid userId,
        PatCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        // Generate token: cnp_ + 32 random bytes as base64url
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var token = ApiKeyAuthenticationOptions.TokenPrefix + Base64UrlEncode(randomBytes);

        var tokenHash = ComputeSha256Hash(token);
        var tokenPrefix = token[..12];

        var entity = new PersonalAccessTokenEntity
        {
            UserId = userId,
            Name = request.Name,
            TokenHash = tokenHash,
            TokenPrefix = tokenPrefix,
            Scopes = request.Scopes is { Length: > 0 } s ? string.Join(",", s) : "",
            ExpiresAt = request.ExpiresAt,
            CreatedAt = DateTime.UtcNow,
        };

        dbContext.PersonalAccessTokens.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Created PAT '{Name}' (prefix: {Prefix}) for user {UserId}",
            request.Name, tokenPrefix, userId);

        return new PatCreateResponse(
            Id: entity.Id,
            Name: entity.Name,
            Token: token,
            Scopes: SplitScopes(entity.Scopes),
            CreatedAt: entity.CreatedAt,
            ExpiresAt: entity.ExpiresAt);
    }

    public async Task<IReadOnlyList<PatListItem>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.PersonalAccessTokens
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        return entities.Select(p => new PatListItem(
            p.Id,
            p.Name,
            p.TokenPrefix,
            SplitScopes(p.Scopes),
            p.CreatedAt,
            p.ExpiresAt,
            p.LastUsedAt,
            p.RevokedAt != null))
            .ToList();
    }

    public async Task<bool> RevokeAsync(
        Guid userId,
        Guid patId,
        CancellationToken cancellationToken = default)
    {
        var pat = await dbContext.PersonalAccessTokens
            .FirstOrDefaultAsync(p => p.Id == patId && p.UserId == userId && p.RevokedAt == null,
                cancellationToken);

        if (pat is null)
            return false;

        pat.RevokedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Revoked PAT {PatId} for user {UserId}", patId, userId);
        return true;
    }

    private static string[] SplitScopes(string scopes) =>
        string.IsNullOrWhiteSpace(scopes) ? [] : scopes.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string ComputeSha256Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
