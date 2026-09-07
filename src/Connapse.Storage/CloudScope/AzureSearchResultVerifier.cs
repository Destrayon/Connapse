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
/// (4d). Fails closed on every uncertain read. Preserves rank order and returns at most topK.
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
    public int CandidateMultiplier =>
        Math.Max(1, settings.Value.CandidateMultiplier);

    public async Task<IReadOnlyList<SearchHit>> VerifyAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default)
    {
        // No Azure enforcement → the resolver did not broaden; nothing to tighten.
        if (enforcement.CurrentValue.StateForAzure(azureAd.CurrentValue.IsConfigured, migration.Determined)
            != EnforcementState.Enforcing)
            return rankedCandidates.Take(topK).ToList();

        // Map hits to their governing URIs (one batched query).
        IReadOnlyDictionary<string, string?> uris =
            await documents.GetResourceUrisAsync(rankedCandidates.Select(h => h.DocumentId).ToList(), ct);

        // Resolve the searcher's Azure context once. Any failure/deprovision → drop every azblob hit.
        AzureContext? azure = await ResolveContextAsync(userId, ct);

        // Verify azblob hits concurrently (bounded); non-azblob pass; decision keyed by index so we
        // can re-assemble in rank order.
        var verdicts = new ConcurrentDictionary<int, bool>();
        using var gate = new SemaphoreSlim(Math.Max(1, settings.Value.MaxParallelism));

        await Task.WhenAll(rankedCandidates.Select(async (hit, i) =>
        {
            string? uri = uris.GetValueOrDefault(hit.DocumentId);
            if (!AzblobUri.TryParse(uri, out Gen2Path path))
            {
                verdicts[i] = true; // s3:// or non-cloud → pass untouched
                return;
            }
            if (azure is null)
            {
                verdicts[i] = false; // enforcing but no usable identity → drop azblob
                return;
            }
            await gate.WaitAsync(ct);
            try { verdicts[i] = await AdmitAzureHitAsync(uri!, path, azure, ct); }
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
        // 1. RBAC read supersedes ACLs — covered by any readable prefix → pass, no live call.
        if (azure.ReadablePrefixes.Any(p => uri.StartsWith(p, StringComparison.Ordinal)))
            return true;

        // 2. Tag-conditioned residue → live tag verify against every covering condition.
        AzureTagCondition[] covering =
            azure.TagConditions.Where(t => uri.StartsWith(t.Scope, StringComparison.Ordinal)).ToArray();
        if (covering.Length > 0)
        {
            IReadOnlyDictionary<string, string>? tags = await blobTags.ReadTagsAsync(path, ct);
            if (tags is null) return false; // fail closed
            return covering.Any(c => AzureTagConditionEvaluator.Matches(c, tags));
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
            scopes.TagConditioned.ToArray());
    }

    private sealed record AzureContext(
        string UserOid, IReadOnlySet<string> GroupOids,
        IReadOnlyList<string> ReadablePrefixes, IReadOnlyList<AzureTagCondition> TagConditions);
}
