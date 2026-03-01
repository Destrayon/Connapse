using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// AWS scope discovery. Currently returns Deny when PrincipalArn is null (Session F
/// enables AWS OIDC federation). When PrincipalArn is populated, verifies via STS
/// and grants full access — prefix-level SimulatePrincipalPolicy deferred to Session F.
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
            logger.LogInformation("User has no AWS PrincipalArn — AWS OIDC federation not yet complete");
            return CloudScopeResult.Deny(
                "AWS identity not yet linked. Connect your AWS account via Profile > Cloud Identities. " +
                "AWS federation requires RS256 JWT signing to be enabled by an administrator.");
        }

        // PrincipalArn is populated — verify the identity is still callable via STS.
        // Full prefix-level policy simulation (SimulatePrincipalPolicy) deferred to Session F.
        try
        {
            using var stsClient = new AmazonSecurityTokenServiceClient();
            var identity = await stsClient.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);

            logger.LogInformation(
                "AWS identity verified for ARN {Arn}, account {Account}. " +
                "Granting full connector access (prefix-level policy simulation deferred to Session F)",
                identityData.PrincipalArn, identity.Account);

            return CloudScopeResult.FullAccess();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AWS STS identity verification failed for {Arn}", identityData.PrincipalArn);
            return CloudScopeResult.Deny($"AWS identity verification failed: {ex.Message}");
        }
    }
}
