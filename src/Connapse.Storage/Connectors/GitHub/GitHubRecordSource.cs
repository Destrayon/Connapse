using System.Globalization;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.Connectors.GitHub;

/// <summary>
/// The issues source's cursor: how far each sweep has read, by <c>updated_at</c>, and the
/// sequence number of the last emission (see <see cref="GitHubRecordState.EmittedSeq"/>).
/// </summary>
internal sealed record GitHubRecordCursor(
    DateTimeOffset? Issues,
    DateTimeOffset? Comments,
    DateTimeOffset? ReviewComments,
    bool CommentsBehind,
    long Seq)
{
    public string Serialize() => JsonSerializer.Serialize(this, GitHubApiClient.Json);

    public static GitHubRecordCursor? TryParse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<GitHubRecordCursor>(text, GitHubApiClient.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The issues-and-pull-requests kind of a GitHub source: sweeps the repository's issues and
/// comments into a local record store, and hands the sync engine the records that changed.
/// <para>
/// Every read is a repository-wide list sorted by <c>updated_at</c> and filtered by <c>since</c>,
/// so a cycle costs pages rather than a request per record, which decides whether a large
/// repository fits in an installation's hourly request budget. The one
/// per-record call is a sub-issue listing, made only for issues that have sub-issues.
/// </para>
/// </summary>
internal sealed class GitHubRecordSource(
    GitHubConnectorConfig config, HttpClient http, ILogger logger, TimeProvider? clock = null, GitHubAuth? auth = null)
{
    /// <summary>
    /// How often every issue is listed to find the ones deleted or transferred away, which a
    /// <c>since</c> sweep cannot see. A relist costs a page per hundred records.
    /// </summary>
    internal static readonly TimeSpan RelistInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// How often comments are swept on a repository where no issue moved. Editing or hiding a
    /// comment does not move its issue, so without this such a change would wait for an unrelated
    /// one. Two requests an hour.
    /// </summary>
    internal static readonly TimeSpan CommentSweepInterval = TimeSpan.FromHours(1);

    private const string PerPage = "per_page=100";

    private readonly GitHubApiClient _api = new(http, config.ApiBaseUrl, auth);
    private readonly GitHubRecordStore _store = new(config.MirrorPath);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private string Repo => $"repos/{Uri.EscapeDataString(config.Owner)}/{Uri.EscapeDataString(config.Repo)}";

    public async Task<SyncDelta> GetChangesAsync(string? cursorText, CancellationToken ct)
    {
        GitHubRecordCursor cursor;
        if (cursorText is null)
        {
            // A null cursor is a fresh start: the engine has either never synced this source or
            // was told to resync. Records from before are not trusted to still be current.
            _store.Reset();
            cursor = new GitHubRecordCursor(null, null, null, CommentsBehind: false, Seq: 0);
        }
        else if (GitHubRecordCursor.TryParse(cursorText) is { } parsed && _store.Exists)
        {
            cursor = parsed;
        }
        else
        {
            // The cursor describes records this host no longer has (a lost volume), or is not
            // one of ours. Only a full sweep can re-establish where things stand.
            return new SyncDelta([], [], NextCursor: null, RequiresFullResync: true);
        }

        var state = _store.LoadState();
        Acknowledge(state, cursor.Seq);

        var marks = new Marks(cursor);
        var cache = new RecordCache(_store);
        bool complete = false;

        // With credentials every check below is a conditional request: an unchanged answer is a
        // 304, which GitHub does not count, so an idle repository costs nothing to poll.
        bool probing = auth is not null;

        // Outside the try: a check that cannot finish (the budget is spent) must fail the cycle,
        // not pass as partial progress — a successful cycle clears a hidden source's revoked mark.
        string? repositoryTag = state.RepositoryETag;
        if (probing && config.Verified)
            repositoryTag = await GitHubRepositoryGuard.VerifyAsync(_api, config, state.RepositoryETag, ct);

        try
        {

            // A probe sees only the newest item: an edit in the same second as it, sorting behind
            // it, leaves the probe unchanged. So once an hour every sweep runs whatever they say.
            bool unprobedDue = probing
                && (state.LastUnprobedSweepAt is not { } lastUnprobed || _clock.GetUtcNow() - lastUnprobed >= CommentSweepInterval);

            // Probed only once a sweep has a mark: the first sweep lists everything regardless.
            var issues = probing && cursor.Issues is not null
                ? await ProbeAsync($"{Repo}/issues?state=all&sort=updated&direction=desc&per_page=1", state.IssuesETag, ct)
                : (Changed: true, ETag: (string?)null);
            if (unprobedDue)
                issues.Changed = true;

            if (issues.Changed)
                await SweepIssuesAsync(cursor.Issues, marks, cache, state, ct);

            (bool Changed, string? ETag) comments = (true, null), reviewComments = (true, null);
            if (probing && config.IncludeComments && cursor.Comments is not null && cursor.ReviewComments is not null)
            {
                comments = await ProbeAsync($"{Repo}/issues/comments?sort=updated&direction=desc&per_page=1", state.CommentsETag, ct);
                reviewComments = await ProbeAsync($"{Repo}/pulls/comments?sort=updated&direction=desc&per_page=1", state.ReviewCommentsETag, ct);
                if (unprobedDue)
                    comments.Changed = reviewComments.Changed = true;
            }

            // Comments are swept when an issue moved (a new comment moves its issue) — which the
            // issues sweep records in the marks, so the obligation survives a budget that runs out
            // before the comments are reached — or when an earlier cycle did not finish them. With
            // credentials the comment probes catch an edit that moves nothing; without them, an
            // hourly sweep does.
            bool sweepComments = config.IncludeComments
                && (marks.CommentsBehind
                    || cursor.Comments is null || cursor.ReviewComments is null
                    || (probing
                        ? comments.Changed || reviewComments.Changed
                        : state.LastCommentSweepAt is not { } last || _clock.GetUtcNow() - last >= CommentSweepInterval));

            if (sweepComments)
            {
                marks.CommentsBehind = true;
                await SweepCommentsAsync(review: false, cursor.Comments, marks, cache, state, ct);
                await SweepCommentsAsync(review: true, cursor.ReviewComments, marks, cache, state, ct);
                await RefetchShrunkenCommentsAsync(state, ct);
                marks.CommentsBehind = false;
                state.LastCommentSweepAt = _clock.GetUtcNow();
            }

            // Only now: every sweep a probe gated has finished, so its ETag may stand for it.
            state.RepositoryETag = repositoryTag;
            if (issues.ETag is not null) state.IssuesETag = issues.ETag;
            if (sweepComments || !comments.Changed) state.CommentsETag = comments.ETag ?? state.CommentsETag;
            if (sweepComments || !reviewComments.Changed) state.ReviewCommentsETag = reviewComments.ETag ?? state.ReviewCommentsETag;
            if (unprobedDue) state.LastUnprobedSweepAt = _clock.GetUtcNow();

            complete = true;
        }
        catch (GitHubRateLimitedException ex)
        {
            logger.LogWarning(
                "GitHub {Owner}/{Repo}: API budget spent until {ResetAt}; keeping progress and resuming next cycle",
                LogSanitizer.Sanitize(config.Owner), LogSanitizer.Sanitize(config.Repo), ex.ResetAt);
        }
        catch (GitHubNotFoundException ex)
        {
            throw GitHubRepositoryGuard.Unavailable(config, ex);
        }
        finally
        {
            _store.SaveState(state);
        }

        // Nothing is emitted from a cycle that stopped partway: a record's comments may still be
        // arriving, and emitting it now would embed it twice. The marks still advance, so the next
        // cycle carries on from here rather than starting over.
        if (!complete)
            return new SyncDelta([], [], marks.ToCursor(cursor.Seq).Serialize(), RequiresFullResync: false);

        await RelistIfDueAsync(firstSync: cursorText is null, state, ct);

        // Asked again before anything is emitted: a name reassigned while the sweeps ran would
        // otherwise hand another repository's records to this source's address.
        if (probing && config.Verified)
            await GitHubRepositoryGuard.VerifyAsync(_api, config, repositoryTag, ct);

        return Emit(cursor, marks, state);
    }

    /// <summary>
    /// Asks whether the newest item of a list changed since <paramref name="etag"/>. A 304 is
    /// free; a 200 costs one request and returns the ETag to remember once the sweep it gates is done.
    /// </summary>
    private async Task<(bool Changed, string? ETag)> ProbeAsync(string url, string? etag, CancellationToken ct)
    {
        var (changed, _, newTag) = await _api.GetIfChangedAsync<System.Text.Json.JsonElement>(url, etag, ct);
        return (changed, newTag);
    }

    // ── Sweeps ─────────────────────────────────────────────────────────────

    /// <returns>True when at least one issue changed; the boundary item re-read by an inclusive <c>since</c> does not count.</returns>
    private async Task SweepIssuesAsync(
        DateTimeOffset? since, Marks marks, RecordCache cache, GitHubRecordState state, CancellationToken ct)
    {
        string url = $"{Repo}/issues?state=all&sort=updated&direction=asc&{PerPage}{Since(since)}";

        await foreach (var page in _api.PagesAsync<GitHubIssue>(url, ct))
        {
            foreach (var issue in page.Items)
            {
                var record = cache.Get(issue.Number);
                if (record.Issue is not null && record.Issue.UpdatedAt == issue.UpdatedAt)
                    continue;

                record.Issue = issue;
                cache.Touch(record);
                state.Dirty.Add(issue.Number);

                // Its comments are owed from here on, even if this cycle stops before them.
                if (config.IncludeComments)
                    marks.CommentsBehind = true;

                if (issue.SubIssuesSummary?.Total > 0 || record.Children.Count > 0)
                    await RefreshChildrenAsync(record, issue.SubIssuesSummary?.Total ?? 0, cache, state, ct);

                // Fewer comments than we hold means one was deleted, which no sweep reports.
                if (record.IssueCommentCount > issue.Comments)
                    state.Suspects.Add(issue.Number);
            }

            // Saved before the mark moves, so a mark never claims records that are not on disk.
            cache.Flush();
            marks.Issues = Advance(marks.Issues, page, page.Items.Select(i => i.UpdatedAt));
        }
    }

    private async Task SweepCommentsAsync(
        bool review, DateTimeOffset? since, Marks marks, RecordCache cache, GitHubRecordState state,
        CancellationToken ct)
    {
        string endpoint = review ? "pulls/comments" : "issues/comments";
        string url = $"{Repo}/{endpoint}?sort=updated&direction=asc&{PerPage}{Since(since)}";

        await foreach (var page in _api.PagesAsync<GitHubComment>(url, ct))
        {
            foreach (var comment in page.Items)
            {
                if (ParentNumber(review ? comment.PullRequestUrl : comment.IssueUrl) is not int number)
                    continue;

                var record = cache.Get(number);
                string key = (review ? "r" : "c") + comment.Id.ToString(CultureInfo.InvariantCulture);
                var stored = ToStored(comment, review);

                if (record.Comments.TryGetValue(key, out var existing) && existing == stored)
                    continue;

                record.Comments[key] = stored;
                cache.Touch(record);
                state.Dirty.Add(number);
            }

            cache.Flush();
            var seen = page.Items.Select(c => c.UpdatedAt);
            if (review) marks.ReviewComments = Advance(marks.ReviewComments, page, seen);
            else marks.Comments = Advance(marks.Comments, page, seen);
        }
    }

    /// <summary>
    /// Re-reads the sub-issues of an issue that moved. Children gained point back at it; children
    /// lost stop doing so. One request, and only for issues that have or had sub-issues.
    /// </summary>
    private async Task RefreshChildrenAsync(
        GitHubStoredRecord parent, int total, RecordCache cache, GitHubRecordState state, CancellationToken ct)
    {
        List<int> children;
        if (total == 0)
        {
            children = [];
        }
        else
        {
            try
            {
                var listed = await _api.GetAllAsync<GitHubIssue>(
                    $"{Repo}/issues/{parent.Number}/sub_issues?{PerPage}", ct);
                children = [.. listed.Select(i => i.Number).Distinct().Order()];
            }
            catch (GitHubNotFoundException)
            {
                // The issue vanished between the list and this call; the relist will catch it.
                return;
            }
        }

        DateTimeOffset now = _clock.GetUtcNow();

        foreach (int gone in parent.Children.Except(children))
        {
            var child = cache.Get(gone);
            if (child.Parent != parent.Number) continue;
            child.Parent = null;
            child.EdgesChangedAt = now;
            cache.Touch(child);
            state.Dirty.Add(gone);
        }

        foreach (int number in children)
        {
            var child = cache.Get(number);
            if (child.Parent == parent.Number) continue;
            child.Parent = parent.Number;
            child.EdgesChangedAt = now;
            cache.Touch(child);
            state.Dirty.Add(number);
        }

        if (!parent.Children.SequenceEqual(children))
            parent.EdgesChangedAt = now;

        parent.Children = children;
    }

    /// <summary>
    /// Replaces the issue comments of records that hold more than GitHub now reports. Each is
    /// forgotten only once its refetch succeeds.
    /// </summary>
    private async Task RefetchShrunkenCommentsAsync(GitHubRecordState state, CancellationToken ct)
    {
        foreach (int number in state.Suspects.ToList())
        {
            var record = _store.Load(number);
            if (record?.Issue is null || record.IssueCommentCount <= record.Issue.Comments)
            {
                state.Suspects.Remove(number);
                continue;
            }

            List<GitHubComment> current;
            try
            {
                current = await _api.GetAllAsync<GitHubComment>($"{Repo}/issues/{number}/comments?{PerPage}", ct);
            }
            catch (GitHubNotFoundException)
            {
                state.Suspects.Remove(number);
                continue;
            }

            foreach (string key in record.Comments.Keys.Where(k => k.StartsWith('c')).ToList())
                record.Comments.Remove(key);
            foreach (var comment in current)
                record.Comments["c" + comment.Id.ToString(CultureInfo.InvariantCulture)] = ToStored(comment, review: false);

            _store.Save(record);
            state.Dirty.Add(number);
            state.Suspects.Remove(number);
        }
    }

    /// <summary>
    /// Lists every issue once a day and deletes the stored records GitHub no longer has. Applied
    /// only from a listing that completed: a partial one would read as mass deletion.
    /// </summary>
    private async Task RelistIfDueAsync(bool firstSync, GitHubRecordState state, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();

        // A first sync's issue sweep had no since, so it was already a full listing.
        if (firstSync || state.LastRelistAt is null)
        {
            state.LastRelistAt = now;
            _store.SaveState(state);
            return;
        }

        if (now - state.LastRelistAt < RelistInterval)
            return;

        var listed = new HashSet<int>();
        try
        {
            await foreach (var page in _api.PagesAsync<GitHubIssue>($"{Repo}/issues?state=all&{PerPage}", ct))
                listed.UnionWith(page.Items.Select(i => i.Number));
        }
        catch (GitHubRateLimitedException)
        {
            return;
        }
        catch (GitHubNotFoundException)
        {
            return;
        }

        var stored = _store.Numbers().ToList();

        // An empty listing against a non-empty store is far likelier to be a bad response than
        // a repository whose every issue was deleted at once.
        if (listed.Count == 0 && stored.Count > 0)
            return;

        // Comments deleted upstream are invisible to a since-sweep, and the count check above only
        // catches issue comments whose total fell. Listing every comment id once a day catches the
        // rest: deleted review comments, and a deletion hidden by a new comment in the same window.
        Dictionary<int, HashSet<string>>? commentIds = config.IncludeComments
            ? await ListAllCommentIdsAsync(ct)
            : null;

        foreach (int number in stored.Where(n => !listed.Contains(n)))
        {
            if (_store.Load(number)?.Issue is { } issue)
                state.PendingDeletes.Add(GitHubRecordRenderer.PathFor(issue));

            _store.Delete(number);
            state.Dirty.Remove(number);
        }

        if (commentIds is not null)
        {
            foreach (int number in stored.Where(listed.Contains))
            {
                var record = _store.Load(number);
                if (record is null || record.Comments.Count == 0) continue;

                var keep = commentIds.GetValueOrDefault(number) ?? [];
                var removed = record.Comments.Keys.Where(k => !keep.Contains(k)).ToList();
                if (removed.Count == 0) continue;

                foreach (string key in removed)
                    record.Comments.Remove(key);

                _store.Save(record);
                state.Dirty.Add(number);
            }
        }

        state.LastRelistAt = now;
        _store.SaveState(state);
    }

    /// <summary>
    /// Every issue and review comment id in the repository, by record number. Null when either
    /// listing did not complete, in which case nothing is removed.
    /// </summary>
    private async Task<Dictionary<int, HashSet<string>>?> ListAllCommentIdsAsync(CancellationToken ct)
    {
        var byNumber = new Dictionary<int, HashSet<string>>();

        try
        {
            foreach (bool review in new[] { false, true })
            {
                string endpoint = review ? "pulls/comments" : "issues/comments";
                await foreach (var page in _api.PagesAsync<GitHubComment>($"{Repo}/{endpoint}?{PerPage}", ct))
                {
                    foreach (var comment in page.Items)
                    {
                        if (ParentNumber(review ? comment.PullRequestUrl : comment.IssueUrl) is not int number)
                            continue;

                        if (!byNumber.TryGetValue(number, out var ids))
                            byNumber[number] = ids = [];
                        ids.Add((review ? "r" : "c") + comment.Id.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is GitHubRateLimitedException or GitHubNotFoundException)
        {
            return null;
        }

        return byNumber;
    }

    // ── Emission ───────────────────────────────────────────────────────────

    /// <summary>
    /// Forgets what the previous cycle emitted once the engine proves it stored that cycle, by
    /// returning with the cursor that carried it. Any other cursor means the engine failed after
    /// the emission, and the records stay dirty to be emitted again.
    /// </summary>
    private static void Acknowledge(GitHubRecordState state, long seq)
    {
        if (state.EmittedSeq == 0)
            return;

        if (seq == state.EmittedSeq)
        {
            state.Dirty.ExceptWith(state.EmittedUpserts);
            state.PendingDeletes.ExceptWith(state.EmittedDeletes);
            if (state.EmittedFull)
                state.InitialListingPending = false;
        }

        state.EmittedSeq = 0;
        state.EmittedUpserts = [];
        state.EmittedDeletes = [];
        state.EmittedFull = false;
    }

    private SyncDelta Emit(GitHubRecordCursor cursor, Marks marks, GitHubRecordState state)
    {
        var upserts = new List<ConnectorFile>();
        var emitted = new List<int>();

        // The first complete emission after a fresh start carries every record, flagged as a full
        // listing, so the engine deletes whatever was indexed before and is not in the store now.
        bool full = state.InitialListingPending;
        if (full)
            state.Dirty.UnionWith(_store.Numbers());

        foreach (int number in state.Dirty.ToList())
        {
            var record = _store.Load(number);
            if (record is null)
            {
                state.Dirty.Remove(number);
                continue;
            }

            // A comment or sub-issue link seen before its issue: held until the issue arrives.
            if (record.Issue is null)
                continue;

            upserts.Add(ToConnectorFile(GitHubRecordRenderer.Render(record, config.Owner, config.Repo, config.CommentPolicy)));
            emitted.Add(number);
        }

        var deletes = state.PendingDeletes.ToList();
        long seq = cursor.Seq;

        if (upserts.Count > 0 || deletes.Count > 0 || full)
        {
            seq++;
            state.EmittedSeq = seq;
            state.EmittedUpserts = emitted;
            state.EmittedDeletes = deletes;
            state.EmittedFull = full;
        }

        _store.SaveState(state);
        return new SyncDelta(
            upserts, deletes, marks.ToCursor(seq).Serialize(), RequiresFullResync: false, IsFullListing: full);
    }

    // ── Reads ──────────────────────────────────────────────────────────────

    public IReadOnlyList<ConnectorFile> List(string? prefix)
    {
        string? normalized = string.IsNullOrEmpty(prefix) ? null : "/" + prefix.Trim('/') + "/";

        return
        [
            .. _store.Numbers()
                .Select(_store.Load)
                .Where(r => r?.Issue is not null)
                .Select(r => ToConnectorFile(GitHubRecordRenderer.Render(r!, config.Owner, config.Repo, config.CommentPolicy)))
                .Where(f => normalized is null || f.Path.StartsWith(normalized, StringComparison.Ordinal))
                .OrderBy(f => f.Path, StringComparer.Ordinal),
        ];
    }

    public GitHubRenderedRecord? Find(string path)
    {
        if (!GitHubRecordRenderer.TryParsePath(path, out int number, out bool isPullRequest))
            return null;

        var record = _store.Exists ? _store.Load(number) : null;
        return record?.Issue is { } issue && issue.IsPullRequest == isPullRequest
            ? GitHubRecordRenderer.Render(record, config.Owner, config.Repo, config.CommentPolicy)
            : null;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private ConnectorFile ToConnectorFile(GitHubRenderedRecord rendered) => new(
        Path: rendered.Path,
        SizeBytes: Encoding.UTF8.GetByteCount(rendered.Markdown),
        LastModified: rendered.LastModified,
        ContentType: "text/markdown",
        // An address only for a private repository, for the reason the docs source gives. The
        // github.com link travels in the metadata either way.
        ResourceUri: config.ResourceUriFor(rendered.Path),
        Metadata: rendered.Metadata,
        Strategy: ChunkingStrategy.Record);

    /// <summary>
    /// Everyone who has commented in the synced records, most active first — what an administrator
    /// chooses from when deciding whose comments are indexed. Read from the local store, so it
    /// costs no GitHub requests and is empty until the first sync.
    /// </summary>
    public IReadOnlyList<GitHubCommentAuthor> CommentAuthors() =>
        _store.Numbers()
            .Select(_store.Load)
            .Where(r => r is not null)
            .SelectMany(r => r!.Comments.Values)
            .GroupBy(c => c.Login, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GitHubCommentAuthor(g.First().Login, g.Any(c => c.AuthorIsBot), g.Count()))
            .OrderByDescending(a => a.Comments)
            .ThenBy(a => a.Login, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static GitHubStoredComment ToStored(GitHubComment comment, bool review) => new(
        Login: comment.User?.Login ?? "ghost",
        Body: comment.Body ?? "",
        CreatedAt: comment.CreatedAt,
        UpdatedAt: comment.UpdatedAt,
        ReviewPath: review ? comment.Path : null,
        Minimized: comment.IsMinimized,
        IsBot: comment.User?.Type is { } type ? string.Equals(type, "Bot", StringComparison.OrdinalIgnoreCase) : null);

    /// <summary>The issue or pull-request number at the end of a comment's parent URL.</summary>
    private static int? ParentNumber(string? url)
    {
        if (url is null) return null;
        string last = url[(url.LastIndexOf('/') + 1)..];
        return int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null;
    }

    /// <summary>
    /// Inclusive, to the second: the item at the mark is read again, which is how same-second
    /// ties are never skipped. Re-reading it is harmless because an unchanged item is not marked
    /// dirty.
    /// </summary>
    private static string Since(DateTimeOffset? mark) =>
        mark is { } m
            ? "&since=" + m.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : "";

    /// <summary>
    /// The newest <c>updated_at</c> seen. A sweep that has never seen anything takes the server's
    /// clock instead, so an empty repository does not re-list from the beginning every cycle.
    /// </summary>
    private static DateTimeOffset? Advance<T>(DateTimeOffset? mark, GitHubPage<T> page, IEnumerable<DateTimeOffset> seen)
    {
        DateTimeOffset? newest = seen.Any() ? seen.Max() : null;
        if (newest is { } n) return mark is { } m && m > n ? m : n;
        return mark ?? page.ServerDate;
    }

    private sealed class Marks(GitHubRecordCursor cursor)
    {
        public DateTimeOffset? Issues { get; set; } = cursor.Issues;
        public DateTimeOffset? Comments { get; set; } = cursor.Comments;
        public DateTimeOffset? ReviewComments { get; set; } = cursor.ReviewComments;
        public bool CommentsBehind { get; set; } = cursor.CommentsBehind;

        public GitHubRecordCursor ToCursor(long seq) => new(Issues, Comments, ReviewComments, CommentsBehind, seq);
    }

    /// <summary>Records read during one page, written back together when the page is done.</summary>
    private sealed class RecordCache(GitHubRecordStore store)
    {
        private readonly Dictionary<int, GitHubStoredRecord> _loaded = [];
        private readonly HashSet<int> _touched = [];

        public GitHubStoredRecord Get(int number)
        {
            if (!_loaded.TryGetValue(number, out var record))
            {
                record = store.Load(number) ?? new GitHubStoredRecord { Number = number };
                _loaded[number] = record;
            }

            return record;
        }

        public void Touch(GitHubStoredRecord record) => _touched.Add(record.Number);

        public void Flush()
        {
            foreach (int number in _touched)
                store.Save(_loaded[number]);

            _loaded.Clear();
            _touched.Clear();
        }
    }
}
