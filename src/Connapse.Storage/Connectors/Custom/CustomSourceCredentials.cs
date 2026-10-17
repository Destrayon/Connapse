using Connapse.Core;
using Connapse.Core.Interfaces;

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
            if (CustomConnectionConfig.ProviderIdOf(connection.ConfigJson) is { } providerId)
                return providers is null ? null : await providers.GetTokenAsync(providerId, ct);
            if (connection.HasSecret)
                return await connections.GetSecretAsync(connection.Id, ct);
        }

        return source.HasSecret ? await sources.GetSecretAsync(source.Id, ct) : null;
    }

    /// <summary>Whether the connection checks permissions per user: it names a custom provider.</summary>
    public static bool ChecksPermissions(Connection? connection) =>
        connection is { Provider: ConnectionProvider.Custom } && CustomConnectionConfig.ProviderIdOf(connection.ConfigJson) is not null;
}
