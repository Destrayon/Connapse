using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.CloudScope;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>One Jira project as a source.</summary>
/// <param name="Site">The site the project lives on.</param>
/// <param name="ProjectId">The project's numeric id, which every query is scoped by.</param>
/// <param name="ProjectKey">The project's key, carried into each document's metadata.</param>
/// <param name="StatePath">This source's own issue-state directory.</param>
/// <param name="IncludeAttachments">Whether issue attachments are indexed too.</param>
/// <param name="MaxAttachmentMb">Attachments larger than this are skipped.</param>
public sealed record JiraProjectConfig(
    AtlassianSite Site, string ProjectId, string ProjectKey, string StatePath,
    bool IncludeAttachments = true, int MaxAttachmentMb = 25)
{
    /// <summary>The largest attachment cap a source may ask for; attachments are buffered and parsed in memory.</summary>
    public const int MaxAttachmentMbCeiling = 100;
}

/// <summary>
/// Syncs a Jira project's issues as record documents. Every cycle lists the project's issue ids,
/// asks which issues changed since the last cycle, fetches only those, and hands the sync engine
/// a full listing from the issue-state store: the engine re-ingests what changed signature and
/// deletes what is gone, so a deleted issue or one moved to another project drops out the same way.
/// Comments that carry a group or role visibility, and JSM internal notes, are left out of the
/// issue document. Each is its own document instead, whose resource URI carries its restriction
/// (see <see cref="Route"/>).
/// </summary>
public sealed partial class JiraProjectConnector(
    JiraProjectConfig config,
    AtlassianApiClient api,
    ILogger? logger = null,
    TimeProvider? clock = null,
    IFileTypeValidator? fileTypes = null)
    : ISyncCursorConnector
{
    /// <summary>How far back of the watermark the change query starts, to cover search indexing lag.</summary>
    internal const long ChangeOverlapMs = 300_000;

    /// <summary>
    /// Whether every listed issue's comments are re-read once a day. A comment's restriction can
    /// change without the issue's updated time moving, so the change query alone can miss it.
    /// </summary>
    internal static bool SweepEnabled = true;

    /// <summary>The download failure when Atlassian refuses attachment reads with a token that reads issues.</summary>
    public const string AttachmentScopeNotice =
        "Attachments were skipped: the site's credential lacks the read:attachment:jira permission. "
        + "Add it to the credential, or turn off Include attachments.";

    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    private const int BulkFetchSize = 100;

    private static readonly string[] BulkFieldNames = ["summary", "updated", "attachment", "comment"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly JiraIssueStateStore _store = new(config.StatePath);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    internal JiraProjectConfig Config => config;

    // Kept for the attachment phases, which skip attachments without a parser as Confluence does.
    internal IFileTypeValidator? FileTypes => fileTypes;

    public ConnectorType Type => ConnectorType.Atlassian;

    public bool SupportsLiveWatch => false;

    private static bool IsUnavailable(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or AtlassianMalformedResponseException or JsonException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static AtlassianUnavailableException Unavailable(Exception ex) =>
        new(AtlassianSyncErrors.JiraUnavailable, ex switch
        {
            TaskCanceledException => "No answer within the time limit.",
            AtlassianMalformedResponseException => ex.Message,
            _ => ConnectionTesters.AtlassianConnectionTester.Describe(ex),
        }, ex);

    public async Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct = default)
    {
        var previous = cursor is null ? null : JiraCursor.TryParse(cursor);
        if (cursor is not null && previous is null)
            return new SyncDelta([], [], NextCursor: null, RequiresFullResync: true);

        // A null cursor is a fresh start: what the store holds is from before a resync and is not
        // trusted, so every listed issue is fetched again. The store is replaced only once the
        // cycle has completed, as in Confluence, so jobs already queued still find their records.
        bool everything = cursor is null || previous!.WatermarkMs is null;
        DateTimeOffset now = _clock.GetUtcNow();
        bool sweep = everything
            || (SweepEnabled && (previous!.LastCommentSweepAt is not { } swept || now - swept >= SweepInterval));
        long startedMs = now.ToUnixTimeMilliseconds();
        var state = cursor is null ? new JiraSyncState() : _store.LoadState();
        HashSet<string> listed;
        Dictionary<string, JiraStoredIssue> fetched;

        try
        {
            await ReadProjectAsync(state, ct);
            await ReadFieldsAsync(state, ct);

            listed = await ListIdsAsync(ct);
            // A sweep fetches every listed issue again, and with it every comment.
            var changed = new HashSet<string>(
                sweep ? listed : listed.Where(id => _store.Load(id) is null), StringComparer.Ordinal);
            if (!everything)
            {
                foreach (var (id, updated) in await ChangedSinceAsync(previous!.WatermarkMs!.Value, ct))
                {
                    if (listed.Contains(id) && (updated is null || _store.Load(id)?.Updated != updated))
                        changed.Add(id);
                }
            }

            fetched = await FetchAsync(changed, listed, startedMs, ct);
        }
        catch (AtlassianRateLimitedException ex)
        {
            // Nothing is applied from a cycle that stopped partway: it would read as deletions.
            _logger.LogWarning(
                "Jira project {ProjectKey}: rate limited; keeping the last listing and retrying next cycle",
                LogSanitizer.Sanitize(config.ProjectKey));
            return new SyncDelta([], [], cursor, RequiresFullResync: false, Notice: ex.Message, Deferred: true);
        }
        catch (Exception ex) when (IsUnavailable(ex, ct))
        {
            throw Unavailable(ex);
        }

        // A fresh start keeps only what this cycle fetched: a record from before the resync is not
        // trusted, even for an issue that is still listed but that bulkfetch did not return.
        IReadOnlySet<string> keep = cursor is null ? fetched.Keys.ToHashSet(StringComparer.Ordinal) : listed;
        foreach (var issue in fetched.Values)
            _store.Save(issue);
        foreach (string id in _store.Ids().Where(id => !keep.Contains(id)).ToList())
            _store.Delete(id);
        _store.SaveState(state);

        var files = Listing(null, out int skipped);
        string? notice = skipped > 0
            ? $"{skipped} attachment{(skipped == 1 ? " was" : "s were")} {ConfluenceSpaceConnector.SkippedAttachmentsPhrase} "
              + $"{config.MaxAttachmentMb} MB, or a file type Connapse can't read. To include larger files, raise "
              + "Max attachment size on the source."
            : null;

        return new SyncDelta(
            files, [], new JiraCursor(startedMs, sweep ? now : previous?.LastCommentSweepAt).Serialize(),
            RequiresFullResync: false, IsFullListing: true, Notice: notice);
    }

    public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default) =>
        Task.FromResult(Listing(prefix, out _));

    /// <summary>
    /// Fetches the issue with its rich-text fields and every comment, and renders it with only the
    /// comments anyone who can browse the issue can see.
    /// </summary>
    public async Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        if (FindAttachment(path) is { } attachment)
            return await DownloadAttachmentAsync(attachment, ct);

        if (FindComment(path) is { } separate)
            return await ReadCommentAsync(separate.Issue, separate.Comment, ct);

        var stored = Find(path)
            ?? throw new FileNotFoundException(
                $"'{LogSanitizer.Sanitize(path)}' is not a synced issue of Jira project {LogSanitizer.Sanitize(config.ProjectKey)}.");
        var state = _store.LoadState();

        string fields = string.Join(',', state.RichTextFieldIds.Where(IsFieldId).Prepend("*navigable"));
        JsonElement issue;
        try
        {
            issue = await api.GetJsonAsync<JsonElement>($"rest/api/3/issue/{stored.Id}?fields={Uri.EscapeDataString(fields)}", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Jira issue {stored.Id} no longer exists.", ex);
        }

        // Moved out between the listing and this read: not this source's to index any more.
        string? projectId = issue.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object
            && f.TryGetProperty("project", out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.String
                ? pid.GetString()
                : null;
        if (projectId is null)
            throw new IOException($"Jira issue {stored.Id} came back without its project; it will be retried.");
        if (projectId != config.ProjectId)
            throw new FileNotFoundException($"Jira issue {stored.Id} is no longer in this project.");

        var comments = new List<JiraComment>();
        await foreach (var comment in CommentsAsync(stored.Id, ct))
        {
            // Routed from what Jira says now, not the stored record: a comment restricted since the
            // listing must not reach the issue document.
            if (!Route(ToStored(comment), config.ProjectId).Separate)
                comments.Add(ToRendered(comment));
        }

        string projectName = string.IsNullOrWhiteSpace(state.ProjectName) ? config.ProjectKey : state.ProjectName;
        string markdown = JiraRecordRenderer.RenderIssue(projectName, issue, comments, state.RichTextFieldIds, state.FieldNames);
        return new MemoryStream(Encoding.UTF8.GetBytes(markdown));
    }

    /// <summary>
    /// A restricted comment or internal note. Its document is queued under the URI the listing
    /// gave it, so a comment whose routing has changed since is not read: made public it belongs
    /// inline, and restricted differently it would be indexed under the old restriction. The next
    /// listing carries the new routing.
    /// </summary>
    private async Task<Stream> ReadCommentAsync(JiraStoredIssue issue, JiraStoredComment stored, CancellationToken ct)
    {
        CommentResponse comment;
        try
        {
            comment = await api.GetJsonAsync<CommentResponse>($"rest/api/3/issue/{issue.Id}/comment/{stored.Id}", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Jira comment {stored.Id} on issue {issue.Id} no longer exists.", ex);
        }

        var live = Route(ToStored(comment), config.ProjectId);
        if (!live.Separate || live != Route(stored, config.ProjectId))
            throw new FileNotFoundException($"Jira comment {stored.Id} on issue {issue.Id} has changed visibility since it was listed.");

        var state = _store.LoadState();
        string projectName = string.IsNullOrWhiteSpace(state.ProjectName) ? config.ProjectKey : state.ProjectName;
        string markdown = JiraRecordRenderer.RenderComment(projectName, issue.Key, issue.Summary, ToRendered(comment));
        return new MemoryStream(Encoding.UTF8.GetBytes(markdown));
    }

    /// <summary>
    /// Streams an attachment, never more than the size cap: the listed size is Jira's word, and the
    /// pipeline copies a download into memory whole. Failures that a retry cannot fix are
    /// permanent: a redirect off Atlassian's hosts, a missing attachment scope, or more bytes than
    /// the cap allows. A 404 is the attachment gone.
    /// </summary>
    private async Task<Stream> DownloadAttachmentAsync(JiraStoredAttachment attachment, CancellationToken ct)
    {
        Stream download;
        try
        {
            // Jira answers with a redirect to Atlassian's media service, which the client follows
            // only onto Atlassian's own hosts.
            download = await api.DownloadAsync($"rest/api/3/attachment/content/{attachment.Id}", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Jira attachment {attachment.Id} no longer exists.", ex);
        }
        catch (AtlassianRedirectRefusedException ex)
        {
            throw new PermanentIngestionException(
                $"Jira attachment {attachment.Id} could not be downloaded: {ex.Message}", ex);
        }
        catch (Exception ex) when (IsAttachmentScopeRefusal(ex))
        {
            // A retry is refused the same way until the credential gains the scope.
            throw new PermanentIngestionException($"Jira attachment {attachment.Id} was not downloaded. {AttachmentScopeNotice}", ex);
        }

        return new SizeCappedStream(
            download,
            config.MaxAttachmentMb * 1024L * 1024L,
            $"Jira attachment {attachment.Id} is larger than the {config.MaxAttachmentMb} MB cap.");
    }

    /// <summary>
    /// A refusal of an attachment read that is not the credential itself being refused: a 401 with
    /// a freshly issued token or a 403. A token endpoint refusal is the site's problem and propagates.
    /// </summary>
    private static bool IsAttachmentScopeRefusal(Exception ex) =>
        ex is AtlassianAuthException { TokenRefused: false }
        || ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized };

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(Find(path) is not null || FindComment(path) is not null || FindAttachment(path) is not null);

    /// <summary>Returned unchanged: a document's path is already the id-based form this connector reads.</summary>
    public string ResolveJobPath(string relativePath) => relativePath;

    public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Jira sources are polled; they do not support live watch.");

    // ── Sync ───────────────────────────────────────────────────────────────

    private async Task ReadProjectAsync(JiraSyncState state, CancellationToken ct)
    {
        ProjectResponse project;
        try
        {
            project = await api.GetJsonAsync<ProjectResponse>($"rest/api/3/project/{config.ProjectId}", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            // Deleted, or the service account lost Browse projects. Hidden from search, not
            // deleted: a permission slip should not wipe the index.
            int status = (int)ex.StatusCode!;
            throw new JiraProjectUnreadableException(
                AtlassianSyncErrors.JiraProjectUnreadable(config.ProjectKey, status), $"HTTP {status}", ex);
        }

        state.ProjectName = string.IsNullOrWhiteSpace(project.Name) ? config.ProjectKey : project.Name;
        state.ProjectKey = string.IsNullOrWhiteSpace(project.Key) ? config.ProjectKey : project.Key;
    }

    /// <summary>The site's rich-text custom fields: those whose schema is the textarea custom type.</summary>
    private async Task ReadFieldsAsync(JiraSyncState state, CancellationToken ct)
    {
        var fields = await api.GetJsonAsync<List<FieldResponse>>("rest/api/3/field", ct);
        var rich = fields
            .Where(field => IsFieldId(field.Id)
                && field.Schema?.Custom?.EndsWith(":textarea", StringComparison.Ordinal) == true)
            .OrderBy(field => field.Id, StringComparer.Ordinal)
            .ToList();

        state.RichTextFieldIds = [.. rich.Select(field => field.Id!)];
        state.FieldNames = rich.ToDictionary(field => field.Id!, field => field.Name ?? field.Id!, StringComparer.Ordinal);
    }

    private async Task<HashSet<string>> ListIdsAsync(CancellationToken ct)
    {
        string url = $"rest/api/3/search/jql?jql={Uri.EscapeDataString($"project = {config.ProjectId}")}&fields=id&maxResults=5000";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var hit in api.PageByTokenAsync(url, ReadHits, ct))
            ids.Add(hit.Id);
        return ids;
    }

    /// <summary>
    /// Issues updated since five minutes before <paramref name="watermarkMs"/>, with their updated
    /// time (null when Jira gave none, which counts as changed). The bound is unquoted epoch
    /// milliseconds: a quoted date is read in the service account's timezone (JRACLOUD-74279).
    /// </summary>
    private async Task<List<(string Id, DateTimeOffset? Updated)>> ChangedSinceAsync(long watermarkMs, CancellationToken ct)
    {
        string jql = $"project = {config.ProjectId} AND updated >= {watermarkMs - ChangeOverlapMs} ORDER BY updated ASC";
        string url = $"rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}&fields=id,updated&maxResults=5000";
        var changed = new List<(string, DateTimeOffset?)>();
        await foreach (var hit in api.PageByTokenAsync(url, ReadHits, ct))
            changed.Add((hit.Id, hit.Updated));
        return changed;
    }

    /// <summary>
    /// One search page's issues. Anything malformed throws rather than being skipped: the listing
    /// is applied as the whole project, so a dropped entry would read as a deleted issue.
    /// </summary>
    private static List<SearchHit> ReadHits(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("issues", out var issues)
            || issues.ValueKind != JsonValueKind.Array)
            throw new AtlassianMalformedResponseException("Jira returned a search page with no issues array.");

        var hits = new List<SearchHit>();
        foreach (var entry in issues.EnumerateArray())
        {
            string? id = entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            if (!JiraIssueStateStore.IsId(id))
                throw new AtlassianMalformedResponseException("Jira returned a search entry without a valid issue id.");

            string? updated = entry.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object
                && fields.TryGetProperty("updated", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString()
                    : null;
            hits.Add(new SearchHit(id!, JiraRecordRenderer.ParseTime(updated)));
        }

        return hits;
    }

    /// <summary>
    /// Fetches the changed issues a hundred at a time, and each one's comments in full. An issue
    /// bulkfetch does not return keeps its previous record (or, new, waits for the next cycle); one
    /// whose comments answer 404 was deleted since the listing and is taken out of it.
    /// </summary>
    private async Task<Dictionary<string, JiraStoredIssue>> FetchAsync(
        HashSet<string> changed, HashSet<string> listed, long startedMs, CancellationToken ct)
    {
        var fetched = new Dictionary<string, JiraStoredIssue>(StringComparer.Ordinal);
        DateTimeOffset readAt = DateTimeOffset.FromUnixTimeMilliseconds(startedMs);

        foreach (string[] batch in changed.Order(StringComparer.Ordinal).Chunk(BulkFetchSize))
        {
            BulkResponse bulk;
            using (var response = await api.PostAsync(
                "rest/api/3/issue/bulkfetch", new { issueIdsOrKeys = batch, fields = BulkFieldNames }, ct))
            {
                response.EnsureSuccessStatusCode();
                bulk = await response.Content.ReadFromJsonAsync<BulkResponse>(Json, ct)
                    ?? throw new AtlassianMalformedResponseException("Jira returned an empty bulkfetch answer.");
            }

            foreach (var item in bulk.Issues ?? [])
            {
                if (!JiraIssueStateStore.IsId(item.Id) || !batch.Contains(item.Id) || item.Fields?.Updated is not { } updated)
                    continue;

                var issue = new JiraStoredIssue
                {
                    Id = item.Id!,
                    Key = item.Key ?? "",
                    Summary = item.Fields.Summary ?? "",
                    Updated = updated,
                    Attachments = [.. (item.Fields.Attachment ?? [])
                        .Where(a => JiraIssueStateStore.IsId(a.Id) && !string.IsNullOrWhiteSpace(a.Filename))
                        .Select(a => new JiraStoredAttachment
                        {
                            Id = a.Id!,
                            FileName = a.Filename!,
                            MediaType = a.MimeType,
                            Size = a.Size ?? 0,
                            Created = a.Created ?? DateTimeOffset.MinValue,
                        })],
                    CommentsReadAt = readAt,
                };

                // The inline comment field is capped at 20 and serves only as a count.
                if (item.Fields.Comment?.Total is not 0)
                {
                    try
                    {
                        await foreach (var comment in CommentsAsync(issue.Id, ct))
                        {
                            if (!JiraIssueStateStore.IsId(comment.Id))
                                continue;
                            issue.Comments.Add(ToStored(comment));
                        }
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
                    {
                        listed.Remove(issue.Id);
                        continue;
                    }
                }

                if (config.IncludeAttachments && issue.Attachments.Count > 0)
                    await TieAttachmentsAsync(issue, ct);

                fetched[issue.Id] = issue;
            }
        }

        return fetched;
    }

    /// <summary>
    /// Ties each attachment linked from a restricted comment to that comment, so its document takes
    /// the comment's restriction: a file added in a restricted comment is hidden by Jira from
    /// those the comment is hidden from. Each restricted comment is read again with its rendered
    /// HTML, where an attachment appears as a link carrying its id. The first comment to link an
    /// attachment ties it; a later one with another restriction, a comment Connapse cannot
    /// interpret, or one whose restriction changed since the page read makes it deny-all.
    /// A comment that shows a file Connapse cannot name by id (a media-service reference, or an
    /// attachment address of an unknown form) may show any of the issue's attachments, so every
    /// attachment not tied to a comment with that same restriction becomes deny-all.
    /// </summary>
    private async Task TieAttachmentsAsync(JiraStoredIssue issue, CancellationToken ct)
    {
        var attachments = issue.Attachments.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var routes = new Dictionary<string, (bool Separate, bool Internal, JiraRestriction? Restriction, bool DenyAll)>(StringComparer.Ordinal);
        string? untieableCommentId = null;

        foreach (var comment in issue.Comments)
        {
            var route = Route(comment, config.ProjectId);
            if (!route.Separate)
                continue;
            routes[comment.Id] = route;

            CommentResponse live;
            try
            {
                live = await api.GetJsonAsync<CommentResponse>(
                    $"rest/api/3/issue/{issue.Id}/comment/{comment.Id}?expand=renderedBody", ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
            {
                // Deleted since the page read: it no longer hides anything.
                continue;
            }

            // Without the rendered body there is no telling what the comment links.
            string html = live.RenderedBody
                ?? throw new AtlassianMalformedResponseException($"Jira returned comment {comment.Id} without its rendered body.");
            bool changed = Route(ToStored(live), config.ProjectId) != route;

            foreach (string id in LinkedAttachmentIds(html))
            {
                if (!attachments.TryGetValue(id, out var attachment))
                    continue;

                if (attachment.RestrictingCommentId is null)
                    attachment.RestrictingCommentId = comment.Id;
                else if (routes[attachment.RestrictingCommentId] != route)
                    attachment.DenyAll = true;

                if (changed || route.DenyAll)
                    attachment.DenyAll = true;
            }

            if (untieableCommentId is null && HasUntieableReference(html))
                untieableCommentId = comment.Id;
        }

        if (untieableCommentId is null)
            return;

        var untieable = routes[untieableCommentId];
        foreach (var attachment in issue.Attachments)
        {
            if (attachment.RestrictingCommentId is null)
            {
                // Tied to that comment so an internal note's attachments keep the internal mark.
                attachment.RestrictingCommentId = untieableCommentId;
                attachment.DenyAll = true;
            }
            else if (routes[attachment.RestrictingCommentId] != untieable)
            {
                attachment.DenyAll = true;
            }
        }
    }

    /// <summary>The attachment ids a comment's rendered HTML links to, by any of Jira's attachment addresses.</summary>
    internal static IEnumerable<string> LinkedAttachmentIds(string html) =>
        ApiLinkPattern().Matches(html)
            .Concat(SecureLinkPattern().Matches(html))
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Whether the HTML shows a file Connapse cannot name by attachment id: any <c>data-media-*</c>
    /// attribute, or a <c>src</c> or <c>href</c> that points at a media service, a blob, or an
    /// attachment or thumbnail address none of the id patterns reads.
    /// </summary>
    internal static bool HasUntieableReference(string html)
    {
        if (MediaAttributePattern().IsMatch(html))
            return true;

        foreach (Match link in LinkAttributePattern().Matches(html))
        {
            string url = link.Groups["url"].Value;
            if (ApiLinkPattern().IsMatch(url) || SecureLinkPattern().IsMatch(url))
                continue;
            if (UntieableUrlPattern().IsMatch(url))
                return true;
        }

        return false;
    }

    /// <summary>
    /// What is kept of a comment. A group is identified by its id only, never its mutable name; a
    /// role by its name. A visibility without a type is kept as one no route recognises.
    /// </summary>
    private static JiraStoredComment ToStored(CommentResponse comment) => new()
    {
        Id = comment.Id ?? "",
        Updated = comment.Updated ?? comment.Created ?? DateTimeOffset.MinValue,
        VisibilityType = comment.Visibility is null ? null : comment.Visibility.Type ?? "unknown",
        VisibilityIdentifier = comment.Visibility?.Type == "group"
            ? comment.Visibility.Identifier
            : comment.Visibility?.Value ?? comment.Visibility?.Identifier,
        Internal = comment.JsdPublic == false,
    };

    private static JiraComment ToRendered(CommentResponse comment) =>
        new(comment.Author?.DisplayName ?? "Unknown user", comment.Created ?? DateTimeOffset.MinValue, comment.Body);

    private IAsyncEnumerable<CommentResponse> CommentsAsync(string issueId, CancellationToken ct) =>
        api.PageByOffsetAsync(
            $"rest/api/3/issue/{issueId}/comment?orderBy=created", "comments",
            e => e.ValueKind == JsonValueKind.Object ? e.Deserialize<CommentResponse>(Json) : null, 100, ct);

    // ── Documents ──────────────────────────────────────────────────────────

    /// <summary>
    /// Every stored issue, its restricted comments, and with attachments on, every attachment within
    /// the size cap that has a parser. <paramref name="skipped"/> counts the attachments left out.
    /// </summary>
    private IReadOnlyList<ConnectorFile> Listing(string? prefix, out int skipped)
    {
        string? normalized = string.IsNullOrEmpty(prefix) ? null : "/" + prefix.Trim('/') + "/";
        var state = _store.LoadState();
        var files = new List<ConnectorFile>();
        skipped = 0;

        foreach (var issue in _store.Ids().Select(_store.Load).OfType<JiraStoredIssue>())
        {
            files.Add(ToConnectorFile(issue, state));
            files.AddRange(issue.Comments
                .Where(c => JiraIssueStateStore.IsId(c.Id) && Route(c, config.ProjectId).Separate)
                .Select(c => ToCommentFile(issue, c, state)));

            if (!config.IncludeAttachments)
                continue;

            foreach (var attachment in issue.Attachments.Where(a => JiraIssueStateStore.IsId(a.Id)))
            {
                // An empty file has nothing to index, and is neither too large nor unreadable.
                if (IsIndexable(attachment))
                    files.Add(ToAttachmentFile(issue, attachment, state));
                else if (attachment.Size > 0)
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

    /// <summary>
    /// Within the cap and parseable. A size Jira did not report (or reported as zero or less) is
    /// not taken as small: the attachment is skipped.
    /// </summary>
    private bool IsIndexable(JiraStoredAttachment attachment) =>
        attachment.Size > 0
        && attachment.Size <= config.MaxAttachmentMb * 1024L * 1024L
        && fileTypes is not null
        && fileTypes.IsSupported(AtlassianFileNames.SafeFileName(attachment.FileName));

    /// <summary>
    /// An attachment as a document: its real size and creation time are the signature (a Jira
    /// attachment is never edited, only replaced). Its URI is the issue's, or, when the attachment
    /// was linked from a restricted comment, carries that comment's restriction.
    /// </summary>
    private ConnectorFile ToAttachmentFile(JiraStoredIssue issue, JiraStoredAttachment attachment, JiraSyncState state)
    {
        string projectName = string.IsNullOrWhiteSpace(state.ProjectName) ? config.ProjectKey : state.ProjectName;

        return new ConnectorFile(
            Path: AttachmentPathFor(issue.Id, attachment),
            SizeBytes: attachment.Size,
            LastModified: attachment.Created.UtcDateTime,
            ContentType: attachment.MediaType,
            ResourceUri: AttachmentUri(issue, attachment),
            Metadata: new Dictionary<string, string>
            {
                ["jira:issueKey"] = issue.Key,
                ["jira:breadcrumb"] = JiraRecordRenderer.Breadcrumb(projectName, issue.Key, issue.Summary)
                    + " > " + AtlassianFileNames.SafeFileName(attachment.FileName),
                ["jira:url"] = $"{config.Site.SiteUrl}/browse/{Uri.EscapeDataString(issue.Key)}",
            },
            Strategy: null);
    }

    /// <summary>
    /// The URI of an attachment: the tying comment's internal flag and restriction, deny-all when
    /// the tie says so or the tying comment is no longer a restricted one on record.
    /// </summary>
    private string AttachmentUri(JiraStoredIssue issue, JiraStoredAttachment attachment)
    {
        string cloudId = config.Site.CloudId;
        if (attachment.RestrictingCommentId is not { } commentId)
            return AtlassianUri.ForJiraAttachment(cloudId, issue.Id, attachment.Id, false, null);

        var comment = issue.Comments.FirstOrDefault(c => c.Id == commentId);
        var route = comment is null ? default : Route(comment, config.ProjectId);
        bool denyAll = attachment.DenyAll || !route.Separate || route.DenyAll;
        return AtlassianUri.ForJiraAttachment(
            cloudId, issue.Id, attachment.Id, route.Internal, denyAll ? JiraRestriction.DenyAll : route.Restriction);
    }

    internal static string AttachmentPathFor(string issueId, JiraStoredAttachment attachment) =>
        $"/issues/{issueId}/attachments/{attachment.Id}/{AtlassianFileNames.SafeFileName(attachment.FileName)}";

    private ConnectorFile ToConnectorFile(JiraStoredIssue issue, JiraSyncState state)
    {
        string projectName = string.IsNullOrWhiteSpace(state.ProjectName) ? config.ProjectKey : state.ProjectName;
        string projectKey = string.IsNullOrWhiteSpace(state.ProjectKey) ? config.ProjectKey : state.ProjectKey;

        return new ConnectorFile(
            Path: PathFor(issue.Id),
            // Not a size: the change signature the sync engine compares, with the time below.
            SizeBytes: Signature(issue, projectName),
            LastModified: issue.Updated.UtcDateTime,
            ContentType: "text/markdown",
            ResourceUri: AtlassianUri.ForJiraIssue(config.Site.CloudId, issue.Id),
            Metadata: new Dictionary<string, string>
            {
                ["jira:projectKey"] = projectKey,
                ["jira:issueKey"] = issue.Key,
                ["jira:breadcrumb"] = JiraRecordRenderer.Breadcrumb(projectName, issue.Key, issue.Summary),
                ["jira:url"] = $"{config.Site.SiteUrl}/browse/{Uri.EscapeDataString(issue.Key)}",
            },
            Strategy: ChunkingStrategy.Record);
    }

    /// <summary>
    /// A restricted comment's or internal note's own document. Its URI carries the restriction;
    /// one Connapse cannot interpret becomes <see cref="JiraRestriction.DenyAll"/>, so nobody sees it.
    /// </summary>
    private ConnectorFile ToCommentFile(JiraStoredIssue issue, JiraStoredComment comment, JiraSyncState state)
    {
        string projectName = string.IsNullOrWhiteSpace(state.ProjectName) ? config.ProjectKey : state.ProjectName;
        var route = Route(comment, config.ProjectId);

        return new ConnectorFile(
            Path: CommentPathFor(issue.Id, comment.Id),
            SizeBytes: comment.Updated.ToUnixTimeMilliseconds(),
            LastModified: comment.Updated.UtcDateTime,
            ContentType: "text/markdown",
            ResourceUri: AtlassianUri.ForJiraComment(
                config.Site.CloudId, issue.Id, comment.Id, route.Internal, route.DenyAll ? JiraRestriction.DenyAll : route.Restriction),
            Metadata: new Dictionary<string, string>
            {
                ["jira:issueKey"] = issue.Key,
                ["jira:breadcrumb"] = JiraRecordRenderer.Breadcrumb(projectName, issue.Key, issue.Summary) + " > Comment",
                ["jira:visibility"] = Describe(route),
                ["jira:url"] = $"{config.Site.SiteUrl}/browse/{Uri.EscapeDataString(issue.Key)}?focusedCommentId={comment.Id}",
            },
            Strategy: null);
    }

    /// <summary>For display only: <c>group</c>, <c>role:{name}</c>, <c>restricted</c> (deny-all), <c>internal</c>, comma-joined.</summary>
    private static string Describe((bool Separate, bool Internal, JiraRestriction? Restriction, bool DenyAll) route)
    {
        var parts = new List<string>();
        if (route.Internal)
            parts.Add("internal");
        if (route.DenyAll)
            parts.Add("restricted");
        else if (route.Restriction is { Type: JiraRestrictionType.Group })
            parts.Add("group");
        else if (route.Restriction is { } role)
            parts.Add($"role:{role.Value}");
        return string.Join(',', parts);
    }

    /// <summary>
    /// The issue's updated time in epoch milliseconds shifted into the high bits, with 21 bits of a
    /// SHA-256 of its key, the project name and its inline comments (ids and updated times) below.
    /// 42 bits of milliseconds last until 2109, so the value stays positive. The hash catches what
    /// changes the document without moving the issue's own updated time: a renamed project, a
    /// re-keyed issue, or a comment edited or restricted.
    /// </summary>
    internal static long Signature(JiraStoredIssue issue, string projectName)
    {
        var parts = new List<string> { issue.Key, projectName };
        foreach (var comment in issue.Comments.Where(c => c.IsPublic).OrderBy(c => c.Id, StringComparer.Ordinal))
            parts.Add($"{comment.Id}@{comment.Updated.ToUnixTimeMilliseconds()}");

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
        long ms = Math.Clamp(issue.Updated.ToUnixTimeMilliseconds(), 0, (1L << 42) - 1);
        return (ms << 21) | (BinaryPrimitives.ReadUInt32BigEndian(hash) & 0x1F_FFFF);
    }

    /// <summary>
    /// Where a comment goes: inline in the issue document only when it has no visibility and is not
    /// an internal note. A visibility whose target Connapse cannot read (no identifier, a group
    /// that is not a group id, a type it does not know) is a separate document nobody can see.
    /// </summary>
    internal static (bool Separate, bool Internal, JiraRestriction? Restriction, bool DenyAll) Route(
        JiraStoredComment c, string projectId)
    {
        bool isInternal = c.Internal;
        if (c.VisibilityType is null)
            return (isInternal, isInternal, null, false);
        if (string.IsNullOrWhiteSpace(c.VisibilityIdentifier))
            return (true, isInternal, null, true);
        return c.VisibilityType switch
        {
            "group" when Guid.TryParse(c.VisibilityIdentifier, out _) =>
                (true, isInternal, new JiraRestriction(JiraRestrictionType.Group, c.VisibilityIdentifier.ToLowerInvariant()), false),
            "role" => (true, isInternal, new JiraRestriction(JiraRestrictionType.Role, c.VisibilityIdentifier, projectId), false),
            _ => (true, isInternal, null, true),
        };
    }

    internal static string PathFor(string issueId) => $"/issues/{issueId}.md";

    internal static string CommentPathFor(string issueId, string commentId) => $"/issues/{issueId}/comments/{commentId}.md";

    [GeneratedRegex(@"^/issues/(?<id>[0-9]{1,19})\.md\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^/issues/(?<id>[0-9]{1,19})/comments/(?<comment>[0-9]{1,19})\.md\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommentPathPattern();

    [GeneratedRegex(@"^/issues/(?<id>[0-9]{1,19})/attachments/(?<att>[0-9]{1,19})/(?<name>[^/]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentPathPattern();

    // ASCII digits only: \d would also match other scripts' digits, which are not ids.
    [GeneratedRegex(@"/rest/api/(?:2|3|latest)/attachment/(?:content|thumbnail)/([0-9]{1,19})(?![0-9])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ApiLinkPattern();

    [GeneratedRegex(@"/secure/(?:attachment|thumbnail)/([0-9]{1,19})/", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SecureLinkPattern();

    [GeneratedRegex(@"\sdata-media-[a-z0-9-]*\s*=", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MediaAttributePattern();

    [GeneratedRegex(@"\s(?:src|href)\s*=\s*(?:""(?<url>[^""]*)""|'(?<url>[^']*)'|(?<url>[^\s>]+))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LinkAttributePattern();

    // A media service host or path, a blob, or any attachment or thumbnail address.
    [GeneratedRegex(@"media\.atlassian\.com|/media/|^\s*blob:|attachment|thumbnail", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UntieableUrlPattern();

    [GeneratedRegex(@"^customfield_[0-9]{1,19}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FieldIdPattern();

    /// <summary>Field ids go into a request's query, so only Jira's own custom-field shape is accepted.</summary>
    private static bool IsFieldId(string? id) => id is not null && FieldIdPattern().IsMatch(id);

    /// <summary>The stored record a path names, or null when it is not one this source holds.</summary>
    private JiraStoredIssue? Find(string path)
    {
        var match = PathPattern().Match(path ?? "");
        return match.Success ? _store.Load(match.Groups["id"].Value) : null;
    }

    /// <summary>
    /// The stored attachment an attachment path names, or null unless it is one the listing would
    /// emit now: same file name, attachments on, within the cap, and parseable.
    /// </summary>
    private JiraStoredAttachment? FindAttachment(string path)
    {
        var match = AttachmentPathPattern().Match(path ?? "");
        if (!match.Success || !config.IncludeAttachments || _store.Load(match.Groups["id"].Value) is not { } issue)
            return null;

        string id = match.Groups["att"].Value;
        var attachment = issue.Attachments.FirstOrDefault(a => a.Id == id);
        return attachment is not null && AttachmentPathFor(issue.Id, attachment) == path && IsIndexable(attachment)
            ? attachment
            : null;
    }

    /// <summary>The stored issue and comment a comment path names, when that comment is a separate document.</summary>
    private (JiraStoredIssue Issue, JiraStoredComment Comment)? FindComment(string path)
    {
        var match = CommentPathPattern().Match(path ?? "");
        if (!match.Success || _store.Load(match.Groups["id"].Value) is not { } issue)
            return null;

        string commentId = match.Groups["comment"].Value;
        var comment = issue.Comments.FirstOrDefault(c => c.Id == commentId);
        return comment is not null && Route(comment, config.ProjectId).Separate ? (issue, comment) : null;
    }

    // ── Wire shapes ────────────────────────────────────────────────────────

    private sealed record ProjectResponse(string? Id, string? Key, string? Name);

    private sealed record FieldSchema(string? Type, string? Custom);

    private sealed record FieldResponse(string? Id, string? Name, FieldSchema? Schema);

    private sealed record SearchHit(string Id, DateTimeOffset? Updated);

    private sealed record CommentCount(int? Total);

    private sealed record AttachmentResponse(
        string? Id, string? Filename, string? MimeType, long? Size,
        [property: JsonConverter(typeof(JiraTimeConverter))] DateTimeOffset? Created);

    private sealed record BulkFields(
        string? Summary,
        [property: JsonConverter(typeof(JiraTimeConverter))] DateTimeOffset? Updated,
        List<AttachmentResponse>? Attachment,
        CommentCount? Comment);

    private sealed record BulkIssue(string? Id, string? Key, BulkFields? Fields);

    private sealed record BulkResponse(List<BulkIssue>? Issues);

    private sealed record CommentAuthor(string? AccountId, string? DisplayName);

    private sealed record CommentVisibility(string? Type, string? Value, string? Identifier);

    private sealed record CommentResponse(
        string? Id,
        CommentAuthor? Author,
        JsonElement? Body,
        [property: JsonConverter(typeof(JiraTimeConverter))] DateTimeOffset? Created,
        [property: JsonConverter(typeof(JiraTimeConverter))] DateTimeOffset? Updated,
        CommentVisibility? Visibility,
        bool? JsdPublic,
        string? RenderedBody = null);

    /// <summary>Jira's timestamps, whose offset has no colon; a value that is not one reads as null.</summary>
    private sealed class JiraTimeConverter : JsonConverter<DateTimeOffset?>
    {
        public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String ? JiraRecordRenderer.ParseTime(reader.GetString()) : SkipNull(ref reader);

        private static DateTimeOffset? SkipNull(ref Utf8JsonReader reader)
        {
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
        {
            if (value is { } at)
                writer.WriteStringValue(at);
            else
                writer.WriteNullValue();
        }
    }
}

/// <summary>The cursor: when the last complete cycle started, and when comments were last swept.</summary>
internal sealed record JiraCursor(long? WatermarkMs, DateTimeOffset? LastCommentSweepAt)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static JiraCursor? TryParse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<JiraCursor>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
