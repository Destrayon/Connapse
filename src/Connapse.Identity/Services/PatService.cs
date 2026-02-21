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
            Scopes = request.Scopes ?? "",
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
            Scopes: entity.Scopes,
            CreatedAt: entity.CreatedAt,
            ExpiresAt: entity.ExpiresAt);
    }

    public async Task<IReadOnlyList<PatListItem>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.PersonalAccessTokens
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new PatListItem(
                p.Id,
                p.Name,
                p.TokenPrefix,
                p.Scopes,
                p.CreatedAt,
                p.ExpiresAt,
                p.LastUsedAt,
                p.RevokedAt != null))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RevokeAsync(
        Guid userId,
        Guid patId,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.PersonalAccessTokens
            .Where(p => p.Id == patId && p.UserId == userId && p.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.RevokedAt, DateTime.UtcNow),
                cancellationToken);

        if (rows > 0)
            logger.LogInformation("Revoked PAT {PatId} for user {UserId}", patId, userId);

        return rows > 0;
    }

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
