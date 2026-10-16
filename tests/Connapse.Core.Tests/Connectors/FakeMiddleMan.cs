using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// An in-memory custom source middle man for one scope, speaking just enough of protocol v1 for the
/// connector: discovery, the full listing (paged by last ID), the change feed (410 for a cursor it did not
/// issue), and content. Rate limiting and server errors can be forced on the next request.
/// <para>
/// It also answers the permission endpoints: a document's access keys default to its top-level folder
/// (<c>folder:design</c>, or <c>folder:</c> at the root); <see cref="Grants"/> says which keys each Connapse
/// user holds, and <see cref="Denies"/> names documents a user may not read even with the key.
/// </para>
/// </summary>
public sealed class FakeMiddleMan : HttpMessageHandler
{
    public const string BaseUrl = "https://bridge.acme.test/api";
    public const string Scope = "eng";
    public const string Token = "fake-token";
    public const string LinkUrl = "https://bridge.acme.test/link";

    private readonly SortedDictionary<string, FakeDocument> _documents = new(StringComparer.Ordinal);
    private readonly List<(string Id, bool Deleted)> _journal = [];

    public int PageSize { get; set; } = 2;
    public bool SupportsChanges { get; set; } = true;
    public bool SupportsTombstones { get; set; } = true;
    public bool SupportsPermissions { get; set; } = true;
    public bool SupportsAccessKeys { get; set; } = true;
    public int MaxCheckBatch { get; set; } = 100;
    public bool SupportsLinking { get; set; }

    /// <summary>Connapse user ID → the linked account's display name: the middle man's own link table.</summary>
    public Dictionary<string, string> Links { get; } = new(StringComparer.Ordinal);

    /// <summary>One-time code → the pending link it activates, as the linking page leaves it.</summary>
    public Dictionary<string, (string ConnapseUserId, string DisplayName)> PendingLinks { get; } = new(StringComparer.Ordinal);

    /// <summary>Every user ID an unlink call named, in order.</summary>
    public List<string> Unlinked { get; } = [];

    /// <summary>Connapse user ID → access keys the user holds.</summary>
    public Dictionary<string, List<string>> Grants { get; } = new(StringComparer.Ordinal);

    /// <summary>Connapse user ID → document IDs denied despite a matching key.</summary>
    public Dictionary<string, HashSet<string>> Denies { get; } = new(StringComparer.Ordinal);

    /// <summary>How the next evaluations call misbehaves, if at all.</summary>
    public EvaluationQuirk Quirk { get; set; }

    /// <summary>Answers requests whatever token they carry, as a carelessly built middle man might.</summary>
    public bool AcceptAnyToken { get; set; }

    /// <summary>Every paged response repeats the same next-page token, as a broken middle man might.</summary>
    public bool RepeatPageToken { get; set; }
    public HttpStatusCode? FailNextWith { get; set; }
    public List<string> Requests { get; } = [];
    public string? LastAuthorization { get; private set; }

    /// <summary>The number of evaluations calls answered.</summary>
    public int EvaluationCalls { get; private set; }

    public void Put(
        string id, string path, string content, string contentType = "text/markdown", string? version = null,
        IReadOnlyList<string>? keys = null)
    {
        int slash = path.IndexOf('/');
        keys ??= [slash < 0 ? "folder:" : $"folder:{path[..slash]}"];
        _documents[id] = new FakeDocument(id, path, content, contentType, version ?? Hash(content), keys);
        _journal.Add((id, false));
    }

    public void Remove(string id)
    {
        _documents.Remove(id);
        _journal.Add((id, true));
    }

