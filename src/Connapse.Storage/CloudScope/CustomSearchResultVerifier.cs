using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors.Custom;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// The live permission check for connected custom sources: every <c>custom://</c> hit is sent to its
/// source's provider, batched, and kept only on an explicit yes. Everything that cannot be confirmed — no
/// user, a missing row, an address that does not parse, a source no longer on a custom connection, a missing
/// provider or token, a failed or mismatched answer, a check still running when the budget runs out — is
/// denied. Hits without a <c>custom://</c> address pass untouched; rank order is kept and nothing is capped.
/// </summary>
public sealed class CustomSearchResultVerifier(
    IDocumentStore documents,
    ISourceStore sources,
    IConnectionStore connections,
    ICustomProviderStore providers,
    IHttpClientFactory httpClients,
    IOptionsMonitor<CustomSourceSettings> settings,
    CustomConnectionPresence presence,
    ILogger<CustomSearchResultVerifier> logger) : IPerSchemeResultVerifier
{
    private const int OverFetch = 3;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    /// <summary>Over-fetch only once a custom connection is known to exist; before that 1, which can only reduce backfill.</summary>
    public int CandidateMultiplier => presence.Known == true ? OverFetch : 1;

    public async Task<IReadOnlyList<SearchHit>> VerifyAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default) =>
        (await VerifyScopedAsync(rankedCandidates, userId, topK, ct)).Hits;

    public async Task<PerSchemeVerification> VerifyScopedAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default)
    {
        if (rankedCandidates.Count == 0) return new PerSchemeVerification(rankedCandidates, Enforced: false);

        IReadOnlyDictionary<string, string?> uris =
            await documents.GetResourceUrisAsync(rankedCandidates.Select(h => h.DocumentId).Distinct().ToList(), ct);

        bool[] allowed = new bool[rankedCandidates.Count];
        Dictionary<Guid, List<(int Index, string DocumentId)>> bySource = [];
        for (int i = 0; i < rankedCandidates.Count; i++)
        {
            if (!uris.TryGetValue(rankedCandidates[i].DocumentId, out string? uri)) continue; // no row: denied
            if (uri is null || !uri.StartsWith(CustomResourceUri.Scheme, StringComparison.Ordinal))
            {
                allowed[i] = true; // not ours to judge
                continue;
            }

            if (!CustomResourceUri.TryParse(uri, out Guid sourceId, out string documentId)) continue; // denied
            if (!bySource.TryGetValue(sourceId, out List<(int, string)>? list)) bySource[sourceId] = list = [];
            list.Add((i, documentId));
        }

        if (bySource.Count > 0 && userId is { } user)
        {
            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(Budget);
            foreach ((Guid sourceId, List<(int Index, string DocumentId)> hits) in bySource)
            {
                IReadOnlyList<bool>? decisions = await CheckAsync(sourceId, user, hits.Select(h => h.DocumentId).ToList(), budget.Token, ct);
                if (decisions is null) continue; // the whole source is denied
                for (int j = 0; j < hits.Count; j++)
                    allowed[hits[j].Index] = decisions[j];
            }
        }

        List<SearchHit> survivors = rankedCandidates.Where((_, i) => allowed[i]).ToList();
        return new PerSchemeVerification(survivors, Enforced: survivors.Count < rankedCandidates.Count);
    }

    /// <summary>The middle man's decisions for these documents, or null when none can be trusted.</summary>
    private async Task<IReadOnlyList<bool>?> CheckAsync(
        Guid sourceId, Guid userId, IReadOnlyList<string> documentIds, CancellationToken budget, CancellationToken ct)
    {
        try
        {
            Source? source = await sources.GetAsync(sourceId, ct);
            Connection? connection = source?.ConnectionId is { } connectionId ? await connections.GetAsync(connectionId, ct) : null;
            if (source is null || connection is not { Provider: ConnectionProvider.Custom }
                || CustomSourceScope.TryParse(source.ScopeJson) is not { } scope)
                return null;

            CustomProvider? provider = CustomConnectionConfig.ProviderIdOf(connection.ConfigJson) is { } providerId
                ? await providers.GetAsync(providerId, ct)
                : null;
            string? token = provider is null ? null : await providers.GetTokenAsync(provider.Id, ct);
            if (provider is null || token is null
                || !CustomSourceEndpoint.TryNormalize(provider.BaseUrl, settings.CurrentValue.AllowedHttpHosts, out string baseUrl, out _))
                return null;

            // The documents were listed by the source's middle man; only that same middle man may approve them.
            if (!CustomSourceEndpoint.TryNormalize(scope.BaseUrl, settings.CurrentValue.AllowedHttpHosts, out string sourceUrl, out _)
                || !string.Equals(sourceUrl, baseUrl, StringComparison.Ordinal))
                return null;

            CustomSourceClient client = new(httpClients.CreateClient(CustomSourceClient.HttpClientName), baseUrl, token);
            return await client.EvaluateAsync(userId.ToString(), scope.ScopeId, documentIds, Math.Max(1, provider.MaxCheckBatch), budget);
        }
        catch (Exception ex) when (ex is CustomSourceException or HttpRequestException or InvalidOperationException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Custom source {SourceId}: the permission check failed; its results are hidden", sourceId);
            return null;
        }
    }
}
