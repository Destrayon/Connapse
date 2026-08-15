namespace Connapse.Core.Interfaces;

public interface IConnectorConfigMapper
{
    /// <summary>
    /// Splits a legacy containers.connector_config blob into the connection identity
    /// (credential + endpoint, shared across containers) and the source scope (the
    /// specific bucket/prefix/subpath this container pointed at).
    /// Throws ArgumentException for ManagedStorage, which is never migrated.
    /// </summary>
    (ConnectionIdentity Connection, string ScopeJson) Map(
        ConnectorType type, string? connectorConfig, string containerName);
}
