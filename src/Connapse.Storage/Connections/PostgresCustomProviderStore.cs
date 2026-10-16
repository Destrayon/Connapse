using System.Security.Cryptography;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Storage.Connections;

/// <summary>
/// Custom providers in their own table rather than <c>provider_credentials</c>, which holds one row per
/// provider kind: a deployment may register any number of middle men. Tokens are DataProtection ciphertext.
/// </summary>
public sealed class PostgresCustomProviderStore(
    IDbContextFactory<KnowledgeDbContext> factory,
    IDataProtectionProvider dataProtection,
    ILogger<PostgresCustomProviderStore> logger) : ICustomProviderStore
{
    private IDataProtector Protector => dataProtection.CreateProtector("CustomProvider.v1");
    private IDataProtector LinkProtector => dataProtection.CreateProtector("CustomProviderLink.v1");

    public async Task<CustomProvider> CreateAsync(SaveCustomProviderRequest request, CancellationToken ct = default)
    {
        string name = ValidName(request);
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        if (await context.CustomProviders.AnyAsync(p => p.Name == name, ct))
            throw new InvalidOperationException($"A custom provider named '{name}' already exists.");

        DateTime now = DateTime.UtcNow;
        CustomProviderEntity entity = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            BaseUrl = request.BaseUrl,
            TokenProtected = string.IsNullOrEmpty(request.Token) ? null : Protector.Protect(request.Token),
            MaxCheckBatch = request.MaxCheckBatch,
            SupportsAccessKeys = request.SupportsAccessKeys,
            LinkUrl = request.LinkUrl,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.CustomProviders.Add(entity);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Created custom provider {ProviderId} ({Name})", entity.Id, Sanitize(entity.Name));
        return ToModel(entity);
    }

    public async Task<CustomProvider?> UpdateAsync(Guid id, SaveCustomProviderRequest request, CancellationToken ct = default)
    {
        string name = ValidName(request);
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        CustomProviderEntity? entity = await context.CustomProviders.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null) return null;

        if (await context.CustomProviders.AnyAsync(p => p.Name == name && p.Id != id, ct))
            throw new InvalidOperationException($"A custom provider named '{name}' already exists.");

        // Its sources' documents were listed by this middle man; pointing the provider elsewhere would let
        // another system approve them. Moving it means new connections and sources.
        if (!string.Equals(entity.BaseUrl, request.BaseUrl, StringComparison.Ordinal)
            && await ConnectionCountAsync(context, id, ct) > 0)
            throw new InvalidOperationException(
                "A custom provider's address cannot change while connections use it: their sources' documents came from the "
                + "current middle man. Add a new provider for the new address instead.");

        entity.Name = name;
        entity.BaseUrl = request.BaseUrl;
        if (!string.IsNullOrEmpty(request.Token))
            entity.TokenProtected = Protector.Protect(request.Token);
        entity.MaxCheckBatch = request.MaxCheckBatch;
        entity.SupportsAccessKeys = request.SupportsAccessKeys;
        entity.LinkUrl = request.LinkUrl;
        entity.VerifiedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
        return ToModel(entity);
    }

    public async Task<CustomProvider?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        CustomProviderEntity? entity = await context.CustomProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return entity is null ? null : ToModel(entity);
    }

    public async Task<IReadOnlyList<CustomProvider>> ListAsync(CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        List<CustomProviderEntity> entities = await context.CustomProviders.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        return entities.Select(ToModel).ToList();
    }

    public async Task<string?> GetTokenAsync(Guid id, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        string? ciphertext = await context.CustomProviders.AsNoTracking()
            .Where(p => p.Id == id).Select(p => p.TokenProtected).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(ciphertext)) return null;

        try
        {
            return Protector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Failed to decrypt the token for custom provider {ProviderId}", id);
            throw new InvalidOperationException(
                $"The stored token for custom provider '{id}' could not be decrypted; enter it again.", ex);
        }
    }

    public async Task<string?> GenerateLinkSecretAsync(Guid id, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        CustomProviderEntity? entity = await context.CustomProviders.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null) return null;

        string secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        entity.LinkSecretProtected = LinkProtector.Protect(secret);
        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
        logger.LogInformation("Generated a new link secret for custom provider {ProviderId}", id);
        return secret;
    }

    public async Task<string?> GetLinkSecretAsync(Guid id, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        string? ciphertext = await context.CustomProviders.AsNoTracking()
            .Where(p => p.Id == id).Select(p => p.LinkSecretProtected).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(ciphertext)) return null;

        try
        {
            return LinkProtector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Failed to decrypt the link secret for custom provider {ProviderId}", id);
            throw new InvalidOperationException(
                $"The stored link secret for custom provider '{id}' could not be decrypted; generate a new one.", ex);
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
        CustomProviderEntity? entity = await context.CustomProviders.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null) return false;

        int inUse = await ConnectionCountAsync(context, id, ct);
        if (inUse > 0)
            throw new InvalidOperationException(
                $"Cannot delete this custom provider: {inUse} connection(s) still use it. Delete those connections first.");

        context.CustomProviders.Remove(entity);
        await context.SaveChangesAsync(ct);
        logger.LogInformation("Deleted custom provider {ProviderId}", id);
        return true;
    }

    // Few custom connections exist, so reading their configuration beats a JSON query here.
    private static async Task<int> ConnectionCountAsync(KnowledgeDbContext context, Guid providerId, CancellationToken ct)
    {
        var configs = await context.Connections.AsNoTracking()
            .Where(c => c.Provider == (int)ConnectionProvider.Custom)
            .Select(c => c.ConfigJson)
            .ToListAsync(ct);
        return configs.Count(c => CustomConnectionConfig.ProviderIdOf(c?.RootElement.GetRawText()) == providerId);
    }

    private static string ValidName(SaveCustomProviderRequest request)
    {
        string name = request.Name.Trim();
        if (name.Length is 0 or > 128)
            throw new ArgumentException("A custom provider's name must be 1-128 characters.", nameof(request));
        return name;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static CustomProvider ToModel(CustomProviderEntity e) => new(
        e.Id, e.Name, e.BaseUrl, e.TokenProtected is not null, e.MaxCheckBatch, e.SupportsAccessKeys, e.LinkUrl,
        e.VerifiedAt, e.CreatedAt, e.UpdatedAt, e.LinkSecretProtected is not null);
}
