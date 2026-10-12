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
    bool IncludeAttachments = true, int MaxAttachmentMb = 25);

/// <summary>
/// Syncs a Jira project's issues as record documents. Every cycle lists the project's issue ids,
/// asks which issues changed since the last cycle, fetches only those, and hands the sync engine
/// a full listing from the issue-state store: the engine re-ingests what changed signature and
/// deletes what is gone, so a deleted issue or one moved to another project drops out the same way.
/// Comments that carry a group or role visibility, and JSM internal notes, are left out of the
/// issue document.
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
        // trusted, so every listed issue is fetched again. The store is replaced only once a
        // listing has completed, as in Confluence, so jobs already queued still find their records.
        bool everything = cursor is null || previous!.WatermarkMs is null;
        long startedMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var state = cursor is null ? new JiraSyncState() : _store.LoadState();
        HashSet<string> listed;
        Dictionary<string, JiraStoredIssue> fetched;

        try
        {
            await ReadProjectAsync(state, ct);
            await ReadFieldsAsync(state, ct);

            listed = await ListIdsAsync(ct);
            var changed = new HashSet<string>(
                everything ? listed : listed.Where(id => _store.Load(id) is null), StringComparer.Ordinal);
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

        foreach (var issue in fetched.Values)
            _store.Save(issue);
        foreach (string id in _store.Ids().Where(id => !listed.Contains(id)).ToList())
            _store.Delete(id);
        _store.SaveState(state);

        // The daily comment sweep arrives with restricted comments; until then the cursor carries it forward.
        return new SyncDelta(
            Listing(null), [], new JiraCursor(startedMs, previous?.LastCommentSweepAt).Serialize(),
            RequiresFullResync: false, IsFullListing: true);
    }

    public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default) =>
        Task.FromResult(Listing(prefix));

    /// <summary>
    /// Fetches the issue with its rich-text fields and every comment, and renders it with only the
    /// comments anyone who can browse the issue can see.
    /// </summary>
    public async Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
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
            if (comment.Visibility is null && comment.JsdPublic != false)
                comments.Add(new JiraComment(comment.Author?.DisplayName ?? "Unknown user", comment.Created ?? DateTimeOffset.MinValue, comment.Body));
        }

        string projectName = string.IsNullOrWhiteSpace(state.ProjectName) ? config.ProjectKey : state.ProjectName;
        string markdown = JiraRecordRenderer.RenderIssue(projectName, issue, comments, state.RichTextFieldIds, state.FieldNames);
        return new MemoryStream(Encoding.UTF8.GetBytes(markdown));
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(Find(path) is not null);

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
                            issue.Comments.Add(new JiraStoredComment
                            {
                                Id = comment.Id!,
                                Updated = comment.Updated ?? comment.Created ?? DateTimeOffset.MinValue,
                                VisibilityType = comment.Visibility is null ? null : comment.Visibility.Type ?? "unknown",
                                VisibilityIdentifier = comment.Visibility?.Type == "group"
                                    ? comment.Visibility.Identifier
                                    : comment.Visibility?.Value ?? comment.Visibility?.Identifier,
                                Internal = comment.JsdPublic == false,
                            });
                        }
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
                    {
                        listed.Remove(issue.Id);
                        continue;
                    }
                }

                fetched[issue.Id] = issue;
            }
        }

        return fetched;
    }

    private IAsyncEnumerable<CommentResponse> CommentsAsync(string issueId, CancellationToken ct) =>
        api.PageByOffsetAsync(
            $"rest/api/3/issue/{issueId}/comment?orderBy=created", "comments",
            e => e.ValueKind == JsonValueKind.Object ? e.Deserialize<CommentResponse>(Json) : null, 100, ct);

    // ── Documents ──────────────────────────────────────────────────────────

    private IReadOnlyList<ConnectorFile> Listing(string? prefix)
    {
        string? normalized = string.IsNullOrEmpty(prefix) ? null : "/" + prefix.Trim('/') + "/";
        var state = _store.LoadState();

        return
        [
            .. _store.Ids()
                .Select(_store.Load)
                .OfType<JiraStoredIssue>()
                .Select(issue => ToConnectorFile(issue, state))
                .Where(f => normalized is null || f.Path.StartsWith(normalized, StringComparison.Ordinal))
                .OrderBy(f => f.Path, StringComparer.Ordinal),
        ];
    }

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

    internal static string PathFor(string issueId) => $"/issues/{issueId}.md";

    [GeneratedRegex(@"^/issues/(?<id>[0-9]{1,19})\.md\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

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
        bool? JsdPublic);

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
