namespace Connapse.Core.Interfaces;

public interface IConnectorFactory
{
    /// <summary>
    /// Builds a read-only connector for a source by recombining its connection's credential
    /// and endpoint with the source's own scope — what a container held in a single
    /// connector_config blob is split across two rows for a source.
    /// <para>
    /// Never returns an <see cref="IWritableConnector"/>. Throws ArgumentException when the
    /// connection does not own the source.
    /// </para>
    /// </summary>
    /// <param name="secret">
    /// The connection's decrypted secret, for the providers that have one. Passed in rather
    /// than read from <paramref name="connection"/> because <see cref="Connection"/> is the
    /// read model returned from the connections API, and keeping credentials off it is the
    /// reason <see cref="IConnectionStore.GetSecretAsync"/> exists as a separate call.
    /// <para>
    /// Null for S3, Azure Blob and Filesystem, which authenticate from ambient identity or
    /// need nothing at all. A provider that requires one and is handed null fails at connect
    /// time, which is the loud failure it should be.
    /// </para>
    /// </param>
    IConnector Create(Source source, Connection connection, string? secret = null);
}