    /// <summary>When set, every request waits for it before answering, so a caller can be caught mid-request.</summary>
    public TaskCompletionSource? Hold { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Hold is { } hold) await hold.Task;
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        return Respond(request, body);
    }

    private HttpResponseMessage Respond(HttpRequestMessage request, string? body)
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
            return failed;
        }

        if (!AcceptAnyToken && LastAuthorization != $"Bearer {Token}")
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        string path = request.RequestUri.AbsolutePath;
        string prefix = new Uri(BaseUrl).AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        path = path[prefix.Length..];
        Dictionary<string, string> query = ParseQuery(request.RequestUri.Query);

        return path switch
        {
            "/.well-known/connapse-source" => Json(Discovery()),
            "/v1/scopes" => Json(new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["id"] = Scope, ["name"] = "Engineering" }),
                ["next_page_token"] = RepeatPageToken ? "again" : null,
            }),
            $"/v1/scopes/{Scope}/documents" => Listing(query.GetValueOrDefault("page_token")),
            $"/v1/scopes/{Scope}/changes" => Changes(query.GetValueOrDefault("page_token") ?? query.GetValueOrDefault("cursor")),
            _ when path.StartsWith($"/v1/scopes/{Scope}/documents/", StringComparison.Ordinal) && path.EndsWith("/content", StringComparison.Ordinal)
                => Content(Uri.UnescapeDataString(path[$"/v1/scopes/{Scope}/documents/".Length..^"/content".Length])),
            "/access/v1/search/resource" when SupportsPermissions && SupportsAccessKeys => SearchKeys(JsonNode.Parse(body ?? "{}")!),
            "/access/v1/evaluations" when SupportsPermissions => Evaluate(JsonNode.Parse(body ?? "{}")!),
            "/v1/links/confirm" when SupportsLinking && request.Method == HttpMethod.Post => ConfirmLink(JsonNode.Parse(body ?? "{}")!),
            _ when SupportsLinking && path.StartsWith("/v1/links/", StringComparison.Ordinal)
                => LinkStatus(request.Method, Uri.UnescapeDataString(path["/v1/links/".Length..])),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private JsonObject Discovery()
    {
        JsonObject capabilities = new() { ["changes"] = SupportsChanges, ["tombstones"] = SupportsChanges && SupportsTombstones };
        if (SupportsPermissions)
        {
            JsonObject permissions = new() { ["check"] = new JsonObject { ["max_batch"] = MaxCheckBatch } };
            if (SupportsAccessKeys) permissions["access_keys"] = new JsonObject { ["cache_ttl_seconds"] = 0 };
            if (SupportsLinking) permissions["linking"] = new JsonObject { ["link_url"] = LinkUrl };
            capabilities["permissions"] = permissions;
        }

        return new JsonObject { ["protocol_versions"] = new JsonArray("1"), ["name"] = "Fake", ["capabilities"] = capabilities };
    }

    private HttpResponseMessage ConfirmLink(JsonNode request)
    {
        string code = request["code"]?.GetValue<string>() ?? "";
        string user = request["connapse_user_id"]?.GetValue<string>() ?? "";
        // Spent either way, as the protocol requires: a refused code cannot be retried by its rightful user.
        if (!PendingLinks.Remove(code, out var pending) || pending.ConnapseUserId != user)
            return new HttpResponseMessage(HttpStatusCode.Conflict);

        Links[user] = pending.DisplayName;
        return Json(new JsonObject { ["linked"] = true, ["display_name"] = pending.DisplayName });
    }

    private HttpResponseMessage LinkStatus(HttpMethod method, string user)
    {
        if (method == HttpMethod.Delete)
        {
            Unlinked.Add(user);
            return new HttpResponseMessage(Links.Remove(user) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
        }

        return Json(new JsonObject { ["linked"] = Links.ContainsKey(user), ["display_name"] = Links.GetValueOrDefault(user) });
    }

    private HttpResponseMessage SearchKeys(JsonNode request)
    {
        string user = request["subject"]?["id"]?.GetValue<string>() ?? "";
        List<string> keys = Grants.GetValueOrDefault(user) ?? [];
        string token = request["page"]?["token"]?.GetValue<string>() ?? "";
        int offset = int.TryParse(token, out int parsed) ? parsed : 0;
        int limit = Math.Min(request["page"]?["limit"]?.GetValue<int>() ?? PageSize, PageSize);
        List<string> page = keys.Skip(offset).Take(limit).ToList();
        string next = RepeatPageToken ? "again" : offset + page.Count < keys.Count ? (offset + page.Count).ToString() : "";
        return Json(new JsonObject
        {
            ["results"] = new JsonArray(page.Select(k => (JsonNode)new JsonObject { ["type"] = "access_key", ["id"] = k }).ToArray()),
            ["page"] = new JsonObject { ["next_token"] = next },
        });
    }

    private HttpResponseMessage Evaluate(JsonNode request)
    {
        EvaluationCalls++;
        if (Quirk == EvaluationQuirk.ServerError) return new HttpResponseMessage(HttpStatusCode.InternalServerError);

        string user = request["subject"]?["id"]?.GetValue<string>() ?? "";
        JsonArray items = request["evaluations"]?.AsArray() ?? [];
        if (items.Count > MaxCheckBatch) return new HttpResponseMessage(HttpStatusCode.BadRequest);

        HashSet<string> held = [.. Grants.GetValueOrDefault(user) ?? []];
        HashSet<string> denied = Denies.GetValueOrDefault(user) ?? [];
        JsonArray decisions = [];
        foreach (JsonNode? item in items)
        {
            string? id = item?["resource"]?["id"]?.GetValue<string>();
            bool allowed = id is not null && _documents.TryGetValue(id, out FakeDocument? d)
                && d.Keys.Any(held.Contains) && !denied.Contains(id);
            decisions.Add(Quirk == EvaluationQuirk.ErrorAlongsideTrue
                ? new JsonObject { ["decision"] = true, ["context"] = new JsonObject { ["error"] = "timeout" } }
                : new JsonObject { ["decision"] = allowed });
        }

        if (Quirk == EvaluationQuirk.WrongCount && decisions.Count > 0) decisions.RemoveAt(decisions.Count - 1);
        return Json(new JsonObject { ["evaluations"] = decisions });
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

    private JsonObject Item(FakeDocument d)
    {
        JsonObject item = new()
        {
            ["id"] = d.Id,
            ["path"] = d.Path,
            ["title"] = d.Path,
            ["content_type"] = d.ContentType,
            ["size"] = Encoding.UTF8.GetByteCount(d.Content),
            ["version"] = d.Version,
            ["modified_at"] = "2026-10-13T19:20:00Z",
        };
        if (SupportsPermissions && SupportsAccessKeys)
        {
            item["access_keys"] = new JsonArray(d.Keys.Select(k => (JsonNode)k).ToArray());
            item["access_version"] = Hash(string.Join('\n', d.Keys));
        }
        return item;
    }

    private JsonObject Upsert(FakeDocument d)
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

    private sealed record FakeDocument(string Id, string Path, string Content, string ContentType, string Version, IReadOnlyList<string> Keys);
}

/// <summary>Ways a middle man's evaluations answer can be wrong.</summary>
public enum EvaluationQuirk
{
    None,
    ErrorAlongsideTrue,
    WrongCount,
    ServerError,
}
