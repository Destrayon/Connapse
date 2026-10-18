using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Storage.Connectors.Custom;

/// <summary>
/// The token a custom source reads with. A source holds none of its own: on a connection that names a custom
/// provider it is the provider's token, and on a connection without one it is the connection's own secret. A
/// source not yet moved onto a connection still reads with the token it was created with.
/// </summary>
public static class CustomSourceCredentials
{
    public static async Task<string?> TokenAsync(
        Source source,
        Connection? connection,
        ISourceStore sources,
        IConnectionStore connections,
        ICustomProviderStore? providers,
        CancellationToken ct)
    {
        if (connection is { Provider: ConnectionProvider.Custom })
        {
            // A connection's token, or its provider's, only ever goes to the server it was stored for: a source
            // whose scope names any other address gets no token at all.
            string? sourceAddress = CustomSourceScope.TryParse(source.ScopeJson)?.BaseUrl;
            if (CustomConnectionConfig.ProviderIdOf(connection.ConfigJson) is { } providerId)
            {
                if (providers is null || await providers.GetAsync(providerId, ct) is not { } provider) return null;
                RequireSameAddress(source, sourceAddress, provider.BaseUrl);
                return await providers.GetTokenAsync(providerId, ct);
            }
            RequireSameAddress(source, sourceAddress, CustomConnectionConfig.BaseUrlOf(connection.ConfigJson));
            return connection.HasSecret ? await connections.GetSecretAsync(connection.Id, ct) : null;
        }

        return source.HasSecret ? await sources.GetSecretAsync(source.Id, ct) : null;
    }

    private static void RequireSameAddress(Source source, string? sourceAddress, string? connectionAddress)
    {
        if (!CustomSourceEndpoint.SameAddress(sourceAddress, connectionAddress) || connectionAddress is null)
            throw new InvalidOperationException(
                $"Source '{source.Name}' cannot sync: it reads {sourceAddress ?? "no address"}, but its connection's source server is {connectionAddress ?? "not set"}.");
    }

    /// <summary>Whether the connection checks permissions per user: it names a custom provider.</summary>
    public static bool ChecksPermissions(Connection? connection) =>
        connection is { Provider: ConnectionProvider.Custom } && CustomConnectionConfig.ProviderIdOf(connection.ConfigJson) is not null;
}
