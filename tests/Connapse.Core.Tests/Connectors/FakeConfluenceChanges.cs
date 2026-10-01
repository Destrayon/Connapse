using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Connapse.Core.Tests.Connectors;

/// <summary>A footer or inline comment on a page or blog post.</summary>
public sealed record FakeConfluenceComment(
    string Id, string PageId, string AuthorId, string Body, DateTimeOffset ModifiedAt, bool Inline = false);

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

        return null;
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
