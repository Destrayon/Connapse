using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Connapse.Core;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Stores;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Identity.Services;

public class CloudIdentityService(
    ICloudIdentityStore store,
    IDataProtectionProvider dataProtection,
    IOptionsMonitor<AzureAdSettings> azureAdOptions,
    IOptionsMonitor<JwtSettings> jwtOptions,
    IHttpClientFactory httpClientFactory,
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

    public AzureConnectResult GetAzureConnectUrl(string baseUrl)
    {
        var settings = azureAdOptions.CurrentValue;
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var redirectUri = string.IsNullOrEmpty(settings.RedirectUri)
            ? $"{baseUrl.TrimEnd('/')}/api/v1/auth/cloud/azure/callback"
            : settings.RedirectUri;

        var authorizeUrl = $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/authorize" +
            $"?client_id={Uri.EscapeDataString(settings.ClientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString("openid profile")}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&response_mode=query";

        return new AzureConnectResult(authorizeUrl, state);
    }

    public async Task<CloudIdentityDto> HandleAzureCallbackAsync(Guid userId, string code, string redirectUri, CancellationToken ct)
    {
        var settings = azureAdOptions.CurrentValue;

        // Exchange authorization code for tokens
        var tokenEndpoint = $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/token";
        var httpClient = httpClientFactory.CreateClient();

        var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["scope"] = "openid profile"
        });

        var response = await httpClient.PostAsync(tokenEndpoint, tokenRequest, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Azure token exchange failed: {StatusCode} {Body}", response.StatusCode, responseBody);
            throw new InvalidOperationException($"Azure AD token exchange failed: {response.StatusCode}");
        }

        var tokenResponse = JsonSerializer.Deserialize<JsonElement>(responseBody);
        var idToken = tokenResponse.GetProperty("id_token").GetString()
            ?? throw new InvalidOperationException("Azure AD response missing id_token");

        // Decode ID token to extract identity claims (no signature validation needed —
        // we received the token directly from Microsoft's token endpoint over HTTPS)
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(idToken);

        var objectId = jwt.Claims.FirstOrDefault(c => c.Type == "oid")?.Value
            ?? throw new InvalidOperationException("Azure ID token missing 'oid' claim");
        var tenantId = jwt.Claims.FirstOrDefault(c => c.Type == "tid")?.Value
            ?? throw new InvalidOperationException("Azure ID token missing 'tid' claim");
        var displayName = jwt.Claims.FirstOrDefault(c => c.Type == "name")?.Value;

        var identityData = new CloudIdentityData(
            PrincipalArn: null,
            AccountId: null,
            ObjectId: objectId,
            TenantId: tenantId,
            DisplayName: displayName);

        return await StoreIdentityAsync(userId, CloudProvider.Azure, identityData, ct);
    }

    public Task<CloudIdentityConnectResult> ConnectAwsAsync(Guid userId, CancellationToken ct)
    {
        if (!IsRs256Enabled())
        {
            return Task.FromResult(new CloudIdentityConnectResult(
                false,
                "RS256 JWT signing is not enabled. AWS OIDC federation requires RS256. " +
                "An admin must enable RS256 in Settings > Security before AWS identity linking is available.",
                null));
        }

        // AWS OIDC federation will be implemented in Session F when RS256 is available.
        // The STS:AssumeRoleWithWebIdentity call requires the user's JWT to be RS256-signed
        // so that AWS can verify it against Connapse's JWKS endpoint.
        return Task.FromResult(new CloudIdentityConnectResult(
            false,
            "AWS OIDC federation is not yet implemented. It will be available once RS256 signing is enabled.",
            null));
    }

    public bool IsRs256Enabled()
    {
        var settings = jwtOptions.CurrentValue;
        return !string.IsNullOrEmpty(settings.SigningAlgorithm) &&
               settings.SigningAlgorithm.Equals("RS256", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsAzureAdConfigured()
    {
        var settings = azureAdOptions.CurrentValue;
        return !string.IsNullOrEmpty(settings.ClientId) && !string.IsNullOrEmpty(settings.TenantId);
    }

    private async Task<CloudIdentityDto> StoreIdentityAsync(
        Guid userId, CloudProvider provider, CloudIdentityData data, CancellationToken ct)
    {
        // Encrypt identity data before storing
        var plaintext = JsonSerializer.Serialize(data, JsonOptions);
        var encrypted = Protector.Protect(plaintext);

        // Upsert: delete existing if present, then create new
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
