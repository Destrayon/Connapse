using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// An in-memory custom source middle man for one scope, speaking just enough of protocol v1 for the
/// connector: discovery, the full listing (paged by last ID), the change feed (410 for a cursor it did not
/// issue), and content. Rate limiting and server errors can be forced on the next request.
/// </summary>
public sealed class FakeMiddleMan : HttpMessageHandler
{
    public const string BaseUrl = "https://bridge.acme.test/api";
    public const string Scope = "eng";
    public const string Token = "fake-token";

    private readonly SortedDictionary<string, FakeDocument> _documents = new(StringComparer.Ordinal);
    private readonly List<(string Id, bool Deleted)> _journal = [];

    public int PageSize { get; set; } = 2;
    public bool SupportsChanges { get; set; } = true;
    public bool SupportsTombstones { get; set; } = true;

    /// <summary>Every paged response repeats the same next-page token, as a broken middle man might.</summary>
    public bool RepeatPageToken { get; set; }
    public HttpStatusCode? FailNextWith { get; set; }
    public List<string> Requests { get; } = [];
    public string? LastAuthorization { get; private set; }

    public void Put(string id, string path, string content, string contentType = "text/markdown", string? version = null)
    {
        _documents[id] = new FakeDocument(id, path, content, contentType, version ?? Hash(content));
        _journal.Add((id, false));
    }

    public void Remove(string id)
    {
        _documents.Remove(id);
        _journal.Add((id, true));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string pathAndQuery = request.RequestUri!.PathAndQuery;
        Requests.Add(pathAndQuery);
        LastAuthorization = request.Headers.Authorization?.ToString();

        if (FailNextWith is { } status)
        {
            FailNextWith = null;
            HttpResponseMessage failed = new(status);
            if (status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.ServiceUnavailable)
                failed.Headers.TryAddWithoutValidation("Retry-After", "30");
            return Task.FromResult(failed);
        }

        if (LastAuthorization != $"Bearer {Token}")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        string path = request.RequestUri.AbsolutePath;
        string prefix = new Uri(BaseUrl).AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        path = path[prefix.Length..];
        Dictionary<string, string> query = ParseQuery(request.RequestUri.Query);

        return Task.FromResult(path switch
        {
            "/.well-known/connapse-source" => Json(new JsonObject
            {
                ["protocol_versions"] = new JsonArray("1"),
                ["name"] = "Fake",
                ["capabilities"] = new JsonObject { ["changes"] = SupportsChanges, ["tombstones"] = SupportsChanges && SupportsTombstones },
            }),
            "/v1/scopes" => Json(new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["id"] = Scope, ["name"] = "Engineering" }),
                ["next_page_token"] = RepeatPageToken ? "again" : null,
            }),
            $"/v1/scopes/{Scope}/documents" => Listing(query.GetValueOrDefault("page_token")),
            $"/v1/scopes/{Scope}/changes" => Changes(query.GetValueOrDefault("page_token") ?? query.GetValueOrDefault("cursor")),
            _ when path.StartsWith($"/v1/scopes/{Scope}/documents/", StringComparison.Ordinal) && path.EndsWith("/content", StringComparison.Ordinal)
                => Content(Uri.UnescapeDataString(path[$"/v1/scopes/{Scope}/documents/".Length..^"/content".Length])),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    private HttpResponseMessage Listing(string? pageToken)
    {
        string after = pageToken ?? "";
        List<FakeDocument> remaining = _documents.Values.Where(d => string.CompareOrdinal(d.Id, after) > 0).ToList();
        List<FakeDocument> page = remaining.Take(PageSize).ToList();
        JsonObject body = new() { ["items"] = new JsonArray(page.Select(Item).ToArray<JsonNode?>()) };
        if (RepeatPageToken) body["next_page_token"] = "again";
        else if (remaining.Count > page.Count) body["next_page_token"] = page[^1].Id;
        else body["change_cursor"] = $"c{_journal.Count}";
        return Json(body);
    }

    private HttpResponseMessage Changes(string? cursor)
    {
        if (cursor is null || !cursor.StartsWith('c') || !int.TryParse(cursor[1..], out int from) || from > _journal.Count)
            return new HttpResponseMessage(HttpStatusCode.Gone);

        int end = Math.Min(from + PageSize, _journal.Count);
        JsonArray items = [];
        foreach ((string id, bool deleted) in _journal.Skip(from).Take(end - from))
            items.Add(deleted || !_documents.TryGetValue(id, out FakeDocument? d)
                ? new JsonObject { ["id"] = id, ["op"] = "delete" }
                : Upsert(d));
        JsonObject body = new() { ["items"] = items };
        if (end < _journal.Count) body["next_page_token"] = $"c{end}";
        else body["next_cursor"] = $"c{end}";
        return Json(body);
    }

    private HttpResponseMessage Content(string id) =>
        _documents.TryGetValue(id, out FakeDocument? d)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(d.Content, Encoding.UTF8, d.ContentType) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);

    private static JsonObject Item(FakeDocument d) => new()
    {
        ["id"] = d.Id,
        ["path"] = d.Path,
        ["title"] = d.Path,
        ["content_type"] = d.ContentType,
        ["size"] = Encoding.UTF8.GetByteCount(d.Content),
        ["version"] = d.Version,
        ["modified_at"] = "2026-10-13T19:20:00Z",
    };

    private static JsonObject Upsert(FakeDocument d)
    {
        JsonObject item = Item(d);
        item["op"] = "upsert";
        return item;
    }

    private static HttpResponseMessage Json(JsonObject body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");

    private static string Hash(string content) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..12];

    private sealed record FakeDocument(string Id, string Path, string Content, string ContentType, string Version);
}
