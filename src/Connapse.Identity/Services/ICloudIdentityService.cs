using Connapse.Core;

namespace Connapse.Identity.Services;

public interface ICloudIdentityService
{
    Task<CloudIdentityDto?> GetAsync(Guid userId, CloudProvider provider, CancellationToken ct = default);
    Task<IReadOnlyList<CloudIdentityDto>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<bool> DisconnectAsync(Guid userId, CloudProvider provider, CancellationToken ct = default);

    AzureConnectResult GetAzureConnectUrl(string baseUrl);
    Task<CloudIdentityDto> HandleAzureCallbackAsync(Guid userId, string code, string codeVerifier, string redirectUri, CancellationToken ct = default);

    Task<AwsDeviceAuthStartResult> StartAwsDeviceAuthAsync(CancellationToken ct = default);
    Task<CloudIdentityDto?> PollAwsDeviceAuthAsync(Guid userId, string deviceCode, CancellationToken ct = default);

    bool IsAwsSsoConfigured();
    bool IsAzureAdConfigured();
}
