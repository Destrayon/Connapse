using System.Collections.Concurrent;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Live per-hit Azure permission verify (Phase 4e). Passes non-Azure hits untouched; for each
/// azblob hit, admits it only if covered by an RBAC prefix (in-memory), or a matching blob-tag
/// condition, or the file's own ACL grants Read AND every ancestor directory grants traverse-X
/// (4d). Fails closed on every uncertain read. Preserves rank order. When actually enforcing,
/// caps the result at topK (backfilling from lower-ranked survivors); when not enforcing, returns
/// every candidate unchanged so the pipeline's own final cap sees the untouched pool.
/// </summary>
public sealed class AzureSearchResultVerifier(
    IDocumentStore documents,
    IAzureIdentityLinkReader links,
    IAzureDirectoryReader directory,
    IAzureRbacReader rbac,
    IGen2FileAclReader fileAcl,
    IBlobTagReader blobTags,
    AncestorTraverseResolver traverse,
    IOptionsMonitor<AzureAdSignInSettings> azureAd,
    IOptionsMonitor<PermissionEnforcementSettings> enforcement,
    EnforcementMigration migration,
    IOptions<AzureVerifierSettings> settings,
    ILogger<AzureSearchResultVerifier> logger) : ISearchResultVerifier
{
    // Over-fetch only pays off when VerifyAsync will actually drop hits, which happens only in the
    // Enforcing state below. NotEnforcing and EnforcingButUnusable never call into the per-hit
    // logic that needs backfill material (NotEnforcing passes everything; EnforcingButUnusable
    // drops every azblob hit outright, and backfill can't rescue a class that's dropped wholesale).
    // So AWS-only, non-cloud, and configured-but-not-yet-enforcing Azure deployments never pay the
    // over-fetch cost even though this verifier is always registered.
    public int CandidateMultiplier =>
        enforcement.CurrentValue.StateForAzure(azureAd.CurrentValue.IsConfigured, migration.Determined)
            == EnforcementState.Enforcing
            ? Math.Max(1, settings.Value.CandidateMultiplier)
            : 1;

    public async Task<IReadOnlyList<SearchHit>> VerifyAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default)
    {
        // No Azure enforcement → the resolver did not broaden; nothing to tighten. Return every
        // candidate unchanged (not capped at topK) — the pipeline's own final Take(topK) applies
        // the cap, so AutoCut still sees the full pool exactly as it would with no verifier at all.
        // A resolve/verify state flip is benign: off→NotEnforcing returns unfiltered (correct for
        // not-enforcing); on→Enforcing filters (fail-closed). Neither over-grants.
        EnforcementState state = enforcement.CurrentValue.StateForAzure(
            azureAd.CurrentValue.IsConfigured, migration.Determined);
        if (state == EnforcementState.NotEnforcing)
            return rankedCandidates;

        // Map hits to their governing URIs (one batched query).
        IReadOnlyDictionary<string, string?> uris =
            await documents.GetResourceUrisAsync(rankedCandidates.Select(h => h.DocumentId).ToList(), ct);

        // Enforcing → resolve identity. EnforcingButUnusable ("searches deny") → no context, so every
        // azblob hit is dropped (and non-cloud still passes), matching the resolver's SearchScopes.Failed.
        AzureContext? azure = state == EnforcementState.Enforcing
            ? await ResolveContextAsync(userId, ct)
            : null;

        // Verify azblob hits concurrently (bounded); non-azblob pass; decision keyed by index so we
        // can re-assemble in rank order.
        var verdicts = new ConcurrentDictionary<int, bool>();
        using var gate = new SemaphoreSlim(Math.Max(1, settings.Value.MaxParallelism));

        await Task.WhenAll(rankedCandidates.Select(async (hit, i) =>
        {
            if (!uris.TryGetValue(hit.DocumentId, out string? uri))
            {
                verdicts[i] = false; // document row not found (deleted/dangling chunk) → fail closed
                return;
            }
            if (uri is null || !AzblobUri.IsAzblob(uri))
            {
                verdicts[i] = true; // non-cloud (null) or non-Azure (s3://) → pass untouched
                return;
            }
            if (!AzblobUri.TryParse(uri, out Gen2Path path))
            {
                verdicts[i] = false; // azblob scheme but unparseable → fail closed, never bypass
                return;
            }
            if (azure is null)
            {
                verdicts[i] = false; // enforcing but no usable identity → drop azblob
                return;
            }
            await gate.WaitAsync(ct);
            try { verdicts[i] = await AdmitAzureHitAsync(uri, path, azure, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Azure hit verify failed; dropping"); verdicts[i] = false; }
            finally { gate.Release(); }
        }));

        var survivors = new List<SearchHit>(topK);
        for (int i = 0; i < rankedCandidates.Count && survivors.Count < topK; i++)
            if (verdicts.GetValueOrDefault(i)) survivors.Add(rankedCandidates[i]);
        return survivors;
    }

    private async Task<bool> AdmitAzureHitAsync(string uri, Gen2Path path, AzureContext azure, CancellationToken ct)
    {
        // 0. Deny assignments outrank every grant (RBAC and ACL). A hit under any applicable deny scope is
        //    blocked by Azure regardless of ACLs, so drop it before any permissive check.
        if (azure.DeniedPrefixes.Any(d => uri.StartsWith(d, StringComparison.Ordinal)))
            return false;

        // 1. RBAC read supersedes ACLs — covered by any readable prefix → pass, no live call.
        // Prefix match is safe because resolver-minted prefixes are account/container-terminated with '/'
        // (or the bare azblob:// wildcard); it never partial-matches a sibling like azblob://acct/docs-secret/.
        if (azure.ReadablePrefixes.Any(p => uri.StartsWith(p, StringComparison.Ordinal)))
            return true;

        // 2. Tag-conditioned residue → live tag verify. A MATCH grants (RBAC-ABAC supersedes ACLs). A
        //    non-match or unreadable tags does NOT drop: this assignment simply doesn't grant, and the file
        //    may still be readable via its own ACL (Azure evaluates ACLs when ABAC does not grant), so fall
        //    through to the Gen2 ACL check below (which is itself fail-closed).
        AzureTagCondition[] covering =
            azure.TagConditions.Where(t => uri.StartsWith(t.Scope, StringComparison.Ordinal)).ToArray();
        if (covering.Length > 0)
        {
            IReadOnlyDictionary<string, string>? tags = await blobTags.ReadTagsAsync(path, ct);
            if (tags is not null && covering.Any(c => AzureTagConditionEvaluator.Matches(c, tags)))
                return true;
            // else: no tag grant — fall through to the ACL path (never over-grants; ACL is fail-closed).
        }

        // 3. Gen2 ACL: file's own Read AND traverse-X on every ancestor. Folder access never trusted.
        Gen2Acl? acl = await fileAcl.ReadFileAclAsync(path, ct);
        if (acl is null) return false; // unreadable / flat account / incomplete → drop
        if (!PosixAclEvaluator.Grants(acl, azure.UserOid, azure.GroupOids, Gen2Permission.Read))
            return false;
        return await traverse.HoldsTraverseOnAllAncestorsAsync(path, azure.UserOid, azure.GroupOids, ct);
    }

    private async Task<AzureContext?> ResolveContextAsync(Guid? userId, CancellationToken ct)
    {
        if (userId is null) return null;
        AzureIdentityRef? link = await links.GetLinkAsync(userId.Value, ct);
        if (link is null) return null;

        AzureIdentitySet identity = await directory.ResolveAsync(link, ct);
        if (identity.Outcome != AzureIdentityOutcome.Resolved) return null; // deprovisioned/failed → drop azblob

        AzureRbacScopes scopes = await rbac.ResolveAsync(link.ObjectId, ct);
        if (scopes.Outcome != RbacOutcome.Resolved) return null; // RBAC uncertain → fail closed

        var groups = identity.PrincipalOids.Where(o => o != link.ObjectId).ToHashSet(StringComparer.Ordinal);
        return new AzureContext(
            link.ObjectId, groups,
            scopes.ReadablePrefixes.Select(s => s.Prefix).ToArray(),
            scopes.TagConditioned.ToArray(),
            scopes.DeniedPrefixes.ToArray());
    }

    private sealed record AzureContext(
        string UserOid, IReadOnlySet<string> GroupOids,
        IReadOnlyList<string> ReadablePrefixes, IReadOnlyList<AzureTagCondition> TagConditions,
        IReadOnlyList<string> DeniedPrefixes);
}
