namespace Connapse.Core.Interfaces;

public interface IConnectorFactory
{
    IConnector Create(Container container);

    /// <summary>
    /// Builds a read-only connector for a source by recombining its connection's credential
    /// and endpoint with the source's own scope — what a container held in a single
    /// connector_config blob is split across two rows for a source.
    /// <para>
    /// Never returns an <see cref="IWritableConnector"/>. Throws ArgumentException when the
    /// connection does not own the source.
    /// </para>
    /// </summary>
    IConnector Create(Source source, Connection connection);
}
