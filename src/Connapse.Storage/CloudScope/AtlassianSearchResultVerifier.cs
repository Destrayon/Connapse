using System.Collections.Concurrent;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Live per-hit Confluence permission verify. Passes every hit that is not an <c>atlassian://</c>
/// document untouched; admits a Confluence hit only when Confluence says the searcher's linked
/// account may read its page. Fails closed on a missing document row, an unparseable address, an
/// unlinked user, and any check that fails or is still running when the search's budget runs out.
/// Keeps rank order and never caps: the composite caps once.
/// </summary>
public sealed class AtlassianSearchResultVerifier(
    IDocumentStore documents,
    IAtlassianIdentityLinkReader links,
    IConnectionStore connections,
    ConfluencePermissionChecker checker,
    AtlassianConnectionPresence presence,
    IOptions<AtlassianVerifierSettings> settings,
    ILogger<AtlassianSearchResultVerifier> logger) : IPerSchemeResultVerifier
{
    /// <summary>
    /// Over-fetch only when some Atlassian connection is known to exist, since only then can hits
    /// be dropped. Answers from the process-wide last known flag, so it never flips back to
    /// "unknown" between searches; before the first answer it is 1, which can only reduce
    /// backfill, never admit a hit.
    /// </summary>
    public int CandidateMultiplier =>
        presence.Known == true ? Math.Max(1, settings.Value.CandidateMultiplier) : 1;

    public async Task<IReadOnlyList<SearchHit>> VerifyAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default) =>
        (await VerifyScopedAsync(rankedCandidates, userId, topK, ct)).Hits;

    // Enforced when this call dropped anything, so the composite caps the backfilled pool at topK; a call that
    // dropped nothing hands the pool back untouched, as a pass-through verifier does.
    public async Task<PerSchemeVerification> VerifyScopedAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default)
    {
        IReadOnlyList<SearchHit> survivors = await VerifyHitsAsync(rankedCandidates, userId, ct);
        return new PerSchemeVerification(survivors, Enforced: survivors.Count < rankedCandidates.Count);
    }

    private async Task<IReadOnlyList<SearchHit>> VerifyHitsAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, CancellationToken ct)
    {
        await RefreshIfDueAsync(ct);

        if (rankedCandidates.Count == 0)
            return rankedCandidates;

        // Every hit's address is read and every atlassian:// hit checked, whatever the presence
        // flag says. The flag is per process and up to a refresh old, while the scope resolver
        // reads connections live, so a site added on another instance would otherwise be searched
        // here unchecked. It only sizes CandidateMultiplier. Deployments with no Confluence hits
        // pay one batched lookup: the link and connection lookups below run only for atlassian://.

        IReadOnlyDictionary<string, string?> uris =
            await documents.GetResourceUrisAsync(rankedCandidates.Select(h => h.DocumentId).ToList(), ct);

        // Decide every hit that needs no call; collect the Confluence ones that do.
        var verdicts = new bool[rankedCandidates.Count];
        var pending = new List<(int Index, string CloudId, string ContentId)>();
        for (int i = 0; i < rankedCandidates.Count; i++)
        {
            if (!uris.TryGetValue(rankedCandidates[i].DocumentId, out string? uri))
                continue; // document row not found → fail closed
            if (!AtlassianUri.IsAtlassian(uri))
            {
                verdicts[i] = true; // not ours (null, s3://, azblob://, github://) → pass untouched
                continue;
            }
            if (AtlassianUri.TryParse(uri, out string cloudId, out string contentId))
                pending.Add((i, cloudId, contentId)); // an unparseable address stays denied
        }

        if (pending.Count == 0)
            return Survivors(rankedCandidates, verdicts);

        string? accountId = await LinkedAccountAsync(userId, ct);
        if (accountId is null)
            return Survivors(rankedCandidates, verdicts); // unlinked → every Confluence hit denied, no calls

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, settings.Value.BudgetMs)));
        using var gate = new SemaphoreSlim(Math.Max(1, settings.Value.MaxParallelism));

        // Chunks and attachments of one page share its check, so each page takes one slot, not one per hit.
        var pages = pending.Select(p => (p.CloudId, p.ContentId)).Distinct().ToList();
        var sites = checker.NewSiteLookup(); // one connection lookup per site for this search
        var allowed = new ConcurrentDictionary<(string CloudId, string ContentId), bool>();
        await Task.WhenAll(pages.Select(async page =>
        {
            try
            {
                await gate.WaitAsync(budget.Token);
                try { allowed[page] = await checker.CanReadAsync(page.CloudId, page.ContentId, accountId, sites, budget.Token); }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Cut off by the budget, not by the caller → unchecked, so denied.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Confluence hit verify failed; dropping");
            }
        }));
        ct.ThrowIfCancellationRequested();

        if (allowed.Count < pages.Count)
            logger.LogWarning("Confluence checks ran out of time; {Unchecked} of {Total} pages denied unchecked",
                pages.Count - allowed.Count, pages.Count);

        foreach (var p in pending)
            verdicts[p.Index] = allowed.GetValueOrDefault((p.CloudId, p.ContentId));
        return Survivors(rankedCandidates, verdicts);
    }

    private static List<SearchHit> Survivors(IReadOnlyList<SearchHit> ranked, bool[] verdicts)
    {
        var survivors = new List<SearchHit>(ranked.Count);
        for (int i = 0; i < ranked.Count; i++)
            if (verdicts[i]) survivors.Add(ranked[i]);
        return survivors;
    }

    private async Task<string?> LinkedAccountAsync(Guid? userId, CancellationToken ct)
    {
        if (userId is not { } id)
            return null;

        try
        {
            return (await links.GetLinkAsync(id, ct))?.AccountId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the searcher's Atlassian link; denying Confluence hits");
            return null;
        }
    }

    /// <summary>
    /// Re-reads the connection list at most once per <see cref="AtlassianConnectionPresence.RefreshInterval"/>
    /// across the process, not once per search.
    /// </summary>
    private async Task RefreshIfDueAsync(CancellationToken ct)
    {
        if (!presence.RefreshDue || !presence.TryBeginRefresh(out long ticket))
            return;

        try
        {
            bool any = (await connections.ListAsync(take: int.MaxValue, ct: ct))
                .Any(c => c.Provider == ConnectionProvider.Atlassian);
            presence.CompleteRefresh(ticket, any);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The last known answer stands; it only sizes retrieval and skips lookups for
            // deployments known to have no site, never admits a Confluence hit.
            presence.AbandonRefresh();
            logger.LogWarning(ex, "Could not tell whether any Atlassian connection exists");
        }
        catch (OperationCanceledException)
        {
            presence.AbandonRefresh();
            throw;
        }
    }
}
