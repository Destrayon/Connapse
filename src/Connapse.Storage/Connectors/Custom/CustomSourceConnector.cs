using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Storage.Connectors.Custom;

/// <summary>
/// A custom source: one scope of a customer-run middle man, read over custom source protocol v1.
/// <para>
/// A document's path is <c>/{escaped id}/{file name}</c>. The ID comes first so the bytes can be fetched
/// from the path alone and so a deletion, which the protocol reports by ID, can be applied as the prefix
/// <c>/{escaped id}/</c>. The file name carries the extension the parsers are chosen by.
/// </para>
/// <para>
/// One connector for every custom source. Without a connection (<paramref name="permissionsFor"/> null) its
/// documents carry no resource URI, like public GitHub documents: everyone who can search sources may read
/// them. On a custom connection they carry <c>custom://{source}/{id}</c> and the middle man's access keys,
/// which is what search filters and checks them by.
/// </para>
/// </summary>
/// <param name="permissionsFor">The source's ID when it is on a custom connection; null when content only.</param>
public sealed class CustomSourceConnector(CustomSourceClient client, CustomSourceScope scope, Guid? permissionsFor = null)
    : ISyncCursorConnector
{
    private static readonly Dictionary<string, string> ExtensionByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text/markdown"] = ".md",
        ["text/plain"] = ".txt",
        ["text/html"] = ".html",
        ["application/pdf"] = ".pdf",
        ["application/json"] = ".json",
        ["text/csv"] = ".csv",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
    };

    public ConnectorType Type => ConnectorType.Custom;

    /// <summary>The source whose documents carry URIs and keys, when it is on a custom connection.</summary>
    public Guid? PermissionsFor => permissionsFor;

    public bool SupportsLiveWatch => false;

    public async Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct = default)
    {
        try
        {
            // Read every sync: a middle man may gain or lose its change feed between cycles. The feed is
            // used only when it also reports deletions; otherwise a completed full listing is the only way
            // to find them, so every sync lists in full.
            CustomDiscovery discovery = await client.GetDiscoveryAsync(ct);
            bool changes = discovery.Capabilities is { Changes: true, Tombstones: true };

            return cursor is null || !changes
                ? await FullListingAsync(changes, ct)
                : await ChangesSinceAsync(cursor, ct);
        }
        catch (CustomSourceCursorExpiredException)
        {
            return new SyncDelta([], [], NextCursor: null, RequiresFullResync: true);
        }
        catch (CustomSourceRateLimitedException ex)
        {
            return new SyncDelta([], [], cursor, RequiresFullResync: false, Notice: ex.Message, Deferred: true);
        }
    }

    public async Task<Stream> ReadFileAsync(string path, CancellationToken ct = default) =>
        await client.OpenContentAsync(scope.ScopeId, IdOf(path), ct);

    public async Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
    {
        IReadOnlyList<ConnectorFile> files = (await FullListingAsync(keepCursor: false, ct)).Upserted;
        return prefix is null ? files : files.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using Stream _ = await ReadFileAsync(path, ct);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public string ResolveJobPath(string relativePath) => relativePath;

    public async IAsyncEnumerable<ConnectorFileEvent> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        throw new NotSupportedException("Custom sources are synced by polling, not watched.");
#pragma warning disable CS0162 // An iterator needs a yield to compile; it is never reached.
        yield break;
#pragma warning restore CS0162
    }

    private async Task<SyncDelta> FullListingAsync(bool keepCursor, CancellationToken ct)
    {
        List<ConnectorFile> files = [];
        PageGuard guard = new("the document listing");
        string? pageToken = null;
        string? changeCursor;
        while (true)
        {
            CustomListingPage page = await client.ListDocumentsAsync(scope.ScopeId, pageToken, ct);
            files.AddRange((page.Items ?? []).Select(ToFile).OfType<ConnectorFile>());
            if (string.IsNullOrEmpty(page.NextPageToken))
            {
                changeCursor = page.ChangeCursor;
                break;
            }
            guard.Next(page.NextPageToken);
            pageToken = page.NextPageToken;
        }

        return new SyncDelta(files, [], keepCursor ? changeCursor : null, RequiresFullResync: false, IsFullListing: true);
    }

    private async Task<SyncDelta> ChangesSinceAsync(string cursor, CancellationToken ct)
    {
        // Only each ID's last change counts: an update followed by a deletion is a deletion, and two renames
        // leave only the second name. Kept in first-seen order so the delta is stable.
        Dictionary<string, CustomDocument> last = new(StringComparer.Ordinal);
        List<string> order = [];
        PageGuard guard = new("the change feed");
        string? pageToken = null;
        string? nextCursor;
        while (true)
        {
            CustomChangePage page = await client.ListChangesAsync(scope.ScopeId, cursor, pageToken, ct);
            foreach (CustomDocument item in page.Items ?? [])
            {
                if (string.IsNullOrEmpty(item.Id)) continue;
                if (!last.ContainsKey(item.Id)) order.Add(item.Id);
                last[item.Id] = item;
            }

            if (string.IsNullOrEmpty(page.NextPageToken))
            {
                nextCursor = page.NextCursor;
                break;
            }
            guard.Next(page.NextPageToken);
            pageToken = page.NextPageToken;
        }

        // Every changed ID clears whatever it was indexed under before — a deletion, or a rename that moved
        // the document to a new file name. The sync keeps the path upserted alongside.
        List<ConnectorFile> upserted = order
            .Select(id => last[id])
            .Where(item => item.Op == "upsert")
            .Select(ToFile)
            .OfType<ConnectorFile>()
            .ToList();

        return new SyncDelta(
            upserted, [], nextCursor ?? cursor, RequiresFullResync: false,
            DeletedPrefixes: order.Select(PrefixOf).ToList());
    }

    private ConnectorFile? ToFile(CustomDocument item)
    {
        if (string.IsNullOrEmpty(item.Id)) return null;

        return new ConnectorFile(
            Path: PrefixOf(item.Id) + FileNameOf(item),
            SizeBytes: item.Size ?? 0,
            LastModified: Signature(item),
            ContentType: item.ContentType,
            ResourceUri: permissionsFor is { } sourceId ? CustomResourceUri.For(sourceId, item.Id) : null,
            AccessKeys: permissionsFor is null ? null : (item.AccessKeys ?? []).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The sync decides a document changed by its size and modified time, while the protocol signals a
    /// change through <c>version</c>. The version's hash is folded into the sub-second part of the
    /// modified time, so a new version always reads as changed even when size and time stay the same.
    /// </summary>
    private static DateTime Signature(CustomDocument item)
    {
        DateTime modified = item.ModifiedAt?.UtcDateTime ?? DateTime.UnixEpoch;
        DateTime seconds = new(modified.Ticks - modified.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(item.Version ?? ""));
        long ticks = BitConverter.ToUInt32(hash, 0) % TimeSpan.TicksPerSecond;
        return seconds.AddTicks(ticks);
    }

    private static string FileNameOf(CustomDocument item)
    {
        string raw = (item.Path ?? item.Title ?? "").Replace('\\', '/');
        string name = raw[(raw.LastIndexOf('/') + 1)..].Trim();
        if (name.Length == 0 || name is "." or "..") name = "document";

        if (Path.HasExtension(name)) return name;
        string? mediaType = item.ContentType?.Split(';')[0].Trim();
        return mediaType is not null && ExtensionByContentType.TryGetValue(mediaType, out string? extension)
            ? name + extension
            : name;
    }

    private static string PrefixOf(string id) => $"/{Uri.EscapeDataString(id)}/";

    private static string IdOf(string path)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new FileNotFoundException($"'{path}' is not a custom source document path.", path);
        return Uri.UnescapeDataString(segments[0]);
    }
}
