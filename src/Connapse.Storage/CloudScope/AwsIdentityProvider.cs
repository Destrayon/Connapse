using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// AWS scope discovery. Checks whether the user's SSO-granted AWS accounts
/// include the account that hosts the S3 connector. PrincipalArn holds
/// comma-separated account IDs from IAM Identity Center SSO login.
/// </summary>
public class AwsIdentityProvider(ILogger<AwsIdentityProvider> logger) : ICloudIdentityProvider
{
    public CloudProvider Provider => CloudProvider.AWS;

    public async Task<CloudScopeResult> DiscoverScopesAsync(
        CloudIdentityData identityData,
        Container container,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(identityData.PrincipalArn))
        {
            return CloudScopeResult.Deny(
                "AWS SSO identity not linked. Connect your AWS account via Profile > Cloud Identities.");
        }

        // PrincipalArn holds comma-separated account IDs from SSO login
        var allowedAccountIds = identityData.PrincipalArn
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowedAccountIds.Count == 0)
        {
            return CloudScopeResult.Deny("AWS SSO identity has no linked accounts.");
        }

        try
        {
            // Check if the service's AWS account is in the user's permitted accounts
            using var stsClient = new AmazonSecurityTokenServiceClient();
            var identity = await stsClient.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
            var serviceAccountId = identity.Account;
            var maskedAccountId = MaskAccountId(serviceAccountId);

            if (!allowedAccountIds.Contains(serviceAccountId))
            {
                logger.LogInformation(
                    "AWS SSO user does not have access to service account ending in {MaskedAccount}",
                    maskedAccountId);
                return CloudScopeResult.Deny(
                    $"Your AWS SSO identity does not include access to the service's AWS account (****{maskedAccountId}).");
            }

            logger.LogInformation(
                "AWS SSO identity verified: account ending in {MaskedAccount} is in user's permitted accounts",
                maskedAccountId);

            return CloudScopeResult.FullAccess();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AWS identity verification failed");
            return CloudScopeResult.Deny($"AWS access verification failed: {ex.Message}");
        }
    }

    private static string MaskAccountId(string accountId) =>
        accountId.Length > 4 ? accountId[^4..] : accountId;
}
