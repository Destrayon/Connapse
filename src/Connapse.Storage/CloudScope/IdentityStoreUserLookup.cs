using Amazon;
using Amazon.IdentityStore;
using Amazon.IdentityStore.Model;
using Connapse.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Reads the IAM Identity Center directory with Connapse's own AWS identity.
/// </summary>
/// <remarks>
/// Needs <c>identitystore:GetUserId</c> and <c>identitystore:DescribeUser</c>, both read-only.
/// Neither is scoped to one user by the permission itself, which is stated on the setup page rather
/// than left for an administrator to find in a policy.
/// <para>
/// Returns null rather than throwing when the directory does not know somebody. A caller here is
/// deciding whether a link still resolves, and "no such user" is an answer rather than a fault. A
/// genuine fault — no permission, no network — is left to propagate, because treating it as "no
/// such user" would silently unlink everybody the first time a policy was wrong.
/// </para>
/// </remarks>
public sealed class IdentityStoreUserLookup(
    ConnapseAwsCredentials credentials,
    IOptionsMonitor<IdentityCenterSettings> options,
    ILogger<IdentityStoreUserLookup> logger) : IDirectoryUserLookup
{
    /// <inheritdoc />
    public async Task<string?> FindUserIdAsync(string userName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        var settings = options.CurrentValue;
        if (!settings.IsConfigured)
        {
            logger.LogWarning("Identity Center is not configured, so no directory user can be resolved");
            return null;
        }

        using var client = CreateClient(settings);

        try
        {
            var response = await client.GetUserIdAsync(
                new GetUserIdRequest
                {
                    IdentityStoreId = settings.IdentityStoreId,
                    AlternateIdentifier = new AlternateIdentifier
                    {
                        // userName and emails.value are the only two paths this accepts. The user
                        // name is the one the SAML assertion carries as its Subject.
                        UniqueAttribute = new UniqueAttribute
                        {
                            AttributePath = "userName",
                            AttributeValue = userName,
                        },
                    },
                },
                ct);

            return response.UserId;
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<DirectoryUser?> DescribeAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var settings = options.CurrentValue;
        if (!settings.IsConfigured)
            return null;

        using var client = CreateClient(settings);

        try
        {
            var response = await client.DescribeUserAsync(
                new DescribeUserRequest
                {
                    IdentityStoreId = settings.IdentityStoreId,
                    UserId = userId,
                },
                ct);

            // UserStatus is ENABLED or DISABLED. Absent is treated as enabled: the field is not
            // populated for every directory, and refusing everybody in that case would deny a
            // whole deployment on the strength of a missing optional attribute.
            bool enabled = response.UserStatus is null
                           || string.Equals(response.UserStatus, "ENABLED", StringComparison.OrdinalIgnoreCase);

            string? email = response.Emails?
                .FirstOrDefault(e => e.Primary ?? false)?.Value
                ?? response.Emails?.FirstOrDefault()?.Value;

            return new DirectoryUser(response.UserId, response.UserName, email, enabled);
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListGroupIdsAsync(
        string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var settings = options.CurrentValue;
        if (!settings.IsConfigured)
            return [];

        using var client = CreateClient(settings);

        List<string> groupIds = [];
        string? nextToken = null;

        // Paged deliberately rather than taking the first page. Somebody in more groups than one
        // page holds would otherwise silently lose the grants held by the rest of them, which
        // presents as a permissions bug with no error anywhere.
        do
        {
            var response = await client.ListGroupMembershipsForMemberAsync(
                new ListGroupMembershipsForMemberRequest
                {
                    IdentityStoreId = settings.IdentityStoreId,
                    MemberId = new MemberId { UserId = userId },
                    NextToken = nextToken,
                },
                ct);

            // Null, not empty, for a user in no groups -- the SDK leaves response
            // collections unset rather than initialising them.
            groupIds.AddRange((response.GroupMemberships ?? [])
                .Select(m => m.GroupId)
                .Where(id => !string.IsNullOrWhiteSpace(id)));

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return groupIds;
    }

    private AmazonIdentityStoreClient CreateClient(IdentityCenterSettings settings) =>
        new(credentials, RegionEndpoint.GetBySystemName(settings.Region));
}
