using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Connapse.Core.Tests.Connectors;

/// <summary>A footer or inline comment on a page or blog post.</summary>
public sealed record FakeConfluenceComment(
    string Id, string PageId, string AuthorId, string Body, DateTimeOffset ModifiedAt, bool Inline = false);

/// <summary>An attachment on a page or blog post. <see cref="Id"/> is the digits after "att".</summary>
public sealed record FakeConfluenceAttachment(
    string Id, string PageId, string FileName, byte[] Content, DateTimeOffset ModifiedAt,
    string MediaType = "text/plain", long? DeclaredSize = null, int Version = 1, bool OmitSize = false)
{
    public long Size => DeclaredSize ?? Content.Length;
}

/// <summary>
/// The fake's comments and the v1 CQL search that reports changes to them. The search understands
/// only the clauses the connector sends: <c>space="KEY"</c>, the type filter, and
/// <c>lastmodified &gt;= "yyyy-MM-dd HH:mm"</c>.
/// </summary>
public sealed partial class FakeConfluence
{
    private readonly Dictionary<string, FakeConfluenceComment> _comments = [];
    private int _nextCommentId = 70000;

    /// <summary>Every CQL query received, decoded.</summary>
    public List<string> SearchQueries { get; } = [];

    /// <summary>Answers the next search request 429, once.</summary>
    public bool RateLimitNextSearch { get; set; }

    public string AddComment(string pageId, string authorId, string body, DateTimeOffset at, bool inline = false)
    {
        lock (_gate)
        {
            string id = (++_nextCommentId).ToString(CultureInfo.InvariantCulture);
            _comments[id] = new FakeConfluenceComment(id, pageId, authorId, body, at, inline);
            return id;
        }
    }

    public void EditComment(string id, string body, DateTimeOffset at)
    {
        lock (_gate) _comments[id] = _comments[id] with { Body = body, ModifiedAt = at };
    }

    [GeneratedRegex(@"^api/v2/(?<kind>pages|blogposts)/(?<id>\d+)/(?<where>footer|inline)-comments$")]
    private static partial Regex CommentsRoute();

    [GeneratedRegex("space=\"(?<key>[^\"]+)\"")]
    private static partial Regex CqlSpace();

    [GeneratedRegex("lastmodified >= \"(?<at>[^\"]+)\"")]
    private static partial Regex CqlSince();

    /// <summary>Answers the comment and search routes; null when the path is neither. Caller holds the gate.</summary>
    private HttpResponseMessage? AnswerChanges(string rest, System.Collections.Specialized.NameValueCollection query)
    {
        if (CommentsRoute().Match(rest) is { Success: true } comments)
        {
            string singular = comments.Groups["kind"].Value == "pages" ? "page" : "blogpost";
            string pageId = comments.Groups["id"].Value;
            if (!_pages.TryGetValue(pageId, out var page) || page.Kind != singular)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            bool inline = comments.Groups["where"].Value == "inline";
            var all = _comments.Values.Where(c => c.PageId == pageId && c.Inline == inline)
                .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

            return Paged(rest, query, all, c => new
            {
                id = c.Id,
                status = "current",
                version = new { number = 1, createdAt = c.ModifiedAt, authorId = c.AuthorId },
                body = new { storage = new { value = c.Body, representation = "storage" } },
            });
        }

        if (rest == "rest/api/search")
            return Search(query);

        if (AttachmentsRoute().Match(rest) is { Success: true } listing)
        {
            string singular = listing.Groups["kind"].Value == "pages" ? "page" : "blogpost";
            string pageId = listing.Groups["id"].Value;
            AttachmentListings[pageId] = AttachmentListings.GetValueOrDefault(pageId) + 1;
            if (RateLimitAttachmentListing.Remove(pageId))
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.TryAddWithoutValidation("Retry-After", "30");
                return limited;
            }

            if (!_pages.TryGetValue(pageId, out var page) || page.Kind != singular)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var all = _attachments.Values.Where(a => a.PageId == pageId).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
            return Paged(rest, query, all, a => new
            {
                id = "att" + a.Id,
                title = a.FileName,
                mediaType = a.MediaType,
                fileSize = a.OmitSize ? (long?)null : a.Size,
                pageId = a.PageId,
                version = new { number = a.Version, createdAt = a.ModifiedAt },
            });
        }

        if (DownloadRoute().Match(rest) is { Success: true } download)
        {
            string attId = download.Groups["att"].Value;
            if (!_attachments.TryGetValue(attId, out var attachment) || attachment.PageId != download.Groups["page"].Value)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(DownloadRedirect ?? $"https://{MediaHost}/file/{attId}/binary?token=media-{attId}");
            return redirect;
        }

