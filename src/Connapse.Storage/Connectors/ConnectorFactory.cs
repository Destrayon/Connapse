using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Creates IConnector instances from ContainerEntity configuration.
/// </summary>
public class ConnectorFactory(
    IManagedStorageProvider managedStorageProvider,
    IOptionsMonitor<SourceSecuritySettings> sourceSecurity,
    ILogger<ConnectorFactory> logger) : IConnectorFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Scopes already warned about running without an allowlist.
    /// <para>
    /// A connector is built on every sync cycle, so warning unconditionally would emit one
    /// line per source every five minutes — burying the very message an operator needs to
    /// act on before these become deny-by-default. Keyed on the scope as well as the
    /// connection, so changing a root or bucket warns again rather than staying silent.
    /// </para>
    /// </summary>
    private readonly HashSet<string> _warnedUnrestricted = [];

    public IConnector Create(Container container)
    {
        return container.ConnectorType switch
        {
            ConnectorType.ManagedStorage => managedStorageProvider.CreateConnector(container.Id),
            ConnectorType.Filesystem => CreateFilesystemConnector(container),
            ConnectorType.S3 => CreateS3Connector(container),
            ConnectorType.AzureBlob => CreateAzureBlobConnector(container),
            _ => throw new NotSupportedException($"Unknown connector type: {container.ConnectorType}")
        };
    }

    public IConnector Create(Source source, Connection connection)
    {
        if (source.ConnectionId != connection.Id)
            throw new ArgumentException(
                $"Connection '{connection.Id}' does not own source '{source.Id}'.", nameof(connection));

        using var credential = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(connection.ConfigJson) ? "{}" : connection.ConfigJson);
        using var scope = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(source.ScopeJson) ? "{}" : source.ScopeJson);

        return connection.Provider switch
        {
            ConnectionProvider.S3 => new S3Connector(new S3ConnectorConfig
            {
                Region = Str(credential, "region") ?? "us-east-1",
                RoleArn = Str(credential, "roleArn"),
                BucketName = RequirePermittedLocation(
                    Arr(credential, "allowedLocations"),
                    Str(scope, "bucketName")
                        ?? throw new InvalidOperationException(
                            $"Source '{source.Name}' has no bucketName in its scope."),
                    Str(scope, "prefix"),
                    connection.Name, source.Name),
                Prefix = Str(scope, "prefix"),
            }),

            ConnectionProvider.AzureBlob => new AzureBlobConnector(new AzureBlobConnectorConfig
            {
                StorageAccountName = Str(credential, "storageAccountName")
                    ?? throw new InvalidOperationException(
                        $"Connection '{connection.Name}' has no storageAccountName."),
                ManagedIdentityClientId = Str(credential, "managedIdentityClientId"),
                ContainerName = RequirePermittedLocation(
                    Arr(credential, "allowedLocations"),
                    Str(scope, "containerName")
                        ?? throw new InvalidOperationException(
                            $"Source '{source.Name}' has no containerName in its scope."),
                    Str(scope, "prefix"),
                    connection.Name, source.Name),
                Prefix = Str(scope, "prefix"),
            }),

            ConnectionProvider.Filesystem => new FilesystemConnector(new FilesystemConnectorConfig
            {
                RootPath = CombineUnderRoot(
                    RequirePermittedRoot(
                        Str(credential, "allowedRoot")
                            ?? throw new InvalidOperationException(
                                $"Connection '{connection.Name}' has no allowedRoot."),
                        connection.Name),
                    Str(scope, "subPath"),
                    source.Name),
                IncludePatterns = Arr(scope, "includePatterns"),
                ExcludePatterns = Arr(scope, "excludePatterns"),
            }),

            _ => throw new NotSupportedException($"Unknown connection provider: {connection.Provider}")
        };
    }

    private static string? Str(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static IReadOnlyList<string> Arr(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : [];

    /// <summary>
    /// True the first time a given scope is seen, false afterwards. The factory is a
    /// singleton, so this is shared across every sync cycle for the process's lifetime.
    /// </summary>
    private bool ShouldWarn(string key)
    {
        lock (_warnedUnrestricted)
        {
            return _warnedUnrestricted.Add(key);
        }
    }

    /// <summary>
    /// Checks that a source's bucket or blob container falls inside the locations its
    /// connection permits, and returns it.
    /// <para>
    /// The cloud counterpart of <see cref="RequirePermittedRoot"/>. IAM cannot make this
    /// distinction on its own: one connection role is shared by every source that uses the
    /// connection, so as far as AWS or Azure is concerned each of those sources is the same
    /// principal.
    /// </para>
    /// </summary>
    private string RequirePermittedLocation(
        IReadOnlyList<string> allowedLocations, string container, string? prefix,
        string connectionName, string sourceName)
    {
        switch (StorageLocationPolicy.Evaluate(allowedLocations, container, prefix))
        {
            case StorageLocationDecision.Allowed:
                return container;

            case StorageLocationDecision.UnrestrictedByConfiguration:
                // Warned rather than refused, matching the filesystem root allowlist: #350
                // backfilled existing S3 and Azure containers into connections and none of
                // them declare locations, so enforcing now would break every upgrade.
                if (ShouldWarn($"location:{connectionName}:{container}"))
                {
                    logger.LogWarning(
                        "Connection {ConnectionName} declares no allowedLocations, so source "
                        + "{SourceName} may name any container its credential can reach. It "
                        + "currently names {Container}.",
                        LogSanitizer.Sanitize(connectionName),
                        LogSanitizer.Sanitize(sourceName),
                        LogSanitizer.Sanitize(container));
                }
                return container;

            default:
                throw new InvalidOperationException(
                    $"Source '{sourceName}' names storage location '{container}/{prefix}', which is not "
                    + $"within the allowedLocations declared by connection '{connectionName}'.");
        }
    }

    /// <summary>
    /// Checks a connection's allowed root against the deployment's configured allowlist.
    /// <para>
    /// The confinement check below stops a source escaping its root; this stops the root
    /// itself being somewhere it should never be. They are independent: an allowlist does not
    /// stop a symlink, and link resolution does not stop <c>allowedRoot: "/"</c>.
    /// </para>
    /// </summary>
    private string RequirePermittedRoot(string allowedRoot, string connectionName)
    {
        switch (sourceSecurity.CurrentValue.EvaluateRoot(allowedRoot))
        {
            case FilesystemRootDecision.Allowed:
                return allowedRoot;

            case FilesystemRootDecision.UnrestrictedByConfiguration:
                // Warned rather than refused, for one release: #350 backfilled existing
                // filesystem containers into connections, so enforcing immediately would
                // break every upgrade until an operator edited configuration. The warning
                // names the root so they know what to add.
                if (ShouldWarn($"root:{connectionName}:{allowedRoot}"))
                {
                    logger.LogWarning(
                        "Connection {ConnectionName} uses filesystem root {Root} with no "
                        + "{SectionName}:AllowedFilesystemRoots configured. Any root is currently "
                        + "accepted; configure the allowlist to bound this.",
                        LogSanitizer.Sanitize(connectionName),
                        LogSanitizer.Sanitize(allowedRoot),
                        SourceSecuritySettings.SectionName);
                }
                return allowedRoot;

            default:
                throw new InvalidOperationException(
                    $"Connection '{connectionName}' names filesystem root '{allowedRoot}', which is not "
                    + $"within any entry of {SourceSecuritySettings.SectionName}:AllowedFilesystemRoots.");
        }
    }

    /// <summary>
    /// Resolves a source's subPath beneath its connection's allowed root, and verifies the
    /// result stays inside it. The allowed root is the boundary an admin configured, so a
    /// scope containing "../" must not be able to reach past it.
    /// </summary>
    private static string CombineUnderRoot(string allowedRoot, string? subPath, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(subPath))
            return Path.GetFullPath(allowedRoot);

        // Delegated rather than compared here (#365). The previous check was Path.GetFullPath
        // plus StartsWith, which is purely lexical: a junction inside the allowed root passed
        // it, and the connector then walked straight through to the target. PathConfinement
        // resolves links on every path segment before comparing.
        return PathConfinement.CombineWithin(allowedRoot, subPath)
            ?? throw new InvalidOperationException(
                $"Source '{sourceName}' resolves to a path outside its connection's allowed root '{allowedRoot}'.");
    }

    private static S3Connector CreateS3Connector(Container container)
    {
        if (string.IsNullOrEmpty(container.ConnectorConfig))
            throw new InvalidOperationException(
                $"S3 connector for container '{container.Name}' requires bucket configuration. No connector config found.");

        var config = JsonSerializer.Deserialize<S3ConnectorConfig>(container.ConnectorConfig, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize S3 connector config for container '{container.Name}'.");

        if (string.IsNullOrWhiteSpace(config.BucketName))
            throw new InvalidOperationException(
                $"S3 connector for container '{container.Name}' has an empty bucket name.");

        return new S3Connector(config);
    }

    private static AzureBlobConnector CreateAzureBlobConnector(Container container)
    {
        if (string.IsNullOrEmpty(container.ConnectorConfig))
            throw new InvalidOperationException(
                $"AzureBlob connector for container '{container.Name}' requires storage account configuration. No connector config found.");

        var config = JsonSerializer.Deserialize<AzureBlobConnectorConfig>(container.ConnectorConfig, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize AzureBlob connector config for container '{container.Name}'.");

        if (string.IsNullOrWhiteSpace(config.StorageAccountName))
            throw new InvalidOperationException(
                $"AzureBlob connector for container '{container.Name}' has an empty storage account name.");

        if (string.IsNullOrWhiteSpace(config.ContainerName))
            throw new InvalidOperationException(
                $"AzureBlob connector for container '{container.Name}' has an empty container name.");

        return new AzureBlobConnector(config);
    }

    private static FilesystemConnector CreateFilesystemConnector(Container container)
    {
        if (string.IsNullOrEmpty(container.ConnectorConfig))
            throw new InvalidOperationException(
                $"Filesystem connector for container '{container.Name}' requires a root path. No connector config found.");

        var config = JsonSerializer.Deserialize<FilesystemConnectorConfig>(container.ConnectorConfig, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize Filesystem connector config for container '{container.Name}'.");

        if (string.IsNullOrWhiteSpace(config.RootPath))
            throw new InvalidOperationException(
                $"Filesystem connector for container '{container.Name}' has an empty root path.");

        return new FilesystemConnector(config);
    }
}
