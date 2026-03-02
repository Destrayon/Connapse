using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
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
    IOptionsMonitor<AwsSsoSettings> awsSsoOptions,
    IAwsSsoClientRegistrar awsSsoRegistrar,
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

    // --- Azure OAuth2 ---

    public AzureConnectResult GetAzureConnectUrl(string baseUrl)
    {
        var settings = azureAdOptions.CurrentValue;
        var state = GenerateState();
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var redirectUri = $"{baseUrl.TrimEnd('/')}/api/v1/auth/cloud/azure/callback";

        var authorizeUrl = $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/authorize" +
            $"?client_id={Uri.EscapeDataString(settings.ClientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString("openid profile")}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&response_mode=query" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256";

        return new AzureConnectResult(authorizeUrl, state, codeVerifier);
    }

    public async Task<CloudIdentityDto> HandleAzureCallbackAsync(Guid userId, string code, string codeVerifier, string redirectUri, CancellationToken ct)
    {
        var settings = azureAdOptions.CurrentValue;

        var tokenEndpoint = $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/token";
        var httpClient = httpClientFactory.CreateClient();

        var tokenParams = new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["scope"] = "openid profile",
            ["code_verifier"] = codeVerifier
        };

        if (!string.IsNullOrEmpty(settings.ClientSecret))
            tokenParams["client_secret"] = settings.ClientSecret;

        var tokenRequest = new FormUrlEncodedContent(tokenParams);

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

    // --- AWS SSO (Device Authorization Flow) ---

    public async Task<AwsDeviceAuthStartResult> StartAwsDeviceAuthAsync(CancellationToken ct)
    {
        var settings = awsSsoOptions.CurrentValue;
        settings = await awsSsoRegistrar.EnsureRegisteredAsync(settings, ct);

        var result = await awsSsoRegistrar.StartDeviceAuthorizationAsync(settings, ct);

        return new AwsDeviceAuthStartResult(
            UserCode: result.UserCode,
            VerificationUri: result.VerificationUri,
            VerificationUriComplete: result.VerificationUriComplete,
            DeviceCode: result.DeviceCode,
            ExpiresInSeconds: result.ExpiresInSeconds,
            IntervalSeconds: result.IntervalSeconds);
    }

    public async Task<CloudIdentityDto?> PollAwsDeviceAuthAsync(
        Guid userId, string deviceCode, CancellationToken ct)
    {
        var settings = awsSsoOptions.CurrentValue;
        settings = await awsSsoRegistrar.EnsureRegisteredAsync(settings, ct);

        var accessToken = await awsSsoRegistrar.PollForTokenAsync(settings, deviceCode, ct);
        if (accessToken is null)
            return null; // Still pending

        var userInfo = await awsSsoRegistrar.ListUserAccountsAsync(settings, accessToken, ct);

        var identityData = new CloudIdentityData(
            PrincipalArn: userInfo.AccountIds,
            AccountId: userInfo.PrimaryAccountId,
            ObjectId: null,
            TenantId: null,
            DisplayName: userInfo.DisplayName);

        var dto = await StoreIdentityAsync(userId, CloudProvider.AWS, identityData, ct);
        logger.LogInformation("AWS SSO identity linked for user {UserId}: {AccountIds}", userId, userInfo.AccountIds);

        return dto;
    }

    public bool IsAwsSsoConfigured()
    {
        var settings = awsSsoOptions.CurrentValue;
        return !string.IsNullOrEmpty(settings.IssuerUrl) && !string.IsNullOrEmpty(settings.Region);
    }

    public bool IsAzureAdConfigured()
    {
        var settings = azureAdOptions.CurrentValue;
        return !string.IsNullOrEmpty(settings.ClientId) && !string.IsNullOrEmpty(settings.TenantId);
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

    // --- Helpers ---

    private static string GenerateState() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string GenerateCodeVerifier() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string ComputeCodeChallenge(string codeVerifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