        return null;
    }

    // ── Attachments ──────────────────────────────────────────────────────

    /// <summary>Where a download redirects to.</summary>
    public const string MediaHost = "api.media.atlassian.com";

    private readonly Dictionary<string, FakeConfluenceAttachment> _attachments = [];

    /// <summary>How many times each page's attachment list was read.</summary>
    public Dictionary<string, int> AttachmentListings { get; } = [];

    /// <summary>Pages whose next attachment listing answers 429, once each.</summary>
    public HashSet<string> RateLimitAttachmentListing { get; } = [];

    /// <summary>Overrides where a download redirects, for the client's host checks.</summary>
    public string? DownloadRedirect { get; set; }

    public void AddAttachment(FakeConfluenceAttachment attachment) { lock (_gate) _attachments[attachment.Id] = attachment; }

    public void RemoveAttachment(string id) { lock (_gate) _attachments.Remove(id); }

    [GeneratedRegex(@"^api/v2/(?<kind>pages|blogposts)/(?<id>\d+)/attachments$")]
    private static partial Regex AttachmentsRoute();

    [GeneratedRegex(@"^rest/api/content/(?<page>\d+)/child/attachment/att(?<att>\d+)/download$")]
    private static partial Regex DownloadRoute();

    [GeneratedRegex(@"^/file/(?<att>\d+)/binary$")]
    private static partial Regex MediaRoute();

    /// <summary>The media host's answer: the attachment's bytes, when the address carries its token.</summary>
    internal HttpResponseMessage AnswerMedia(Uri uri)
    {
        lock (_gate)
        {
            var match = MediaRoute().Match(uri.AbsolutePath);
            string attId = match.Groups["att"].Value;
            if (!match.Success || !_attachments.TryGetValue(attId, out var attachment) || !uri.Query.Contains($"token=media-{attId}"))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(attachment.Content) };
        }
    }

    private HttpResponseMessage Search(System.Collections.Specialized.NameValueCollection query)
    {
        string cql = query["cql"] ?? "";
        SearchQueries.Add(cql);

        if (RateLimitNextSearch)
        {
            RateLimitNextSearch = false;
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.TryAddWithoutValidation("Retry-After", "30");
            return limited;
        }

        string key = CqlSpace().Match(cql).Groups["key"].Value;
        string? spaceId = _spaces.FirstOrDefault(s => s.Value.Key == key).Key;
        DateTimeOffset since = DateTimeOffset.ParseExact(
            CqlSince().Match(cql).Groups["at"].Value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);

        var hits = new List<(string Id, string Type, string Title, string PageId, string PageType, DateTimeOffset At)>();
        if (cql.Contains("comment", StringComparison.Ordinal))
        {
            hits.AddRange(_comments.Values
                .Where(c => _pages.TryGetValue(c.PageId, out var p) && p.SpaceId == spaceId && c.ModifiedAt >= since)
                .Select(c => (c.Id, "comment", "Re: comment", c.PageId, _pages[c.PageId].Kind, c.ModifiedAt)));
        }

        if (cql.Contains("attachment", StringComparison.Ordinal))
        {
            hits.AddRange(_attachments.Values
                .Where(a => _pages.TryGetValue(a.PageId, out var p) && p.SpaceId == spaceId && a.ModifiedAt >= since)
                .Select(a => ("att" + a.Id, "attachment", a.FileName, a.PageId, _pages[a.PageId].Kind, a.ModifiedAt)));
        }

        int limit = int.Parse(query["limit"] ?? "25", CultureInfo.InvariantCulture);
        int start = int.Parse(query["start"] ?? "0", CultureInfo.InvariantCulture);
        var ordered = hits.OrderBy(h => h.At).ThenBy(h => h.Id, StringComparer.Ordinal).ToList();
        var slice = ordered.Skip(start).Take(Math.Min(limit, MaxPageSize)).ToList();
        string? next = start + slice.Count < ordered.Count
            ? $"/rest/api/search?cql={Uri.EscapeDataString(cql)}&limit={limit}&start={start + slice.Count}&expand=content.container"
            : null;

        return FakeAtlassianApi.Json(new
        {
            results = slice.Select(h => new
            {
                content = new
                {
                    id = h.Id,
                    type = h.Type,
                    title = h.Title,
                    container = new { id = h.PageId, type = h.PageType },
                },
                lastModified = h.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            }),
            _links = new { next },
        });
    }

    private HttpResponseMessage Paged<T>(
        string rest, System.Collections.Specialized.NameValueCollection query, List<T> all, Func<T, object> shape)
    {
        int limit = Math.Min(int.Parse(query["limit"] ?? "25", CultureInfo.InvariantCulture), MaxPageSize);
        int start = int.Parse(query["cursor"] ?? "0", CultureInfo.InvariantCulture);
        var slice = all.Skip(start).Take(limit).ToList();
        string? next = start + limit < all.Count
            ? $"/wiki/{rest}?body-format=storage&limit={limit}&cursor={start + limit}"
            : null;

        return FakeAtlassianApi.Json(new { results = slice.Select(shape), _links = new { next } });
    }
}
