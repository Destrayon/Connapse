using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;

namespace Connapse.Core.Tests.Connectors;

/// <summary>A page or blog post as the fake Confluence holds it.</summary>
public sealed record FakeConfluencePage(
    string Id,
    string SpaceId,
    string Title,
    string Body = "<p>Body.</p>",
    string Kind = "page",
    string? ParentId = null,
    string? ParentType = null,
    int Version = 1,
    DateTimeOffset? VersionAt = null);

/// <summary>
/// Confluence's v2 content routes over in-memory state, answered for whichever site the request
/// names. Lists page by an opaque cursor through <c>_links.next</c>, like the real API.
/// </summary>
public sealed partial class FakeConfluence
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Key, string Name)> _spaces = [];
    private readonly HashSet<string> _forbiddenSpaces = [];
    private readonly Dictionary<string, FakeConfluencePage> _pages = [];
    private readonly Dictionary<string, (string Title, string? ParentId, string? ParentType)> _folders = [];
    private readonly Dictionary<string, string> _users = [];

    /// <summary>The most items one list response carries, whatever <c>limit</c> asks for.</summary>
    public int MaxPageSize { get; set; } = 250;

    /// <summary>Every <c>body-format</c> value requested, in order.</summary>
    public List<string> BodyFormats { get; } = [];

    /// <summary>How many times each folder was fetched.</summary>
    public Dictionary<string, int> FolderFetches { get; } = [];

    /// <summary>The account ids asked for in each user bulk lookup.</summary>
    public List<IReadOnlyList<string>> UserLookups { get; } = [];

    public void AddSpace(string id, string key, string name) { lock (_gate) _spaces[id] = (key, name); }

    public void RemoveSpace(string id) { lock (_gate) _spaces.Remove(id); }

    /// <summary>Answers the space 403, as Confluence does once the account loses access.</summary>
    public void ForbidSpace(string id) { lock (_gate) _forbiddenSpaces.Add(id); }

    public void Upsert(FakeConfluencePage page)
    {
        lock (_gate)
            _pages[page.Id] = page with { VersionAt = page.VersionAt ?? Epoch.AddMinutes(page.Version) };
    }

    /// <summary>Bumps a page's version, as an edit does, optionally replacing its body.</summary>
    public void Edit(string id, string? body = null)
    {
        lock (_gate)
        {
            var page = _pages[id];
            _pages[id] = page with
            {
                Body = body ?? page.Body,
                Version = page.Version + 1,
                VersionAt = page.VersionAt!.Value.AddHours(1),
            };
        }
    }

    /// <summary>Renames a page, which Confluence records as a new version.</summary>
    public void Rename(string id, string title)
    {
        Edit(id);
        lock (_gate) _pages[id] = _pages[id] with { Title = title };
    }

    /// <summary>Moves a page under another parent in the same space. Its version does not change.</summary>
    public void Reparent(string id, string? parentId, string? parentType)
    {
        lock (_gate) _pages[id] = _pages[id] with { ParentId = parentId, ParentType = parentType };
    }

    public void RenameSpace(string id, string name) { lock (_gate) _spaces[id] = (_spaces[id].Key, name); }

    public void RenameFolder(string id, string title) { lock (_gate) _folders[id] = _folders[id] with { Title = title }; }

    /// <summary>Answers the next list request that continues from a cursor 429, once.</summary>
    public bool RateLimitNextContinuation { get; set; }

    public void Remove(string id) { lock (_gate) _pages.Remove(id); }

    /// <summary>Moves a page to another space, which drops it from this space's listing.</summary>
    public void Move(string id, string spaceId)
    {
        lock (_gate) _pages[id] = _pages[id] with { SpaceId = spaceId };
    }

    public void AddFolder(string id, string title, string? parentId = null, string? parentType = null)
    {
        lock (_gate) _folders[id] = (title, parentId, parentType);
    }

    public void AddUser(string accountId, string displayName) { lock (_gate) _users[accountId] = displayName; }

    [GeneratedRegex(@"^/ex/confluence/[^/]+/wiki/(?<rest>.+)$")]
    private static partial Regex SitePath();

    [GeneratedRegex(@"^api/v2/spaces/(?<id>\d+)$")]
    private static partial Regex SpaceRoute();

    [GeneratedRegex(@"^api/v2/spaces/(?<id>\d+)/(?<kind>pages|blogposts)$")]
    private static partial Regex SpaceContentRoute();

    [GeneratedRegex(@"^api/v2/(?<kind>pages|blogposts)/(?<id>\d+)$")]
    private static partial Regex ContentRoute();

    [GeneratedRegex(@"^api/v2/folders/(?<id>\d+)$")]
    private static partial Regex FolderRoute();

    /// <summary>The answer for a Confluence request no explicit route claimed.</summary>
    internal HttpResponseMessage Answer(Uri uri)
    {
        lock (_gate)
        {
            var site = SitePath().Match(uri.AbsolutePath);
            if (!site.Success)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            string rest = site.Groups["rest"].Value;
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (query.GetValues("body-format") is { } formats)
                BodyFormats.AddRange(formats);

            if (SpaceRoute().Match(rest) is { Success: true } space)
                return Space(space.Groups["id"].Value);

            if (SpaceContentRoute().Match(rest) is { Success: true } list)
                return List(rest, list.Groups["id"].Value, list.Groups["kind"].Value, query);

            if (ContentRoute().Match(rest) is { Success: true } content)
                return Content(content.Groups["kind"].Value, content.Groups["id"].Value);

            if (FolderRoute().Match(rest) is { Success: true } folder)
                return Folder(folder.Groups["id"].Value);

            if (rest == "rest/api/user/bulk")
                return Users(query.GetValues("accountId") ?? []);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private HttpResponseMessage Space(string id)
    {
        if (_forbiddenSpaces.Contains(id))
            return new HttpResponseMessage(HttpStatusCode.Forbidden);

        return _spaces.TryGetValue(id, out var space)
            ? FakeAtlassianApi.Json(new { id, key = space.Key, name = space.Name, type = "global", status = "current" })
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage List(string rest, string spaceId, string kind, System.Collections.Specialized.NameValueCollection query)
    {
        if (_forbiddenSpaces.Contains(spaceId))
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        if (!_spaces.ContainsKey(spaceId))
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        int limit = Math.Min(int.Parse(query["limit"] ?? "25", CultureInfo.InvariantCulture), MaxPageSize);
        int start = int.Parse(query["cursor"] ?? "0", CultureInfo.InvariantCulture);
        if (start > 0 && RateLimitNextContinuation)
        {
            RateLimitNextContinuation = false;
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.TryAddWithoutValidation("Retry-After", "30");
            return limited;
        }

        string singular = kind == "pages" ? "page" : "blogpost";

        var all = _pages.Values
            .Where(p => p.SpaceId == spaceId && p.Kind == singular)
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
        var slice = all.Skip(start).Take(limit).ToList();

        string? next = start + limit < all.Count
            ? $"/wiki/{rest}?status=current&limit={limit}&cursor={start + limit}"
            : null;

        return FakeAtlassianApi.Json(new
        {
            results = slice.Select(p => new
            {
                id = p.Id,
                title = p.Title,
                status = "current",
                spaceId = p.SpaceId,
                parentId = p.ParentId,
                parentType = p.ParentType,
                version = new { number = p.Version, createdAt = p.VersionAt },
            }),
            _links = new { next },
        });
    }

    private HttpResponseMessage Content(string kind, string id)
    {
        string singular = kind == "pages" ? "page" : "blogpost";
        if (!_pages.TryGetValue(id, out var page) || page.Kind != singular)
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        return FakeAtlassianApi.Json(new
        {
            id = page.Id,
            title = page.Title,
            spaceId = page.SpaceId,
            parentId = page.ParentId,
            parentType = page.ParentType,
            version = new { number = page.Version, createdAt = page.VersionAt },
            body = new { storage = new { value = page.Body, representation = "storage" } },
        });
    }

    private HttpResponseMessage Folder(string id)
    {
        FolderFetches[id] = FolderFetches.GetValueOrDefault(id) + 1;
        return _folders.TryGetValue(id, out var folder)
            ? FakeAtlassianApi.Json(new { id, title = folder.Title, parentId = folder.ParentId, parentType = folder.ParentType })
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Users(string[] accountIds)
    {
        UserLookups.Add(accountIds);
        return FakeAtlassianApi.Json(new
        {
            results = accountIds
                .Where(_users.ContainsKey)
                .Select(a => new { accountId = a, displayName = _users[a], publicName = _users[a] }),
        });
    }
}
