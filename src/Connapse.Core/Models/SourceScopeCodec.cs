namespace Connapse.Core;

/// <summary>
/// Reads and writes a source's stored scope JSON as a <see cref="SourceScope"/>, by the source's
/// provider (#859). The stored keys stay what the connectors read, so no row needs migrating.
/// </summary>
public static class SourceScopeCodec
{
    /// <summary>The provider a scope's sources use.</summary>
    public static ConnectionProvider ProviderOf(SourceScope scope) => throw new NotImplementedException();

    /// <summary>
    /// The typed scope a stored row holds, or null when the JSON doesn't name a scope of the provider's
    /// kind; such a row is left to the connector, which says what is wrong with it.
    /// </summary>
    public static SourceScope? Read(ConnectionProvider provider, string? storedJson) => throw new NotImplementedException();

    /// <summary>
    /// The JSON to store for <paramref name="scope"/>. Given the row's current JSON, keys the type
    /// doesn't model are kept, and a row whose scope is unchanged comes back exactly as it was.
    /// </summary>
    public static string Write(SourceScope scope, string? storedJson = null) => throw new NotImplementedException();
}
