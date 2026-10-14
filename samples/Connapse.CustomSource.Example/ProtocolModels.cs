namespace Connapse.CustomSource.Example;

public sealed record DiscoveryDocument(IReadOnlyList<string> ProtocolVersions, string Name, Capabilities Capabilities);

public sealed record Capabilities(
    bool Changes,
    bool Tombstones,
    int MinSyncIntervalSeconds,
    int MaxPageSize,
    PermissionCapabilities? Permissions);

public sealed record PermissionCapabilities(CheckCapability Check, AccessKeysCapability AccessKeys, LinkingCapability? Linking);

public sealed record CheckCapability(int MaxBatch);

public sealed record AccessKeysCapability(int CacheTtlSeconds);

public sealed record LinkingCapability(string LinkUrl);

public sealed record ScopeItem(string Id, string Name, string? Description);

public sealed record ScopePage(IReadOnlyList<ScopeItem> Items, string? NextPageToken);

public sealed record DocumentItem(
    string Id,
    string Path,
    string Title,
    string ContentType,
    long Size,
    string Version,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<string>? AccessKeys,
    string? AccessVersion)
{
    public static DocumentItem From(ExampleDocument d, bool includeAccess) => new(
        d.Id,
        d.Path,
        System.IO.Path.GetFileNameWithoutExtension(d.Path),
        d.ContentType,
        d.Size,
        d.Version,
        d.ModifiedAt,
        includeAccess ? d.AccessKeys : null,
        includeAccess ? d.AccessVersion : null);
}

public sealed record DocumentPage(IReadOnlyList<DocumentItem> Items, string? NextPageToken, string? ChangeCursor);

public sealed record ChangeItem(
    string Id,
    string Op,
    string? Path = null,
    string? Title = null,
    string? ContentType = null,
    long? Size = null,
    string? Version = null,
    DateTimeOffset? ModifiedAt = null,
    IReadOnlyList<string>? AccessKeys = null,
    string? AccessVersion = null)
{
    public static ChangeItem Delete(string id) => new(id, "delete");

    public static ChangeItem Upsert(DocumentItem d) => new(
        d.Id, "upsert", d.Path, d.Title, d.ContentType, d.Size, d.Version, d.ModifiedAt, d.AccessKeys, d.AccessVersion);
}

public sealed record ChangePage(IReadOnlyList<ChangeItem> Items, string? NextPageToken, string? NextCursor);
