using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.Backfill;

public class ConnectorConfigMapper : IConnectorConfigMapper
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public (ConnectionIdentity Connection, string ScopeJson) Map(
        ConnectorType type, string? connectorConfig, string containerName)
    {
        if (type == ConnectorType.ManagedStorage)
            throw new ArgumentException("Managed storage containers are never migrated to sources.", nameof(type));

        using var doc = string.IsNullOrWhiteSpace(connectorConfig)
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(connectorConfig);
        var root = doc.RootElement;

        return type switch
        {
            ConnectorType.S3 => MapS3(root, containerName),
            ConnectorType.AzureBlob => MapAzure(root, containerName),
            ConnectorType.Filesystem => MapFilesystem(root, containerName),
            _ => throw new ArgumentException($"Unsupported connector type '{type}'.", nameof(type))
        };
    }

    private static (ConnectionIdentity, string) MapS3(JsonElement root, string containerName)
    {
        string region = Str(root, "region") ?? "us-east-1";
        string? roleArn = Str(root, "roleArn");
        string bucket = Str(root, "bucketName") ?? "";
        string? prefix = Str(root, "prefix");

        string dedupKey = $"s3|{region}|{roleArn ?? "-"}";
        string configJson = JsonSerializer.Serialize(new { region, roleArn }, Options);
        string scopeJson = JsonSerializer.Serialize(new { bucketName = bucket, prefix }, Options);

        return (new ConnectionIdentity(ConnectionProvider.S3, dedupKey, NameFor("s3", dedupKey, containerName), configJson), scopeJson);
    }

    private static (ConnectionIdentity, string) MapAzure(JsonElement root, string containerName)
    {
        string account = Str(root, "storageAccountName") ?? "";
        string? clientId = Str(root, "managedIdentityClientId");
        string blobContainer = Str(root, "containerName") ?? "";
        string? prefix = Str(root, "prefix");

        string dedupKey = $"azure|{account}|{clientId ?? "-"}";
        string configJson = JsonSerializer.Serialize(new { storageAccountName = account, managedIdentityClientId = clientId }, Options);
        string scopeJson = JsonSerializer.Serialize(new { containerName = blobContainer, prefix }, Options);

        return (new ConnectionIdentity(ConnectionProvider.AzureBlob, dedupKey, NameFor("azure", dedupKey, containerName), configJson), scopeJson);
    }

    private static (ConnectionIdentity, string) MapFilesystem(JsonElement root, string containerName)
    {
        string rootPath = Str(root, "rootPath") ?? "";
        string[] include = Arr(root, "includePatterns");
        string[] exclude = Arr(root, "excludePatterns");

        string dedupKey = $"fs|{rootPath}";
        string configJson = JsonSerializer.Serialize(new { allowedRoot = rootPath }, Options);
        string scopeJson = JsonSerializer.Serialize(new { subPath = "", includePatterns = include, excludePatterns = exclude }, Options);

        return (new ConnectionIdentity(ConnectionProvider.Filesystem, dedupKey, NameFor("fs", dedupKey, containerName), configJson), scopeJson);
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string[] Arr(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : [];

    /// <summary>
    /// Derives a deterministic connection name from the dedup key. This is load-bearing:
    /// two containers sharing a credential produce the same name, and the unique index on
    /// connections.name is what actually deduplicates them. Do not compare serialized
    /// config JSON instead — Postgres jsonb normalizes property order and whitespace on
    /// storage, so a round-tripped blob will not match what JsonSerializer produced and
    /// every container would end up with its own connection.
    /// </summary>
    private static string NameFor(string provider, string dedupKey, string containerName)
    {
        // Strip the provider prefix already present in the dedup key, then slugify.
        string tail = dedupKey.Contains('|') ? dedupKey[(dedupKey.IndexOf('|') + 1)..] : dedupKey;
        string basis = tail.Trim('|', '-', ' ').Length > 0 ? tail : containerName;

        string slug = new string(basis
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);

        if (slug.Length == 0) slug = "default";

        string name = $"{provider}-{slug}";
        // connections.name is varchar(128).
        return name.Length <= 128 ? name : name[..128];
    }
}
