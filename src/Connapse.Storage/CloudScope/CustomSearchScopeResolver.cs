using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors.Custom;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// What a user's search may rank from custom sources on custom connections: for each such source, its
/// <c>custom://{source}/</c> prefix with the access keys the provider says the user holds, asked live on
/// every search. This is only a pre-filter — <see cref="CustomSearchResultVerifier"/> checks every hit —
/// so a provider without keys, or whose keys call fails, gets the bare prefix and the check decides alone.
/// A user who holds no keys in a source is granted nothing there.
/// </summary>
public sealed class CustomSearchScopeResolver(
    IConnectionStore connections,
    ISourceStore sources,
    ICustomProviderStore providers,
    IHttpClientFactory httpClients,
    IOptionsMonitor<CustomSourceSettings> settings,
    CustomConnectionPresence presence,
    ILogger<CustomSearchScopeResolver> logger) : ISearchScopeResolver
{
    public const int MaxKeys = 10_000;
    private static readonly TimeSpan KeysBudget = TimeSpan.FromSeconds(3);

    public async Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default)
    {
        if (userId is null) return SearchScopes.NoPrincipal;

        // Only connections that name a provider check permissions; one without is content-only.
        List<Connection> custom = (await connections.ListAsync(take: int.MaxValue, ct: ct))
            .Where(CustomSourceCredentials.ChecksPermissions)
            .ToList();
        presence.Record(custom.Count > 0);
        if (custom.Count == 0) return SearchScopes.None;

        List<GrantMatch> matches = [];
        foreach (Connection connection in custom)
        {
            CustomProvider? provider = CustomConnectionConfig.ProviderIdOf(connection.ConfigJson) is { } providerId
                ? await providers.GetAsync(providerId, ct)
                : null;
            string? token = provider is null ? null : await providers.GetTokenAsync(provider.Id, ct);
            if (provider is null || token is null
                || !CustomSourceEndpoint.TryNormalize(provider.BaseUrl, settings.CurrentValue.AllowedHttpHosts, out string baseUrl, out _))
                continue; // nothing to ask, so nothing granted

            CustomSourceClient client = new(httpClients.CreateClient(CustomSourceClient.HttpClientName), baseUrl, token);
            foreach (Source source in await sources.ListByConnectionAsync(connection.Id, ct))
            {
                if (CustomSourceScope.TryParse(source.ScopeJson) is not { } scope) continue;

                // The provider answers for the documents of its own middle man only. A source still reading
                // another one (the provider's address changed under it) is granted nothing.
                if (!CustomSourceEndpoint.TryNormalize(scope.BaseUrl, settings.CurrentValue.AllowedHttpHosts, out string sourceUrl, out _)
                    || !string.Equals(sourceUrl, baseUrl, StringComparison.Ordinal))
                    continue;
                string prefix = CustomResourceUri.SourcePrefix(source.Id);

                if (!provider.SupportsAccessKeys)
                {
                    matches.Add(new GrantMatch(prefix, IsExact: false));
                    continue;
                }

                IReadOnlyList<string>? keys = await KeysAsync(client, scope.ScopeId, userId.Value, source.Id, ct);
                if (keys is null)
                    matches.Add(new GrantMatch(prefix, IsExact: false)); // keys unknown: the check decides
                else if (keys.Count > 0)
                    matches.Add(new GrantMatch(prefix, IsExact: false, keys));
            }
        }

        return matches.Count == 0 ? SearchScopes.None : SearchScopes.Of(matches);
    }

    /// <summary>The user's keys, or null when the provider could not say.</summary>
    private async Task<IReadOnlyList<string>?> KeysAsync(
        CustomSourceClient client, string scopeId, Guid userId, Guid sourceId, CancellationToken ct)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(KeysBudget);
        try
        {
            CustomAccessKeys keys = await client.SearchAccessKeysAsync(scopeId, userId.ToString(), MaxKeys, budget.Token);
            if (keys.Truncated)
            {
                // Filtering by a partial set would hide documents keyed only by the rest, so the check decides.
                logger.LogWarning("Custom source {SourceId}: more than {MaxKeys} access keys for one user; every hit will be checked",
                    sourceId, MaxKeys);
                return null;
            }
            return keys.Keys;
        }
        catch (Exception ex) when (ex is CustomSourceException or HttpRequestException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Custom source {SourceId}: could not read the searcher's access keys; every hit will be checked", sourceId);
            return null;
        }
    }
}
