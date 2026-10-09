using System.Collections.Concurrent;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
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
    IMemoryCache cache,
    IOptions<AtlassianVerifierSettings> settings,
    ILogger<AtlassianSearchResultVerifier> logger) : IPerSchemeResultVerifier
{
    private const string AnyConnectionKey = "atl:any-connection";
    private static readonly TimeSpan AnyConnectionTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Over-fetch only when some Atlassian connection exists, since only then can hits be dropped.
    /// Read on every search, so it answers from a briefly cached flag that <see cref="VerifyAsync"/>
    /// refreshes; until the first refresh it over-fetches, which costs a little and drops nothing.
    /// </summary>
    public int CandidateMultiplier =>
        cache.TryGetValue(AnyConnectionKey, out bool any) && !any
            ? 1
            : Math.Max(1, settings.Value.CandidateMultiplier);

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
        await RefreshAnyConnectionAsync(ct);

        if (rankedCandidates.Count == 0)
            return rankedCandidates;

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
        var allowed = new ConcurrentDictionary<(string CloudId, string ContentId), bool>();
        await Task.WhenAll(pages.Select(async page =>
        {
            try
            {
                await gate.WaitAsync(budget.Token);
                try { allowed[page] = await checker.CanReadAsync(page.CloudId, page.ContentId, accountId, budget.Token); }
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

    private async Task RefreshAnyConnectionAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(AnyConnectionKey, out bool _))
            return;

        try
        {
            bool any = (await connections.ListAsync(take: int.MaxValue, ct: ct))
                .Any(c => c.Provider == ConnectionProvider.Atlassian);
            cache.Set(AnyConnectionKey, any, AnyConnectionTtl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Left unknown, which over-fetches; it only sizes retrieval, never admits a hit.
            logger.LogWarning(ex, "Could not tell whether any Atlassian connection exists");
        }
    }
}
