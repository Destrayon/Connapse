using System.Net;
using Amazon;
using Amazon.S3Control;
using Amazon.S3Control.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Connapse.Core;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Creates S3 Access Grants with Connapse's own AWS identity.
/// </summary>
/// <remarks>
/// The write twin of <see cref="S3AccessGrantsReader"/>, built the same way and against the same
/// account. It reads the grantee's existing grants once and creates only what
/// <see cref="GrantPlanner"/> says is missing, so a rerun converges rather than duplicates.
/// </remarks>
public sealed class S3AccessGrantsWriter(
    ConnapseAwsCredentials credentials,
    IOptionsMonitor<IdentityCenterSettings> options) : IAccessGrantsWriter
{
    private string? accountId;

    /// <inheritdoc />
    public async Task<GrantWriteResult> GrantReadAsync(
        AccessGrantee grantee, string region,
        IReadOnlyList<string> locations, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantee.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        if (!options.CurrentValue.IsConfigured || locations.Count == 0)
            return GrantWriteResult.Nothing;

        var endpoint = RegionEndpoint.GetBySystemName(region);
        string account = await ResolveAccountIdAsync(endpoint, ct);

        using var client = new AmazonS3ControlClient(
            credentials, new AmazonS3ControlConfig { RegionEndpoint = endpoint });

        try
        {
            string? locationId = await FindRootLocationAsync(client, account, ct);
            if (locationId is null)
                return AllFailed(locations,
                    "No s3:// location is registered. Run the Access Grants setup step in Connapse first.",
                    accessDenied: false);

            IReadOnlyList<string> existing = await ListGranteeScopesAsync(client, account, grantee, ct);
            GrantPlan plan = GrantPlanner.Plan(locations, existing);

            return await CreateAsync(client, account, grantee, plan, locationId, ct);
        }
        catch (AmazonS3ControlException ex) when (IsAccessDenied(ex))
        {
            // The identity cannot even look up the location or existing grants. Report every
            // requested location as denied so the UI shows the CloudShell fallback rather than a
            // partial, confusing result.
            return AllFailed(locations, ex.Message, accessDenied: true);
        }
    }

    private async Task<GrantWriteResult> CreateAsync(
        AmazonS3ControlClient client, string account, AccessGrantee grantee,
        GrantPlan plan, string locationId, CancellationToken ct)
    {
        var created = new List<string>();
        var alreadyGranted = new List<string>(plan.AlreadyGranted);
        var failed = new List<GrantWriteFailure>();
        bool accessDenied = false;

        foreach (string subPrefix in plan.ToCreate)
        {
            try
            {
                await client.CreateAccessGrantAsync(new CreateAccessGrantRequest
                {
                    AccountId = account,
                    AccessGrantsLocationId = locationId,
                    AccessGrantsLocationConfiguration =
                        new AccessGrantsLocationConfiguration { S3SubPrefix = subPrefix },
                    Permission = Permission.READ,
                    Grantee = new Grantee
                    {
                        GranteeType = grantee.IsGroup
                            ? GranteeType.DIRECTORY_GROUP
                            : GranteeType.DIRECTORY_USER,
                        GranteeIdentifier = grantee.Id,
                    },
                    // Provenance: cleanup deletes only grants carrying this tag, so it can never
                    // remove a grant an administrator authored by hand over the same bucket.
                    Tags = [new Amazon.S3Control.Model.Tag
                    {
                        Key = GrantTags.ManagedKey, Value = GrantTags.ManagedValue,
                    }],
                }, ct);

                created.Add(subPrefix);
            }
            catch (AmazonS3ControlException ex) when (IsConflict(ex))
            {
                // The read-then-create race backstop: already there is success, not failure.
                alreadyGranted.Add(subPrefix);
            }
            catch (AmazonS3ControlException ex)
            {
                if (IsAccessDenied(ex))
                    accessDenied = true;

                // Keep going: one bad bucket must not hide the rest.
                failed.Add(new GrantWriteFailure(subPrefix, ex.Message));
            }
        }

        return new GrantWriteResult(created, alreadyGranted, failed, accessDenied);
    }

    private static async Task<string?> FindRootLocationAsync(
        AmazonS3ControlClient client, string account, CancellationToken ct)
    {
        var response = await client.ListAccessGrantsLocationsAsync(
            new ListAccessGrantsLocationsRequest { AccountId = account }, ct);

        return (response.AccessGrantsLocationsList ?? [])
            .FirstOrDefault(l => l.LocationScope == "s3://")
            ?.AccessGrantsLocationId;
    }

    private static async Task<IReadOnlyList<string>> ListGranteeScopesAsync(
        AmazonS3ControlClient client, string account, AccessGrantee grantee, CancellationToken ct)
    {
        List<string> scopes = [];
        string? nextToken = null;

        do
        {
            var response = await client.ListAccessGrantsAsync(
                new ListAccessGrantsRequest
                {
                    AccountId = account,
                    GranteeType = grantee.IsGroup
                        ? GranteeType.DIRECTORY_GROUP
                        : GranteeType.DIRECTORY_USER,
                    GranteeIdentifier = grantee.Id,
                    NextToken = nextToken,
                }, ct);

            // Null, not empty, when the account holds no grants — the AWS SDK leaves response
            // collections unset rather than initialising them.
            scopes.AddRange((response.AccessGrantsList ?? [])
                .Select(g => g.GrantScope)
                .Where(s => !string.IsNullOrWhiteSpace(s))!);

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return scopes;
    }

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

    private static GrantWriteResult AllFailed(
        IReadOnlyList<string> locations, string reason, bool accessDenied) =>
        new([], [], [.. locations.Select(l => new GrantWriteFailure(l, reason))], accessDenied);

    private static bool IsAccessDenied(AmazonS3ControlException ex) =>
        ex.StatusCode == HttpStatusCode.Forbidden
        || (ex.ErrorCode?.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool IsConflict(AmazonS3ControlException ex) =>
        ex.StatusCode == HttpStatusCode.Conflict
        || (ex.ErrorCode?.Contains("Conflict", StringComparison.OrdinalIgnoreCase) ?? false)
        || (ex.Message?.Contains("already", StringComparison.OrdinalIgnoreCase) ?? false);
}
