using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Answers what a Connapse user may search in Azure Blob, from the Storage-Blob-Data role
/// assignments held against the Entra identity they linked. Flat accounts only: the RBAC prefix set
/// is exact and complete. Mirrors <see cref="AwsSearchScopeResolver"/> — enforcement gate first,
/// then link → deprovisioning gate → RBAC — and fails closed on every uncertain path.
/// </summary>
public sealed class AzureSearchScopeResolver(
    IAzureIdentityLinkReader links,
    IAzureDirectoryReader directory,
    IAzureRbacReader rbac,
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
        switch (enforcement.CurrentValue.StateFor(azureAd.CurrentValue.IsConfigured, migration.Determined))
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

        AzureRbacScopes scopes = await rbac.ResolveAsync(link.ObjectId, ct);
        if (scopes.Outcome is RbacOutcome.Failed)
            return SearchScopes.Failed;

        // Flat accounts: RBAC readable prefixes are the answer. Tag-conditioned residue and Gen2
        // ACLs are NOT admitted here — they require Phase 4e's live per-hit verifier; omitting them
        // is a temporary under-grant on the epic branch that 4e closes before the epic reaches main.
        var matches = scopes.ReadablePrefixes
            .Select(s => new GrantMatch(s.Prefix, IsExact: false))
            .ToList();

        return SearchScopes.Of(matches);
    }
}
