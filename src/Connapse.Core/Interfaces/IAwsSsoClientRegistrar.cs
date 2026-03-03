namespace Connapse.Core.Interfaces;

public record AwsSsoUserInfo(
    string AccountIds,
    string? PrimaryAccountId,
    string? DisplayName);

public record AwsDeviceAuthorizationResult(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresInSeconds,
    int IntervalSeconds);

public interface IAwsSsoClientRegistrar
{
    /// <summary>
    /// Ensures the OAuth2 client is registered with IAM Identity Center.
    /// Calls RegisterClient if credentials are missing or expired.
    /// </summary>
    Task<AwsSsoSettings> EnsureRegisteredAsync(
        AwsSsoSettings settings,
        CancellationToken ct = default);

    /// <summary>
    /// Starts the device authorization flow. Returns a user code and verification URL
    /// that the user must visit to authenticate.
    /// </summary>
    Task<AwsDeviceAuthorizationResult> StartDeviceAuthorizationAsync(
        AwsSsoSettings settings,
        CancellationToken ct = default);

    /// <summary>
    /// Polls for the device authorization token. Returns the SSO access token once
    /// the user completes authentication, or null if still pending.
    /// Throws on expiry or denial.
    /// </summary>
    Task<string?> PollForTokenAsync(
        AwsSsoSettings settings,
        string deviceCode,
        CancellationToken ct = default);

    /// <summary>
    /// Uses an SSO access token to discover the user's permitted AWS accounts.
    /// </summary>
    Task<AwsSsoUserInfo> ListUserAccountsAsync(
        AwsSsoSettings settings,
        string accessToken,
        CancellationToken ct = default);
}
