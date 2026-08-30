using Amazon;
using Amazon.S3Control;
using Amazon.S3Control.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Connapse.Core;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Reads S3 Access Grants with Connapse's own AWS identity.
/// </summary>
/// <remarks>
/// Needs <c>s3:ListAccessGrants</c>, which AWS does not scope to one grantee — see
/// <see cref="IAccessGrantsReader"/> for why that is a deliberate trade and where an administrator
/// is told about it.
/// <para>
/// Every page is read. A grantee with more grants than one page holds would otherwise silently lose
/// the rest, and the failure would look like a person unable to see documents they were granted
/// rather than like a bug here.
/// </para>
/// </remarks>
public sealed class S3AccessGrantsReader(
    ConnapseAwsCredentials credentials,
    IOptionsMonitor<IdentityCenterSettings> options) : IAccessGrantsReader
{
    /// <summary>
    /// The account the Access Grants instance belongs to, resolved once.
    /// </summary>
    /// <remarks>
    /// Asked of STS rather than configured, so it cannot disagree with the identity actually making
    /// the call: an instance belongs to exactly one account, and naming a different one fails with
    /// an error about a missing instance rather than about the account, which sends whoever reads it
    /// to the wrong place entirely.
    /// <para>
    /// Cached for the process. It is a property of the credential, and the credential is a
    /// singleton — re-asking on every search would add a round trip to every query to learn
    /// something that cannot change without a restart.
    /// </para>
    /// </remarks>
    private string? accountId;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccessGrantRecord>> ListForGranteeAsync(
        AccessGrantee grantee, string region, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantee.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        if (!options.CurrentValue.IsConfigured)
            return [];

        var endpoint = RegionEndpoint.GetBySystemName(region);
        string account = await ResolveAccountIdAsync(endpoint, ct);

        using var client = new AmazonS3ControlClient(
            credentials, new AmazonS3ControlConfig { RegionEndpoint = endpoint });

        List<AccessGrantRecord> grants = [];
        string? nextToken = null;

        do
        {
            var response = await client.ListAccessGrantsAsync(
                new ListAccessGrantsRequest
                {
                    AccountId = account,
                    GranteeType = grantee.IsGroup ? GranteeType.DIRECTORY_GROUP : GranteeType.DIRECTORY_USER,
                    GranteeIdentifier = grantee.Id,
                    NextToken = nextToken,
                },
                ct);

            // Null, not empty, when the account holds no grants. The AWS SDK for .NET
            // leaves response collections unset rather than initialising them, so the
            // ordinary state of a fresh Access Grants instance threw here and the
            // resolver reported an outage -- meaning NoGrants was never reachable.
            foreach (var grant in response.AccessGrantsList ?? [])
            {
                if (string.IsNullOrWhiteSpace(grant.GrantScope))
                    continue;

                grants.Add(new AccessGrantRecord(
                    grant.GrantScope,
                    IsObjectScope: LooksLikeObjectGrant(grant.GrantScope),
                    ApplicationArn: grant.ApplicationArn));
            }

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return grants;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAllScopesAsync(
        string region, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        if (!options.CurrentValue.IsConfigured)
            return [];

        var endpoint = RegionEndpoint.GetBySystemName(region);
        string account = await ResolveAccountIdAsync(endpoint, ct);

        using var client = new AmazonS3ControlClient(
            credentials, new AmazonS3ControlConfig { RegionEndpoint = endpoint });

        List<string> scopes = [];
        string? nextToken = null;

        do
        {
            // No grantee filter: the question is whether anybody is granted anything here, not
            // what one person may read.
            var response = await client.ListAccessGrantsAsync(
                new ListAccessGrantsRequest { AccountId = account, NextToken = nextToken }, ct);

            // Null, not empty, when the account holds no grants — the AWS SDK leaves response
            // collections unset rather than initialising them.
            scopes.AddRange((response.AccessGrantsList ?? [])
                .Select(g => g.GrantScope)
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return scopes;
    }

    /// <summary>
    /// Whether a grant names one object rather than everything beneath a prefix.
    /// </summary>
    /// <remarks>
    /// Inferred from the scope's shape, because AWS does not report it. <c>S3PrefixType</c> is an
    /// input to <c>CreateAccessGrant</c> and is returned by nothing — not <c>ListAccessGrants</c>,
    /// not <c>GetAccessGrant</c> — so the trailing asterisk AWS puts on a prefix grant is the only
    /// signal there is.
    /// <para>
    /// The default leans the safe way on purpose. Reading a prefix grant as exact under-permits: a
    /// person misses documents they were granted, notices, and says so. Reading an object grant as a
    /// prefix over-permits, and a grant for <c>report.pdf</c> would also admit
    /// <c>report.pdf.bak</c> — which nobody notices, because nothing looks wrong.
    /// </para>
    /// </remarks>
    private static bool LooksLikeObjectGrant(string grantScope) =>
        !grantScope.TrimEnd().EndsWith('*');

    private async Task<string> ResolveAccountIdAsync(RegionEndpoint region, CancellationToken ct)
    {
        if (accountId is { Length: > 0 })
            return accountId;

        using var sts = new AmazonSecurityTokenServiceClient(
            credentials, new AmazonSecurityTokenServiceConfig { RegionEndpoint = region });

        var identity = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
        accountId = identity.Account;
        return accountId;
    }
}
