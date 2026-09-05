using System.Text.Json;
using Connapse.Core;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Stores;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Connapse.Identity.Services;

public class CloudIdentityService(
    ICloudIdentityStore store,
    IDataProtectionProvider dataProtection,
    ILogger<CloudIdentityService> logger) : ICloudIdentityService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private IDataProtector Protector => dataProtection.CreateProtector("CloudIdentity.v1");

    public async Task<CloudIdentityDto?> GetAsync(Guid userId, CloudProvider provider, CancellationToken ct)
    {
        var entity = await store.GetByUserAndProviderAsync(userId, provider, ct);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<IReadOnlyList<CloudIdentityDto>> ListAsync(Guid userId, CancellationToken ct)
    {
        var entities = await store.ListByUserAsync(userId, ct);
        return entities.Select(ToDto).ToList();
    }

    public async Task<bool> DisconnectAsync(Guid userId, CloudProvider provider, CancellationToken ct)
    {
        var result = await store.DeleteAsync(userId, provider, ct);
        if (result)
            logger.LogInformation("User {UserId} disconnected {Provider} cloud identity", userId, provider);
        return result;
    }

    // --- Storage ---

    private async Task<CloudIdentityDto> StoreIdentityAsync(
        Guid userId, CloudProvider provider, CloudIdentityData data, CancellationToken ct)
    {
        var plaintext = JsonSerializer.Serialize(data, JsonOptions);
        var encrypted = Protector.Protect(plaintext);

        var existing = await store.GetByUserAndProviderAsync(userId, provider, ct);
        if (existing is not null)
            await store.DeleteAsync(userId, provider, ct);

        var entity = new UserCloudIdentityEntity
        {
            UserId = userId,
            Provider = provider,
            IdentityDataJson = encrypted
        };

        var created = await store.CreateAsync(entity, ct);
        logger.LogInformation("Stored {Provider} cloud identity for user {UserId}", provider, userId);

        return new CloudIdentityDto(created.Id, provider, data, created.CreatedAt, created.LastUsedAt);
    }

    private CloudIdentityDto ToDto(UserCloudIdentityEntity entity)
    {
        CloudIdentityData data;
        try
        {
            var decrypted = Protector.Unprotect(entity.IdentityDataJson);
            data = JsonSerializer.Deserialize<CloudIdentityData>(decrypted, JsonOptions)
                ?? new CloudIdentityData(null, null, null, null, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to decrypt cloud identity {Id} for user {UserId}", entity.Id, entity.UserId);
            data = new CloudIdentityData(null, null, null, null, null);
        }

        return new CloudIdentityDto(entity.Id, entity.Provider, data, entity.CreatedAt, entity.LastUsedAt);
    }
}
