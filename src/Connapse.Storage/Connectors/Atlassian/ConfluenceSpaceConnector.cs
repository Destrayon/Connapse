using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
/// <param name="IncludeAttachments">Whether page attachments are indexed too.</param>
/// <param name="MaxAttachmentMb">Attachments larger than this are skipped.</param>
public sealed record ConfluenceSpaceConfig(
    AtlassianSite Site,
    string SpaceId,
    string SpaceKey,
    string StatePath,
    bool IncludeAttachments = true,
    int MaxAttachmentMb = 25);

/// <summary>
/// Syncs a Confluence space's current pages and blog posts. Every cycle lists the whole space
/// without bodies (a page of 250 per request), records what it saw in the page-state store, and
/// hands the sync engine a full listing: the engine re-ingests only what changed signature and
/// deletes what is gone, so a page trashed, archived or moved to another space drops out the
/// same way. Paths are id-based, so a rename or move inside the space never re-ingests.
/// </summary>
public sealed partial class ConfluenceSpaceConnector(
    ConfluenceSpaceConfig config,
    AtlassianApiClient api,
    ILogger? logger = null,
    TimeProvider? clock = null,
    IFileTypeValidator? fileTypes = null)
    : ISyncCursorConnector
{
    /// <summary>How far up the tree a breadcrumb or folder lookup climbs before it gives up.</summary>
    internal const int MaxAncestors = 50;

    /// <summary>How often the titles of folders in use are read again, to catch a renamed folder.</summary>
    internal static readonly TimeSpan FolderRefreshInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a page's attachment list is trusted. Deleted attachments are invisible to the
    /// change query, so every page's list is read again once a day.
    /// </summary>
    internal static readonly TimeSpan AttachmentSweepInterval = TimeSpan.FromHours(24);

    private const string ListQuery = "status=current&limit=250";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConfluencePageStateStore _store = new(config.StatePath);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Dictionary<string, string> _userNames = new(StringComparer.Ordinal);

    internal ConfluenceSpaceConfig Config => config;

    public ConnectorType Type => ConnectorType.Atlassian;

    public bool SupportsLiveWatch => false;

    public async Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct = default)
    {
        var previous = cursor is null ? null : ConfluenceCursor.TryParse(cursor);
        if (cursor is not null && previous is null)
            return new SyncDelta([], [], NextCursor: null, RequiresFullResync: true);

        // A null cursor is a fresh start: what the store holds is from before a resync and is not
        // trusted. It is replaced only once a listing has completed, though — emptying it first
        // would leave ingestion jobs already queued with nothing to read if this cycle stops.
        bool fresh = cursor is null;
        DateTimeOffset started = _clock.GetUtcNow();
        var state = fresh ? new ConfluenceSyncState() : _store.LoadState();
        var listed = new Dictionary<string, ConfluenceStoredPage>(StringComparer.Ordinal);
        Dictionary<string, DateTimeOffset> commented = [];
        HashSet<string> attached = [];

        try
        {
            var space = await api.GetJsonAsync<SpaceResponse>($"api/v2/spaces/{config.SpaceId}", ct);
            state.SpaceName = string.IsNullOrWhiteSpace(space.Name) ? config.SpaceKey : space.Name;

            await ListAsync("pages", "page", listed, ct);
            await ListAsync("blogposts", "blogpost", listed, ct);
            await ResolveFoldersAsync(listed.Values, state, started, ct);

            // A first sync reads every page, comments included, so it has nothing to catch up on.
            if (previous?.Watermark is { } watermark)
                (commented, attached) = await ChangedPagesAsync(watermark, ct);
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

        var stored = Merge(listed, fresh, commented, attached);

        // After the listing is settled, and outside its all-or-nothing rule: a page whose list
        // could not be read keeps the attachments it had, so nothing is deleted for it.
        string? stopped = config.IncludeAttachments
            ? await RefreshAttachmentsAsync(listed.Values, state, started, ct)
            : null;

        Save(listed, stored, state);
        state.Watermark = started;
        _store.SaveState(state);

        var files = Listing(null, out int skipped);
        string? skippedNote = skipped > 0
            ? $"{skipped} attachment{(skipped == 1 ? "" : "s")} skipped: over {config.MaxAttachmentMb} MB or unsupported type"
            : null;
        string? notice = string.Join(" ", new[] { skippedNote, stopped }.Where(n => n is not null));

        return new SyncDelta(
            files, [], new ConfluenceCursor(started).Serialize(), RequiresFullResync: false, IsFullListing: true,
            Notice: notice.Length > 0 ? notice : null);
    }

    public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default) =>
        Task.FromResult(Listing(prefix, out _));

    /// <summary>
    /// Fetches the page's storage-format body and renders it under its breadcrumb. Only storage
    /// format is ever asked for: the rendered views expand include macros as the service account,
    /// which would put restricted pages' text into less restricted ones.
    /// </summary>
    public async Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        if (FindAttachment(path) is { } found)
        {
            // Confluence answers with a redirect to its media service, which the client follows
            // only onto Atlassian's own hosts.
            return await api.DownloadAsync(
                $"rest/api/content/{found.Page.Id}/child/attachment/att{found.Attachment.Id}/download", ct);
        }

        var page = Find(path)
            ?? throw new FileNotFoundException(
                $"'{LogSanitizer.Sanitize(path)}' is not a synced page of Confluence space {LogSanitizer.Sanitize(config.SpaceKey)}.");

        string endpoint = page.Kind == "blogpost" ? "blogposts" : "pages";
        ContentResponse content;
        try
        {
            content = await api.GetJsonAsync<ContentResponse>($"api/v2/{endpoint}/{page.Id}?body-format=storage", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Confluence {page.Kind} {page.Id} no longer exists.", ex);
        }

        // Moved out between the listing and this read: not this source's to index any more.
        if (content.SpaceId is { } spaceId && spaceId != config.SpaceId)
            throw new FileNotFoundException($"Confluence {page.Kind} {page.Id} is no longer in this space.");

        string body = content.Body?.Storage?.Value ?? "";
        var comments = await CommentsAsync(endpoint, page, ct);

        var accounts = new HashSet<string>(ConfluenceStorageRenderer.MentionedAccountIds(body), StringComparer.Ordinal);
        foreach (var comment in comments)
        {
            accounts.UnionWith(ConfluenceStorageRenderer.MentionedAccountIds(comment.Body));
            if (comment.AuthorId is not null)
                accounts.Add(comment.AuthorId);
        }

        var names = await UserNamesAsync(accounts, ct);
        var breadcrumb = Breadcrumb(page, _store.LoadState(), _store.Load);
        var rendered = ConfluenceStorageRenderer.Render(new ConfluenceRenderInput(
            breadcrumb,
            body,
            [.. comments.Select(c => new ConfluenceComment(
                c.AuthorId is not null && names.TryGetValue(c.AuthorId, out string? name) ? name : "Unknown user",
                c.Created,
                c.Body))],
            names));
        return new MemoryStream(Encoding.UTF8.GetBytes(rendered.Markdown));
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(Find(path) is not null || FindAttachment(path) is not null);

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

    private static IEnumerable<ListedContent> ReadResults(JsonElement root) =>
        root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(e => e.Deserialize<ListedContent>(Json)).OfType<ListedContent>().ToList()
            : [];

    /// <summary>
    /// Learns the title and parent of every folder a listed page sits under, climbing nested
    /// folders. Known folders are reused from the state and read again once every
    /// <see cref="FolderRefreshInterval"/>, so a renamed folder reaches its pages' breadcrumbs.
    /// A folder that answered 403 or 404 is remembered as unavailable until that refresh too.
    /// Folders no listed page reaches any more are dropped. Nothing is written to
    /// <paramref name="state"/> until every lookup has finished, so a rate limit midway leaves it as it was.
    /// </summary>
    private async Task ResolveFoldersAsync(
        IEnumerable<ConfluenceStoredPage> pages, ConfluenceSyncState state, DateTimeOffset now, CancellationToken ct)
    {
        bool refresh = state.FoldersRefreshedAt is not { } last || now - last >= FolderRefreshInterval;

        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        var parents = new Dictionary<string, ConfluenceFolderParent>(StringComparer.Ordinal);
        var unavailable = new HashSet<string>(StringComparer.Ordinal);

        var pending = new Queue<(string Id, int Depth)>(
            pages.Where(p => p.ParentType == "folder" && p.ParentId is not null)
                .Select(p => (p.ParentId!, 0)));

        while (pending.TryDequeue(out var next))
        {
            if (next.Depth >= MaxAncestors || titles.ContainsKey(next.Id) || unavailable.Contains(next.Id))
                continue;

            if (!refresh && state.FolderUnavailable.Contains(next.Id))
            {
                unavailable.Add(next.Id);
                continue;
            }

            ConfluenceFolderParent parent;
            if (!refresh && state.FolderTitles.TryGetValue(next.Id, out string? known))
            {
                titles[next.Id] = known;
                parent = state.FolderParents.GetValueOrDefault(next.Id) ?? new ConfluenceFolderParent(null, null);
            }
            else
            {
                FolderResponse folder;
                try
                {
                    folder = await api.GetJsonAsync<FolderResponse>($"api/v2/folders/{next.Id}", ct);
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                {
                    // The breadcrumb just stops here; the page itself is still listed.
                    unavailable.Add(next.Id);
                    continue;
                }

                titles[next.Id] = folder.Title ?? "";
                parent = new ConfluenceFolderParent(
                    ConfluencePageStateStore.IsContentId(folder.ParentId) ? folder.ParentId : null, folder.ParentType);
            }

            parents[next.Id] = parent;
            if (parent.ParentType == "folder" && parent.ParentId is not null)
                pending.Enqueue((parent.ParentId, next.Depth + 1));
        }

        state.FolderTitles = titles;
        state.FolderParents = parents;
        state.FolderUnavailable = unavailable;
        if (refresh)
            state.FoldersRefreshedAt = now;
    }

    /// <summary>
    /// Asks CQL which comments (and, with attachments on, which attachments) changed since a day
    /// before <paramref name="watermark"/>. Returns the newest comment change per page, and the
    /// pages whose attachments changed. The day of overlap absorbs CQL's unstated timezone; a hit
    /// reported twice is harmless because a comment time only moves forward and an attachment hit
    /// only re-reads the page's list.
    /// </summary>
    private async Task<(Dictionary<string, DateTimeOffset> Commented, HashSet<string> Attached)> ChangedPagesAsync(
        DateTimeOffset watermark, CancellationToken ct)
    {
        string since = (watermark - TimeSpan.FromDays(1)).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        string types = config.IncludeAttachments ? "type in (comment, attachment)" : "type=comment";
        string cql = $"space=\"{CqlQuote(config.SpaceKey)}\" AND {types} AND lastmodified >= \"{since}\"";
        string url = $"rest/api/search?cql={Uri.EscapeDataString(cql)}&limit=100&expand=content.container";

        var commented = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var attached = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var hit in api.PageAsync(url, ReadSearchResults, ct))
        {
            string? pageId = hit.Content?.Container?.Id;
            if (!ConfluencePageStateStore.IsContentId(pageId))
                continue;

            if (hit.Content!.Type == "attachment")
                attached.Add(pageId!);
            else if (hit.LastModified is { } at && (!commented.TryGetValue(pageId!, out var known) || at > known))
                commented[pageId!] = at;
        }

        return (commented, attached);
    }

    /// <summary>A value for inside a CQL double-quoted string.</summary>
    private static string CqlQuote(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static IEnumerable<SearchHit> ReadSearchResults(JsonElement root) =>
        root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(e => e.Deserialize<SearchHit>(Json)).OfType<SearchHit>().ToList()
            : [];

    /// <summary>
    /// Fills each listed page with what earlier cycles learned and the change query reported, and
    /// returns the stored records to compare against. On a fresh start comment times are not
    /// trusted, but attachment lists are kept (marked due) so a listing cut short deletes nothing.
    /// </summary>
    private Dictionary<string, ConfluenceStoredPage> Merge(
        Dictionary<string, ConfluenceStoredPage> listed, bool fresh,
        Dictionary<string, DateTimeOffset> commented, HashSet<string> attached)
    {
        var stored = new Dictionary<string, ConfluenceStoredPage>(StringComparer.Ordinal);
        foreach (var page in listed.Values)
        {
            if (_store.Load(page.Id) is { } existing)
            {
                stored[page.Id] = existing;
                page.Attachments = existing.Attachments;
                page.AttachmentsListedAt = fresh ? null : existing.AttachmentsListedAt;
                if (!fresh)
                    page.LastCommentAt = existing.LastCommentAt;
            }

            // Only ever moves forward, so a comment the one-day overlap reports again changes nothing.
            if (commented.TryGetValue(page.Id, out var at) && (page.LastCommentAt is null || at > page.LastCommentAt))
                page.LastCommentAt = at;

            if (attached.Contains(page.Id))
                page.AttachmentsListedAt = null;
        }

        return stored;
    }

    /// <summary>Writes the records that changed, deletes the ones no longer listed, and indexes attachments by id.</summary>
    private void Save(
        Dictionary<string, ConfluenceStoredPage> listed, Dictionary<string, ConfluenceStoredPage> stored, ConfluenceSyncState state)
    {
        foreach (var page in listed.Values)
        {
            if (!stored.TryGetValue(page.Id, out var existing)
                || !JsonSerializer.SerializeToUtf8Bytes(existing, Json).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(page, Json)))
                _store.Save(page);
        }

        foreach (string id in _store.Ids().Where(id => !listed.ContainsKey(id)).ToList())
            _store.Delete(id);

        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var page in listed.Values)
            foreach (var attachment in page.Attachments)
                owners[attachment.Id] = page.Id;
        state.AttachmentPages = owners;
    }

    /// <summary>
    /// Reads the attachment list of every page that is due: never read, named by an attachment
    /// change, or older than <see cref="AttachmentSweepInterval"/>. A rate limit stops the reading
    /// and is returned as a notice; every page not reached keeps its previous list and stays due.
    /// </summary>
    private async Task<string?> RefreshAttachmentsAsync(
        IEnumerable<ConfluenceStoredPage> pages, ConfluenceSyncState state, DateTimeOffset now, CancellationToken ct)
    {
        var due = pages
            .Where(p => p.AttachmentsListedAt is not { } at || now - at >= AttachmentSweepInterval)
            .OrderBy(p => p.AttachmentsListedAt.HasValue)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
        bool sweeping = state.LastAttachmentSweepAt is null || due.Any(p => p.AttachmentsListedAt is not null);
        bool complete = true;

        foreach (var page in due)
        {
            try
            {
                page.Attachments = await ListAttachmentsAsync(page, ct);
                page.AttachmentsListedAt = now;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                // Gone or hidden since the listing; the next listing settles which.
                complete = false;
            }
            catch (AtlassianRateLimitedException ex)
            {
                _logger.LogWarning(
                    "Confluence space {SpaceKey}: rate limited while listing attachments; {Remaining} page(s) keep their previous list until next cycle",
                    LogSanitizer.Sanitize(config.SpaceKey), due.Count - due.IndexOf(page));
                return ex.Message;
            }
        }

        // Recorded only when a sweep of aged lists ran to the end.
        if (complete && sweeping)
            state.LastAttachmentSweepAt = now;
        return null;
    }

    private async Task<List<ConfluenceStoredAttachment>> ListAttachmentsAsync(ConfluenceStoredPage page, CancellationToken ct)
    {
        string endpoint = page.Kind == "blogpost" ? "blogposts" : "pages";
        var attachments = new List<ConfluenceStoredAttachment>();

        await foreach (var item in api.PageAsync($"api/v2/{endpoint}/{page.Id}/attachments?limit=250", ReadAttachments, ct))
        {
            // Confluence ids attachments "att" plus digits; the digits are what addresses carry.
            string? id = item.Id is { } raw && raw.StartsWith("att", StringComparison.Ordinal) ? raw[3..] : item.Id;
            if (!ConfluencePageStateStore.IsContentId(id) || string.IsNullOrWhiteSpace(item.Title))
                continue;

            attachments.Add(new ConfluenceStoredAttachment
            {
                Id = id!,
                Version = item.Version?.Number ?? 0,
                VersionAt = item.Version?.CreatedAt ?? DateTimeOffset.MinValue,
                FileName = item.Title,
                MediaType = item.MediaType,
                Size = item.FileSize ?? 0,
            });
        }

        return attachments;
    }

    private static IEnumerable<AttachmentResponse> ReadAttachments(JsonElement root) =>
        root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(e => e.Deserialize<AttachmentResponse>(Json)).OfType<AttachmentResponse>().ToList()
            : [];

    // ── Documents ──────────────────────────────────────────────────────────

    /// <summary>
    /// Every stored page, and with attachments on, every attachment of theirs that is within the
    /// size cap and has a parser. <paramref name="skipped"/> counts the attachments left out.
    /// </summary>
    private IReadOnlyList<ConnectorFile> Listing(string? prefix, out int skipped)
    {
        string? normalized = string.IsNullOrEmpty(prefix) ? null : "/" + prefix.Trim('/') + "/";
        var pages = _store.Ids()
            .Select(_store.Load)
            .OfType<ConfluenceStoredPage>()
            .ToDictionary(p => p.Id, StringComparer.Ordinal);
        var state = _store.LoadState();
        var files = new List<ConnectorFile>();
        skipped = 0;

        foreach (var page in pages.Values)
        {
            var breadcrumb = Breadcrumb(page, state, pages.GetValueOrDefault);
            files.Add(ToConnectorFile(page, breadcrumb));

            if (!config.IncludeAttachments)
                continue;

            foreach (var attachment in page.Attachments)
            {
                if (IsIndexable(attachment))
                    files.Add(ToConnectorFile(page, attachment, breadcrumb));
                else
                    skipped++;
            }
        }

        return
        [
            .. files
                .Where(f => normalized is null || f.Path.StartsWith(normalized, StringComparison.Ordinal))
                .OrderBy(f => f.Path, StringComparer.Ordinal),
        ];
    }

    private bool IsIndexable(ConfluenceStoredAttachment attachment) =>
        attachment.Size <= config.MaxAttachmentMb * 1024L * 1024L
        && fileTypes is not null
        && fileTypes.IsSupported(SafeFileName(attachment.FileName));

    /// <summary>
    /// An attachment as a document: its real size and version time are the signature, and its
    /// address is the page's, so a search hit on it is checked against the page.
    /// </summary>
    private ConnectorFile ToConnectorFile(
        ConfluenceStoredPage page, ConfluenceStoredAttachment attachment, IReadOnlyList<string> breadcrumb) => new(
        Path: AttachmentPath(attachment),
        SizeBytes: attachment.Size,
        LastModified: attachment.VersionAt.UtcDateTime,
        ContentType: attachment.MediaType,
        ResourceUri: AtlassianUri.ForAttachment(config.Site.CloudId, page.Id, attachment.Id),
        Metadata: new Dictionary<string, string>
        {
            ["confluence:spaceKey"] = config.SpaceKey,
            ["confluence:title"] = attachment.FileName,
            ["confluence:pageId"] = page.Id,
            ["confluence:breadcrumb"] = string.Join(" > ", breadcrumb.Append(attachment.FileName)),
            ["confluence:url"] = $"{config.Site.SiteUrl}/wiki/pages/viewpageattachments.action?pageId={page.Id}",
        },
        Strategy: null);

    internal static string AttachmentPath(ConfluenceStoredAttachment attachment) =>
        $"/attachments/{attachment.Id}/{SafeFileName(attachment.FileName)}";

    /// <summary>
    /// A file name safe to use as the last path segment: no separators, control characters or
    /// characters Windows refuses, no run of dots, no leading or trailing dots or spaces, at most
    /// 200 characters with the extension kept. The same name always comes out the same.
    /// </summary>
    internal static string SafeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);

        string safe = DotRun().Replace(sb.ToString(), ".").Trim(' ', '.');
        if (safe.Length > 200)
        {
            string extension = Path.GetExtension(safe);
            safe = safe[..(200 - Math.Min(extension.Length, 20))] + extension[..Math.Min(extension.Length, 20)];
        }

        return safe.Length == 0 ? "attachment" : safe;
    }

    [GeneratedRegex(@"\.{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex DotRun();

    private ConnectorFile ToConnectorFile(ConfluenceStoredPage page, IReadOnlyList<string> breadcrumb)
    {
        DateTimeOffset modified = page.LastCommentAt is { } commented && commented > page.VersionAt
            ? commented
            : page.VersionAt;

        return new ConnectorFile(
            Path: PathFor(page),
            // Not a size: the page's version in the high half and a hash of its breadcrumb in the
            // low half. With the time below it is the signature the sync engine compares, so an
            // unchanged page is never fetched again, while a page whose space, ancestor or folder
            // was renamed, or which moved under another parent, is re-ingested with its new
            // breadcrumb even though its own version did not move.
            SizeBytes: Signature(page.Version, breadcrumb),
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

    /// <summary>
    /// The version shifted into the high 32 bits, with the first four bytes of a SHA-256 of the
    /// breadcrumb below it. Deterministic across processes, unlike <see cref="string.GetHashCode()"/>.
    /// </summary>
    internal static long Signature(int version, IReadOnlyList<string> breadcrumb)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', breadcrumb)));
        return ((long)Math.Max(version, 0) << 32) | BinaryPrimitives.ReadUInt32BigEndian(hash);
    }

    internal static string PathFor(ConfluenceStoredPage page) =>
        page.Kind == "blogpost" ? $"/blogposts/{page.Id}.md" : $"/pages/{page.Id}.md";

    [GeneratedRegex(@"^/(?<kind>pages|blogposts)/(?<id>[0-9]{1,19})\.md\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^/attachments/(?<id>[0-9]{1,19})/(?<name>[^/]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentPattern();

    /// <summary>
    /// The page and attachment an attachment path names, or null unless it is one the listing would
    /// emit now: same file name, attachments on, within the cap, and parseable.
    /// </summary>
    private (ConfluenceStoredPage Page, ConfluenceStoredAttachment Attachment)? FindAttachment(string path)
    {
        var match = AttachmentPattern().Match(path ?? "");
        if (!match.Success || !config.IncludeAttachments)
            return null;

        string id = match.Groups["id"].Value;
        if (!_store.LoadState().AttachmentPages.TryGetValue(id, out string? pageId) || _store.Load(pageId) is not { } page)
            return null;

        var attachment = page.Attachments.FirstOrDefault(a => a.Id == id);
        return attachment is not null && AttachmentPath(attachment) == path && IsIndexable(attachment)
            ? (page, attachment)
            : null;
    }

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

    // ── Reading ────────────────────────────────────────────────────────────

    /// <summary>
    /// Space name, then each ancestor page or folder from the top down, then the title. Walks the
    /// stored parent links, so it costs no requests; stops at an unknown parent, a cycle, or
    /// <see cref="MaxAncestors"/> levels. The listing and the read share it, so the breadcrumb the
    /// signature hashes is the one the page is rendered with.
    /// </summary>
    private List<string> Breadcrumb(
        ConfluenceStoredPage page, ConfluenceSyncState state, Func<string, ConfluenceStoredPage?> pages)
    {
        string title = page.Title;
        string space = string.IsNullOrWhiteSpace(state.SpaceName) ? config.SpaceKey : state.SpaceName;
        if (page.Kind == "blogpost")
            return [space, "Blog", title];

        var ancestors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { "page:" + page.Id };
        string? id = page.ParentId;
        string? type = page.ParentType;

        while (id is not null && ancestors.Count < MaxAncestors && seen.Add(type + ":" + id))
        {
            if (type == "folder" && state.FolderTitles.TryGetValue(id, out string? folderTitle))
            {
                ancestors.Add(folderTitle);
                var parent = state.FolderParents.GetValueOrDefault(id);
                (id, type) = (parent?.ParentId, parent?.ParentType);
            }
            else if (type == "page" && pages(id) is { Kind: "page" } parentPage)
            {
                ancestors.Add(parentPage.Title);
                (id, type) = (parentPage.ParentId, parentPage.ParentType);
            }
            else
            {
                break;
            }
        }

        ancestors.Reverse();
        return [space, .. ancestors, title];
    }

    /// <summary>
    /// The page's footer comments, and for a page its inline comments too, oldest first. Storage
    /// format only, like the body.
    /// </summary>
    private async Task<List<PageComment>> CommentsAsync(string endpoint, ConfluenceStoredPage page, CancellationToken ct)
    {
        var comments = new List<PageComment>();
        string[] kinds = page.Kind == "blogpost" ? ["footer"] : ["footer", "inline"];

        foreach (string kind in kinds)
        {
            string url = $"api/v2/{endpoint}/{page.Id}/{kind}-comments?body-format=storage";
            await foreach (var comment in api.PageAsync(url, ReadComments, ct))
            {
                comments.Add(new PageComment(
                    comment.Version?.AuthorId,
                    comment.Version?.CreatedAt ?? DateTimeOffset.MinValue,
                    comment.Body?.Storage?.Value ?? ""));
            }
        }

        return [.. comments.OrderBy(c => c.Created)];
    }

    private static IEnumerable<CommentResponse> ReadComments(JsonElement root) =>
        root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(e => e.Deserialize<CommentResponse>(Json)).OfType<CommentResponse>().ToList()
            : [];

    /// <summary>
    /// Display names for mentioned accounts, looked up a hundred at a time and kept for this
    /// connector's lifetime. A failed lookup leaves the names out rather than failing the page.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> UserNamesAsync(IReadOnlySet<string> accountIds, CancellationToken ct)
    {
        foreach (string[] batch in accountIds.Where(a => !_userNames.ContainsKey(a)).Chunk(100))
        {
            string query = string.Join('&', batch.Select(a => "accountId=" + Uri.EscapeDataString(a)));
            try
            {
                var users = await api.GetJsonAsync<UserBulkResponse>($"rest/api/user/bulk?{query}", ct);
                foreach (var user in users.Results ?? [])
                {
                    string? name = string.IsNullOrWhiteSpace(user.DisplayName) ? user.PublicName : user.DisplayName;
                    if (user.AccountId is not null && !string.IsNullOrWhiteSpace(name))
                        _userNames[user.AccountId] = name;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    "Confluence space {SpaceKey}: looking up mentioned users failed (HTTP {Status}); rendering without their names",
                    LogSanitizer.Sanitize(config.SpaceKey), (int?)ex.StatusCode);
            }
        }

        return _userNames;
    }

    // ── Wire shapes ────────────────────────────────────────────────────────

    private sealed record StorageBody(string? Value, string? Representation);

    private sealed record ContentBody(StorageBody? Storage);

    private sealed record ContentResponse(string? Id, string? Title, string? SpaceId, ContentBody? Body);

    private sealed record UserResponse(string? AccountId, string? DisplayName, string? PublicName);

    private sealed record UserBulkResponse(List<UserResponse>? Results);

    private sealed record SpaceResponse(string? Id, string? Key, string? Name);

    private sealed record VersionInfo(int Number, DateTimeOffset? CreatedAt);

    private sealed record ListedContent(string? Id, string? Title, string? ParentId, string? ParentType, VersionInfo? Version);

    private sealed record FolderResponse(string? Id, string? Title, string? ParentId, string? ParentType);

    private sealed record SearchContainer(string? Id, string? Type);

    private sealed record SearchContent(string? Id, string? Type, SearchContainer? Container);

    private sealed record SearchHit(SearchContent? Content, DateTimeOffset? LastModified);

    private sealed record CommentVersion(DateTimeOffset? CreatedAt, string? AuthorId);

    private sealed record CommentResponse(string? Id, CommentVersion? Version, ContentBody? Body);

    private sealed record PageComment(string? AuthorId, DateTimeOffset Created, string Body);

    private sealed record AttachmentResponse(string? Id, string? Title, string? MediaType, long? FileSize, VersionInfo? Version);

    /// <summary>The cursor: when the last complete listing started, which the next change query counts back from.</summary>
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
