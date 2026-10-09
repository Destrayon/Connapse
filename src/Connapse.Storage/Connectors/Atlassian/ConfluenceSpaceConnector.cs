using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.CloudScope;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>One Confluence space as a source.</summary>
/// <param name="Site">The site the space lives on.</param>
/// <param name="SpaceId">The space's numeric id.</param>
/// <param name="SpaceKey">The space's key, carried into each document's metadata.</param>
/// <param name="StatePath">This source's own page-state directory.</param>
public sealed record ConfluenceSpaceConfig(AtlassianSite Site, string SpaceId, string SpaceKey, string StatePath);

/// <summary>
/// Syncs a Confluence space's current pages and blog posts. Every cycle lists the whole space
/// without bodies (a page of 250 per request), records what it saw in the page-state store, and
/// hands the sync engine a full listing: the engine re-ingests only what changed signature and
/// deletes what is gone, so a page trashed, archived or moved to another space drops out the
/// same way. Paths are id-based, so a rename or move inside the space never re-ingests.
/// </summary>
public sealed partial class ConfluenceSpaceConnector(
    ConfluenceSpaceConfig config, AtlassianApiClient api, ILogger? logger = null, TimeProvider? clock = null)
    : ISyncCursorConnector
{
    /// <summary>How far up the tree a breadcrumb or folder lookup climbs before it gives up.</summary>
    internal const int MaxAncestors = 50;

    private const string ListQuery = "status=current&limit=250";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConfluencePageStateStore _store = new(config.StatePath);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public ConnectorType Type => ConnectorType.Atlassian;

    public bool SupportsLiveWatch => false;

    public async Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct = default)
    {
        if (cursor is null)
        {
            // A fresh start: whatever the store holds is from before a resync and not trusted.
            _store.Reset();
        }
        else if (ConfluenceCursor.TryParse(cursor) is null)
        {
            return new SyncDelta([], [], NextCursor: null, RequiresFullResync: true);
        }

        DateTimeOffset started = _clock.GetUtcNow();
        var state = _store.LoadState();
        var listed = new Dictionary<string, ConfluenceStoredPage>(StringComparer.Ordinal);

        try
        {
            var space = await api.GetJsonAsync<SpaceResponse>($"api/v2/spaces/{config.SpaceId}", ct);
            state.SpaceName = string.IsNullOrWhiteSpace(space.Name) ? config.SpaceKey : space.Name;

            await ListAsync("pages", "page", listed, ct);
            await ListAsync("blogposts", "blogpost", listed, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            // Deleted, or the service account lost access. Hidden from search, not deleted: a
            // permission slip should not wipe the index.
            throw new SourceAccessRevokedException(
                $"Confluence space {LogSanitizer.Sanitize(config.SpaceKey)} can no longer be read (HTTP {(int)ex.StatusCode!}).", ex);
        }
        catch (AtlassianRateLimitedException ex)
        {
            // Nothing is applied from a listing that stopped partway: it would read as deletions.
            _logger.LogWarning(
                "Confluence space {SpaceKey}: rate limited; keeping the last listing and retrying next cycle",
                LogSanitizer.Sanitize(config.SpaceKey));
            return new SyncDelta([], [], cursor, RequiresFullResync: false, Notice: ex.Message);
        }

        await ResolveFoldersAsync(listed.Values, state, ct);
        Apply(listed);

        state.Watermark = started;
        _store.SaveState(state);

        return new SyncDelta(
            List(null), [], new ConfluenceCursor(started).Serialize(), RequiresFullResync: false, IsFullListing: true);
    }

    public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default) =>
        Task.FromResult(List(prefix));

    public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException("Reading Confluence pages is not available yet.");

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(Find(path) is not null);

    /// <summary>Returned unchanged: a document's path is already the id-based form this connector reads.</summary>
    public string ResolveJobPath(string relativePath) => relativePath;

    public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Confluence sources are polled; they do not support live watch.");

    // ── Listing ────────────────────────────────────────────────────────────

    private async Task ListAsync(
        string endpoint, string kind, Dictionary<string, ConfluenceStoredPage> listed, CancellationToken ct)
    {
        string url = $"api/v2/spaces/{config.SpaceId}/{endpoint}?{ListQuery}";

        await foreach (var item in api.PageAsync(url, ReadResults, ct))
        {
            if (!ConfluencePageStateStore.IsContentId(item.Id))
                continue;

            listed[item.Id!] = new ConfluenceStoredPage
            {
                Id = item.Id!,
                Kind = kind,
                Version = item.Version?.Number ?? 0,
                VersionAt = item.Version?.CreatedAt ?? DateTimeOffset.MinValue,
                Title = item.Title ?? "",
                ParentId = ConfluencePageStateStore.IsContentId(item.ParentId) ? item.ParentId : null,
                ParentType = item.ParentType,
            };
        }
    }

    /// <summary>
    /// One listing page's entries. Anything malformed throws rather than being skipped: a listing
    /// is applied as the whole space, so a dropped entry would read as a deleted page.
    /// </summary>
    private static List<ListedContent> ReadResults(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
            throw new AtlassianMalformedResponseException("Confluence returned a listing with no results array.");

        var items = new List<ListedContent>();
        foreach (var entry in results.EnumerateArray())
        {
            ListedContent? item = null;
            if (entry.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    item = entry.Deserialize<ListedContent>(Json);
                }
                catch (JsonException)
                {
                    item = null;
                }
            }

            if (item is null || !ConfluencePageStateStore.IsContentId(item.Id) || item.Version is not { Number: > 0 })
                throw new AtlassianMalformedResponseException("Confluence returned a listing entry without a valid id and version.");

            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// Looks up each folder a listed page sits in whose title is not yet known, then that folder's
    /// own parent folders. One request per folder, ever: titles are cached in the state.
    /// </summary>
    private async Task ResolveFoldersAsync(
        IEnumerable<ConfluenceStoredPage> pages, ConfluenceSyncState state, CancellationToken ct)
    {
        var pending = new Queue<(string Id, int Depth)>(
            pages.Where(p => p.ParentType == "folder" && p.ParentId is not null)
                .Select(p => (p.ParentId!, 0)));

        while (pending.TryDequeue(out var next))
        {
            if (state.FolderTitles.ContainsKey(next.Id) || next.Depth >= MaxAncestors)
                continue;

            FolderResponse folder;
            try
            {
                folder = await api.GetJsonAsync<FolderResponse>($"api/v2/folders/{next.Id}", ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                // The breadcrumb just stops here; the page itself is still listed.
                continue;
            }

            string? parentId = ConfluencePageStateStore.IsContentId(folder.ParentId) ? folder.ParentId : null;
            state.FolderTitles[next.Id] = folder.Title ?? "";
            state.FolderParents[next.Id] = new ConfluenceFolderParent(parentId, folder.ParentType);

            if (folder.ParentType == "folder" && parentId is not null)
                pending.Enqueue((parentId, next.Depth + 1));
        }
    }

    /// <summary>Writes what changed and deletes the records the listing no longer has.</summary>
    private void Apply(Dictionary<string, ConfluenceStoredPage> listed)
    {
        foreach (var page in listed.Values)
        {
            var existing = _store.Load(page.Id);
            if (existing is not null)
            {
                // Kept from earlier cycles: the listing does not carry them.
                page.LastCommentAt = existing.LastCommentAt;
                page.Attachments = existing.Attachments;

                if (existing.Kind == page.Kind && existing.Version == page.Version && existing.VersionAt == page.VersionAt
                    && existing.Title == page.Title && existing.ParentId == page.ParentId && existing.ParentType == page.ParentType)
                    continue;
            }

            _store.Save(page);
        }

        foreach (string id in _store.Ids().Where(id => !listed.ContainsKey(id)).ToList())
            _store.Delete(id);
    }

    // ── Documents ──────────────────────────────────────────────────────────

    private IReadOnlyList<ConnectorFile> List(string? prefix)
    {
        string? normalized = string.IsNullOrEmpty(prefix) ? null : "/" + prefix.Trim('/') + "/";

        return
        [
            .. _store.Ids()
                .Select(_store.Load)
                .OfType<ConfluenceStoredPage>()
                .Select(ToConnectorFile)
                .Where(f => normalized is null || f.Path.StartsWith(normalized, StringComparison.Ordinal))
                .OrderBy(f => f.Path, StringComparer.Ordinal),
        ];
    }

    private ConnectorFile ToConnectorFile(ConfluenceStoredPage page)
    {
        DateTimeOffset modified = page.LastCommentAt is { } commented && commented > page.VersionAt
            ? commented
            : page.VersionAt;

        return new ConnectorFile(
            Path: PathFor(page),
            // Not a size: the version number. With the time below it is the signature the sync
            // engine compares, so an unchanged page is never fetched again.
            SizeBytes: page.Version,
            LastModified: modified.UtcDateTime,
            ContentType: "text/markdown",
            ResourceUri: AtlassianUri.ForPage(config.Site.CloudId, page.Id),
            Metadata: new Dictionary<string, string>
            {
                ["confluence:spaceKey"] = config.SpaceKey,
                ["confluence:title"] = page.Title,
                ["confluence:url"] = $"{config.Site.SiteUrl}/wiki/pages/viewpage.action?pageId={page.Id}",
            },
            // The container's default chunker, which routes markdown to the document-aware one.
            Strategy: null);
    }

    internal static string PathFor(ConfluenceStoredPage page) =>
        page.Kind == "blogpost" ? $"/blogposts/{page.Id}.md" : $"/pages/{page.Id}.md";

    [GeneratedRegex(@"^/(?<kind>pages|blogposts)/(?<id>[0-9]{1,19})\.md\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    /// <summary>The stored record a path names, or null when it is not one this source holds.</summary>
    private ConfluenceStoredPage? Find(string path)
    {
        var match = PathPattern().Match(path ?? "");
        if (!match.Success)
            return null;

        string kind = match.Groups["kind"].Value == "blogposts" ? "blogpost" : "page";
        var page = _store.Load(match.Groups["id"].Value);
        return page?.Kind == kind ? page : null;
    }

    // ── Wire shapes ────────────────────────────────────────────────────────

    private sealed record SpaceResponse(string? Id, string? Key, string? Name);

    private sealed record VersionInfo(int Number, DateTimeOffset? CreatedAt);

    private sealed record ListedContent(string? Id, string? Title, string? ParentId, string? ParentType, VersionInfo? Version);

    private sealed record FolderResponse(string? Id, string? Title, string? ParentId, string? ParentType);

    /// <summary>The cursor: when the last complete listing started.</summary>
    private sealed record ConfluenceCursor(DateTimeOffset? Watermark)
    {
        public string Serialize() => JsonSerializer.Serialize(this, Json);

        public static ConfluenceCursor? TryParse(string text)
        {
            try
            {
                return JsonSerializer.Deserialize<ConfluenceCursor>(text, Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
