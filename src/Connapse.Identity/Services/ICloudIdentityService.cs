using Connapse.Core;

namespace Connapse.Identity.Services;

public interface ICloudIdentityService
{
    Task<CloudIdentityDto?> GetAsync(Guid userId, CloudProvider provider, CancellationToken ct = default);
    Task<IReadOnlyList<CloudIdentityDto>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<bool> DisconnectAsync(Guid userId, CloudProvider provider, CancellationToken ct = default);

    AzureConnectResult GetAzureConnectUrl(string baseUrl);
    Task<CloudIdentityDto> HandleAzureCallbackAsync(Guid userId, string code, string redirectUri, CancellationToken ct = default);

    Task<CloudIdentityConnectResult> ConnectAwsAsync(Guid userId, CancellationToken ct = default);
    bool IsRs256Enabled();
    bool IsAzureAdConfigured();
}

public record CloudIdentityConnectResult(bool Success, string? Error, CloudIdentityDto? Identity);
