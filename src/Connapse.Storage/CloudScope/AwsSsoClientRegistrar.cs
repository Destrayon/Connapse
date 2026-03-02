using Amazon;
using Amazon.SSO;
using Amazon.SSO.Model;
using Amazon.SSOOIDC;
using Amazon.SSOOIDC.Model;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.CloudScope;

public class AwsSsoClientRegistrar(
    ISettingsStore settingsStore,
    ILogger<AwsSsoClientRegistrar> logger) : IAwsSsoClientRegistrar
{
    public async Task<AwsSsoSettings> EnsureRegisteredAsync(
        AwsSsoSettings settings,
        CancellationToken ct = default)
    {
        var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Credentials valid if ClientId exists and expiry is >10 minutes in the future
        if (!string.IsNullOrEmpty(settings.ClientId)
            && settings.ClientSecretExpiresAt.HasValue
            && settings.ClientSecretExpiresAt.Value > nowEpoch + 600)
        {
            return settings;
        }

        logger.LogInformation("AWS SSO client credentials missing or expiring — calling RegisterClient");

        using var oidcClient = CreateOidcClient(settings.Region);

        var response = await oidcClient.RegisterClientAsync(new RegisterClientRequest
        {
            ClientName = "Connapse",
            ClientType = "public",
            Scopes = ["sso:account:access"],
            GrantTypes = ["urn:ietf:params:oauth:grant-type:device_code", "refresh_token"],
            IssuerUrl = settings.IssuerUrl
        }, ct);

        var updated = new AwsSsoSettings
        {
            IssuerUrl = settings.IssuerUrl,
            Region = settings.Region,
            ClientId = response.ClientId,
            ClientSecret = response.ClientSecret,
            ClientSecretExpiresAt = response.ClientSecretExpiresAt
        };

        await settingsStore.SaveAsync("awssso", updated, ct);
        logger.LogInformation("AWS SSO client registered, expires at {ExpiresAt}",
            updated.ClientSecretExpiresAt.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(updated.ClientSecretExpiresAt.Value)
                : "unknown");

        return updated;
    }

    public async Task<AwsDeviceAuthorizationResult> StartDeviceAuthorizationAsync(
        AwsSsoSettings settings,
        CancellationToken ct = default)
    {
        using var oidcClient = CreateOidcClient(settings.Region);

        var response = await oidcClient.StartDeviceAuthorizationAsync(new StartDeviceAuthorizationRequest
        {
            ClientId = settings.ClientId,
            ClientSecret = settings.ClientSecret,
            StartUrl = settings.IssuerUrl
        }, ct);

        return new AwsDeviceAuthorizationResult(
            DeviceCode: response.DeviceCode,
            UserCode: response.UserCode,
            VerificationUri: response.VerificationUri,
            VerificationUriComplete: response.VerificationUriComplete,
            ExpiresInSeconds: response.ExpiresIn ?? 600,
            IntervalSeconds: response.Interval ?? 5);
    }

    public async Task<string?> PollForTokenAsync(
        AwsSsoSettings settings,
        string deviceCode,
        CancellationToken ct = default)
    {
        using var oidcClient = CreateOidcClient(settings.Region);

        try
        {
            var response = await oidcClient.CreateTokenAsync(new CreateTokenRequest
            {
                ClientId = settings.ClientId,
                ClientSecret = settings.ClientSecret,
                GrantType = "urn:ietf:params:oauth:grant-type:device_code",
                DeviceCode = deviceCode
            }, ct);

            return response.AccessToken;
        }
        catch (AuthorizationPendingException)
        {
            // User hasn't completed authentication yet
            return null;
        }
        catch (SlowDownException)
        {
            // Polling too fast — caller should increase interval
            return null;
        }
    }

    public async Task<AwsSsoUserInfo> ListUserAccountsAsync(
        AwsSsoSettings settings,
        string accessToken,
        CancellationToken ct = default)
    {
        using var ssoClient = new AmazonSSOClient(new AmazonSSOConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region)
        });

        var accounts = new List<AccountInfo>();
        string? nextToken = null;

        do
        {
            var response = await ssoClient.ListAccountsAsync(new ListAccountsRequest
            {
                AccessToken = accessToken,
                NextToken = nextToken,
                MaxResults = 100
            }, ct);

            accounts.AddRange(response.AccountList);
            nextToken = response.NextToken;
        } while (!string.IsNullOrEmpty(nextToken));

        var accountIds = string.Join(",", accounts.Select(a => a.AccountId));
        var primaryAccountId = accounts.FirstOrDefault()?.AccountId;
        var displayName = accounts.FirstOrDefault()?.AccountName;

        logger.LogInformation("AWS SSO ListAccounts returned {Count} account(s)", accounts.Count);

        return new AwsSsoUserInfo(accountIds, primaryAccountId, displayName);
    }

    private static AmazonSSOOIDCClient CreateOidcClient(string region) =>
        new(new AmazonSSOOIDCConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region)
        });
}
