using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Answers what a Connapse user may search in Azure Blob. Broad retrieve-then-verify: for any valid
/// enforcing identity (link present, not deprovisioned, directory not failed) this returns the single
/// broad <c>azblob://</c> match so every Azure candidate is retrieved by relevance; the Phase 4e
/// per-hit verifier tightens the result via RBAC coverage, tag conditions, and Gen2 ACLs. Mirrors
/// <see cref="AwsSearchScopeResolver"/> for the enforcement gate and link → deprovisioning gate, and
/// fails closed on every uncertain path.
/// </summary>
public sealed class AzureSearchScopeResolver(
    IAzureIdentityLinkReader links,
    IAzureDirectoryReader directory,
    IOptionsMonitor<AzureAdSignInSettings> azureAd,
    IOptionsMonitor<PermissionEnforcementSettings> enforcement,
    EnforcementMigration migration,
    IMemoryCache cache,
    ILogger<AzureSearchScopeResolver> logger) : ISearchScopeResolver
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    private const string KeyPrefix = "azure-scopes:";

    public async Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default)
    {
        switch (enforcement.CurrentValue.StateForAzure(azureAd.CurrentValue.IsConfigured, migration.Determined))
        {
            case EnforcementState.NotEnforcing:
                return SearchScopes.Unrestricted;
            case EnforcementState.EnforcingButUnusable:
                logger.LogError(
                    "Azure per-user permissions cannot be determined — Azure AD sign-in is incomplete or the startup migration did not complete; denying rather than widening");
                return SearchScopes.Failed;
        }

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
            logger.LogError(ex, "Could not resolve Azure search scopes; denying rather than widening");
            return SearchScopes.Failed;
        }

        if (resolved.Outcome is ScopeOutcome.Granted or ScopeOutcome.NoGrants)
            cache.Set(key, resolved, CacheLifetime);
        return resolved;
    }

    private async Task<SearchScopes> ResolveUncachedAsync(Guid userId, CancellationToken ct)
    {
        AzureIdentityRef? link = await links.GetLinkAsync(userId, ct);
        if (link is null)
            return SearchScopes.NoPrincipal;

        // Deprovisioning gate: a disabled/deleted Entra account is denied even if role assignments
        // still exist. A Failed identity lookup is an uncertain answer → deny.
        AzureIdentitySet identity = await directory.ResolveAsync(link, ct);
        if (identity.Outcome is AzureIdentityOutcome.Deprovisioned)
        {
            logger.LogInformation("A linked Entra identity is disabled or gone; denying");
            return SearchScopes.NoPrincipal;
        }
        if (identity.Outcome is AzureIdentityOutcome.Failed)
            return SearchScopes.Failed;

        // Broad retrieve-then-verify (§E amendment): a valid enforcing identity retrieves EVERY Azure
        // candidate by relevance; the post-retrieval verifier (Phase 4e) tightens per hit via RBAC
        // coverage, tag conditions, and Gen2 file-ACL + ancestor traverse. Narrowing here (e.g. to RBAC
        // prefixes) would drop ACL-only "Case C" files, which can live in any container.
        return SearchScopes.Of([new GrantMatch("azblob://", IsExact: false)]);
    }
}
