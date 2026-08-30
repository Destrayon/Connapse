using Connapse.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Answers what a Connapse user may search, from the S3 access grants held against the IAM Identity
/// Center user they proved they were.
/// </summary>
/// <remarks>
/// Resolved with Connapse's own AWS identity rather than one belonging to the person searching.
/// Nothing here holds, refreshes or presents a per-user credential; the link records who somebody
/// is, and this asks AWS what that person has been granted.
/// <para>
/// <b>Fails closed, always.</b> Every path that cannot produce a confident answer returns
/// <see cref="SearchScopes.Failed"/> rather than <see cref="SearchScopes.Unrestricted"/>. Those two
/// are opposites and the difference is the whole feature: unrestricted is "this deployment does not
/// filter", and returning it because a lookup threw would turn one AWS outage into a corpus-wide
/// disclosure.
/// </para>
/// </remarks>
public sealed class AwsSearchScopeResolver(
    IAwsIdentityLinkReader links,
    IDirectoryUserLookup directoryUsers,
    IAccessGrantsReader accessGrants,
    IAwsGrantRegions grantRegions,
    IOptionsMonitor<SamlSignInSettings> samlSignIn,
    IMemoryCache cache,
    ILogger<AwsSearchScopeResolver> logger) : ISearchScopeResolver
{
    /// <summary>How long a resolved answer is reused before AWS is asked again.</summary>
    /// <remarks>
    /// Resolving costs three or four AWS calls, and a search should not. Sixty seconds is chosen so
    /// that the cache lifetime <i>is</i> the revocation delay and is short enough to state plainly:
    /// removing a grant, disabling a user or deleting one takes effect within a minute. That is
    /// already far better than the design this replaced, where an identity-enhanced role session
    /// could outlive a revocation by up to twelve hours.
    /// </remarks>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    private const string KeyPrefix = "aws-scopes:";

    /// <inheritdoc />
    public async Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default)
    {
        // The one legitimate use of Unrestricted, and the reason it exists as a distinct outcome.
        // A deployment that has not set per-user permissions up is not filtering at all, and
        // denying here would leave every existing installation unable to search anything the moment
        // it upgraded. Filtering is opt-in; this is the opt.
        if (!samlSignIn.CurrentValue.IsConfigured)
            return SearchScopes.Unrestricted;

        // Not a person — an unauthenticated caller, or one this deployment could not name. Nothing
        // to resolve against, and guessing would be the disclosure this class exists to prevent.
        if (userId is null)
            return SearchScopes.NoPrincipal;

        string key = KeyPrefix + userId.Value;
        if (cache.TryGetValue(key, out SearchScopes? cached) && cached is not null)
            return cached;

        SearchScopes resolved;
        try
        {
            resolved = await ResolveUncachedAsync(userId.Value, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately broad. Every AWS failure mode here — no permission, throttled,
            // unreachable, a malformed grant scope — has the same correct answer, and the one
            // outcome that must never follow from an exception is an unfiltered search.
            logger.LogError(ex, "Could not resolve AWS search scopes; denying rather than widening");
            return SearchScopes.Failed;
        }

        // Only a confident answer is cached. Caching a failure would hold a denial in place for a
        // minute after whatever caused it was fixed, and the fix is usually someone watching.
        if (resolved.Outcome is ScopeOutcome.Granted or ScopeOutcome.NoGrants)
            cache.Set(key, resolved, CacheLifetime);

        return resolved;
    }

    private async Task<SearchScopes> ResolveUncachedAsync(Guid userId, CancellationToken ct)
    {
        string? directoryUserId = await links.GetDirectoryUserIdAsync(userId, ct);

        // Connected nobody, or connected before the directory id was recorded. Either way there is
        // no identity to resolve grants against, and they have to connect again.
        if (string.IsNullOrWhiteSpace(directoryUserId))
            return SearchScopes.NoPrincipal;

        // Revocation is detected here rather than awaited. Connapse holds no credential that could
        // expire when somebody is deprovisioned, so nothing lapses on its own — the only thing that
        // stops a disabled person searching is this call noticing.
        var user = await directoryUsers.DescribeAsync(directoryUserId, ct);
        if (user is null)
        {
            logger.LogInformation("A linked directory user no longer exists; denying");
            return SearchScopes.NoPrincipal;
        }

        if (!user.Enabled)
        {
            logger.LogInformation("A linked directory user is disabled; denying");
            return SearchScopes.NoPrincipal;
        }

        // Every region that has buckets, not just the directory's. A grant is created against
        // the Access Grants instance in the bucket's region, so a deployment with data in two
        // regions keeps its grants in two instances — and asking only one hides documents the
        // person was granted, silently, with nothing to notice.
        //
        // Any region that throws takes the whole resolution down with it, by design: the catch
        // above turns that into a denial. Unioning whatever succeeded would hand somebody a
        // partial answer that looks complete, which is the failure this feature exists to prevent.
        var regions = await grantRegions.ListAsync(ct);

        var grantees = new List<AccessGrantee> { new(false, directoryUserId) };

        // Group-held grants are invisible to a user-grantee filter, so each group is asked for
        // separately. This is the expansion AWS performs inside ListCallerAccessGrants and that
        // Connapse takes on in exchange for not holding anybody's credential.
        foreach (string groupId in await directoryUsers.ListGroupIdsAsync(directoryUserId, ct))
            grantees.Add(new AccessGrantee(true, groupId));

        List<AccessGrantRecord> records = [];

        foreach (string region in regions)
        {
            foreach (var grantee in grantees)
                records.AddRange(await accessGrants.ListForGranteeAsync(grantee, region, ct));
        }

        var matches = records
            .Where(IsExercisableHere)
            .Select(r => GrantScope.Parse(r.GrantScope, r.IsObjectScope))
            .DistinctBy(m => (m.Value, m.IsExact))
            .ToList();

        return SearchScopes.Of(matches);
    }

    /// <summary>
    /// Whether a grant is one Connapse should honour, given it presents no application identity.
    /// </summary>
    /// <remarks>
    /// A grant carrying an application ARN may only be exercised through that application, and
    /// Connapse is not one — it never calls <c>GetDataAccess</c>, so it has no application identity
    /// to present. Honouring such a grant would show somebody documents AWS would refuse them
    /// everywhere else.
    /// <para>
    /// This is a convention Connapse respects by reading the field, not a rule STS enforces on it.
    /// Filtering the query by application instead would be worse in the other direction: it would
    /// drop every grant that names no application, which is the ordinary case.
    /// </para>
    /// </remarks>
    private static bool IsExercisableHere(AccessGrantRecord grant) =>
        string.IsNullOrWhiteSpace(grant.ApplicationArn)
        || string.Equals(grant.ApplicationArn, "NA", StringComparison.OrdinalIgnoreCase)
        || string.Equals(grant.ApplicationArn, "ALL", StringComparison.OrdinalIgnoreCase);
}
