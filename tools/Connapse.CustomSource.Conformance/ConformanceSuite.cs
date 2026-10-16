using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Connapse.CustomSource.Conformance;

/// <summary>
/// Checks a middle man against the custom source protocol, version 1 (<c>docs/protocols/custom-source-v1.md</c>).
/// Each check states one requirement of that document; checks for a capability the discovery document does
/// not declare are skipped. Checks run in order and share what earlier ones found (the scope, the listing,
/// the change cursor), so a failure early on skips what depends on it rather than failing it again.
/// </summary>
public sealed partial class ConformanceSuite(HttpClient http, ConformanceOptions options, IConformanceHooks? hooks = null)
{
    private const int MaxPages = 10_000;
    private readonly IConformanceHooks _hooks = hooks ?? new NoHooks();
    private readonly List<CheckResult> _results = [];

    private JsonObject? _capabilities;
    private string? _scope;
    private List<JsonObject>? _documents;
    private List<string>? _listingPageTokens;
    private string? _changeCursor;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct = default)
    {
        await RunCheckAsync("discovery", "The discovery document lists protocol version 1", DiscoveryAsync, ct);
        await RunCheckAsync("auth.missing-token", "Requests without a token are refused with 401", MissingTokenAsync, ct);
        await RunCheckAsync("auth.wrong-token", "Requests with a wrong token are refused with 401", WrongTokenAsync, ct);
        await RunCheckAsync("scopes.listing", "Scopes page to the end, each with an ID and a name", ScopesAsync, ct);

        await RunCheckAsync("listing.pages", "A full listing pages to one end marker with every required member", ListingAsync, ct);
        await RunCheckAsync("listing.page-size", "No listing page holds more than max_page_size items", PageSizeAsync, ct);
        await RunCheckAsync("listing.stable-token", "A listing page token returns the same page when sent again", StableTokenAsync, ct);
        await RunCheckAsync("listing.deletion-while-paging", "Deleting a document on an earlier page drops no unchanged document from the rest of the listing", DeletionWhilePagingAsync, ct);
        await RunCheckAsync("listing.unknown-token", "An unknown listing page token is answered with 410", UnknownPageTokenAsync, ct);
        await RunCheckAsync("listing.unknown-scope", "An unknown scope is answered with 404", UnknownScopeAsync, ct);
        await RunCheckAsync("content.etag", "Content carries Content-Type and ETag, and If-None-Match is answered with 304", ContentEtagAsync, ct);
        await RunCheckAsync("content.unknown-document", "Content of an unknown document is answered with 404 or 410", UnknownDocumentAsync, ct);

        await RunCheckAsync("changes.shape", "The change feed answers the listing's cursor with a next_cursor", ChangeShapeAsync, ct);
        await RunCheckAsync("changes.unknown-cursor", "An unknown cursor is answered with 410", UnknownCursorAsync, ct);
        await RunCheckAsync("changes.round-trip", "A new document appears as an upsert, and a deleted one as a delete", ChangeRoundTripAsync, ct);

        await RunPermissionChecksAsync(ct);
        await RunLinkingChecksAsync(ct);
        return _results;
    }

    private async Task RunCheckAsync(string id, string title, Func<CancellationToken, Task> body, CancellationToken ct)
    {
        try
        {
            await body(ct);
            _results.Add(new CheckResult(id, title, CheckOutcome.Pass));
        }
        catch (CheckSkippedException ex)
        {
            _results.Add(new CheckResult(id, title, CheckOutcome.Skip, ex.Message));
        }
        catch (CheckFailedException ex)
        {
            _results.Add(new CheckResult(id, title, CheckOutcome.Fail, ex.Message));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            _results.Add(new CheckResult(id, title, CheckOutcome.Fail, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    // ── Core ─────────────────────────────────────────────────────────────

    private async Task DiscoveryAsync(CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/.well-known/connapse-source", ct);
        JsonObject body = await ExpectJsonAsync(response, HttpStatusCode.OK);
        if (body["protocol_versions"] is not JsonArray versions || !versions.Any(v => v?.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == "1"))
            throw new CheckFailedException("protocol_versions does not contain \"1\".");
        _capabilities = body["capabilities"] as JsonObject ?? [];
    }

    private async Task MissingTokenAsync(CancellationToken ct)
    {
        foreach (string path in new[] { "/.well-known/connapse-source", "/v1/scopes" })
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, ct, token: null);
            ExpectStatus(response, HttpStatusCode.Unauthorized, $"GET {path} without a token");
        }
    }

    private async Task WrongTokenAsync(CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/v1/scopes", ct, token: options.Token + "-wrong");
        ExpectStatus(response, HttpStatusCode.Unauthorized, "GET /v1/scopes with a wrong token");
    }

    private async Task ScopesAsync(CancellationToken ct)
    {
        List<string> ids = [];
        string? pageToken = null;
        for (int page = 0; ; page++)
        {
            if (page == MaxPages) throw new CheckFailedException($"The scopes listing did not end within {MaxPages} pages.");
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/v1/scopes" + Query(("page_token", pageToken)), ct);
            JsonObject body = await ExpectJsonAsync(response, HttpStatusCode.OK);
            foreach (JsonObject item in Items(body, "items"))
            {
                string id = RequiredString(item, "id", "scope");
                RequiredString(item, "name", $"scope '{id}'");
                ids.Add(id);
            }

            string? next = OptionalString(body, "next_page_token");
            if (string.IsNullOrEmpty(next)) break;
            if (next == pageToken) throw new CheckFailedException("The scopes listing returned the same next_page_token twice.");
            pageToken = next;
        }

        if (ids.Count == 0) throw new CheckFailedException("No scopes are offered.");
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw new CheckFailedException("A scope ID appears more than once.");
        if (options.Scope is { } wanted && !ids.Contains(wanted, StringComparer.Ordinal))
            throw new CheckFailedException($"The scope '{wanted}' is not offered.");
        _scope = options.Scope ?? ids[0];
    }

    // ── Content ──────────────────────────────────────────────────────────

    private string Scope => _scope ?? throw new CheckSkippedException("No scope to test: the scopes check did not pass.");

    private List<JsonObject> Documents => _documents ?? throw new CheckSkippedException("No listing to test: the listing check did not pass.");

    private bool Capability(string name) => _capabilities?[name]?.GetValueKind() == JsonValueKind.True;

    private int? MaxPageSize => _capabilities?["max_page_size"] is JsonValue v && v.TryGetValue(out int size) ? size : null;

    private string DocumentsPath(string scope) => $"/v1/scopes/{Uri.EscapeDataString(scope)}/documents";

    private async Task ListingAsync(CancellationToken ct)
    {
        string scope = Scope;
        List<JsonObject> documents = [];
        List<string> tokens = [];
        string? pageToken = null;
        int? largestPage = null;
        for (int page = 0; ; page++)
        {
            if (page == MaxPages) throw new CheckFailedException($"The listing did not end within {MaxPages} pages.");
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, DocumentsPath(scope) + Query(("page_token", pageToken)), ct);
            JsonObject body = await ExpectJsonAsync(response, HttpStatusCode.OK);
            List<JsonObject> items = Items(body, "items");
            largestPage = Math.Max(largestPage ?? 0, items.Count);
            foreach (JsonObject item in items)
            {
                CheckListingItem(item);
                documents.Add(item);
            }

            string? next = OptionalString(body, "next_page_token");
            string? cursor = OptionalString(body, "change_cursor");
            if (!string.IsNullOrEmpty(next) && !string.IsNullOrEmpty(cursor))
                throw new CheckFailedException("A listing page carries both next_page_token and change_cursor.");
            if (string.IsNullOrEmpty(next))
            {
                if (Capability("changes") && string.IsNullOrEmpty(cursor))
                    throw new CheckFailedException("The last listing page has no change_cursor, though discovery declares changes.");
                _changeCursor = cursor;
                break;
            }

            if (next == pageToken) throw new CheckFailedException("The listing returned the same next_page_token twice.");
            tokens.Add(next);
            pageToken = next;
        }

        IEnumerable<string> ids = documents.Select(d => d["id"]!.GetValue<string>());
        if (ids.Distinct(StringComparer.Ordinal).Count() != documents.Count)
            throw new CheckFailedException("A document ID appears more than once in one full listing.");
        if (documents.Count == 0)
            throw new CheckFailedException($"The scope '{scope}' lists no documents; the content checks need at least one.");

        _documents = documents;
        _listingPageTokens = tokens;
        _largestListingPage = largestPage;
    }

    private int? _largestListingPage;

    private static void CheckListingItem(JsonObject item)
    {
        string id = RequiredString(item, "id", "document");
        if (id.Length > 512) throw new CheckFailedException($"Document ID '{id[..40]}…' is longer than 512 characters.");
        RequiredString(item, "path", $"document '{id}'");
        RequiredString(item, "content_type", $"document '{id}'");
        RequiredString(item, "version", $"document '{id}'");
        if (item["size"] is not JsonValue size || !size.TryGetValue(out long _))
            throw new CheckFailedException($"Document '{id}' has no numeric size.");
    }

    private Task PageSizeAsync(CancellationToken ct)
    {
        _ = Documents;
        if (MaxPageSize is not { } max) throw new CheckSkippedException("Discovery declares no max_page_size.");
        if (_largestListingPage > max)
            throw new CheckFailedException($"A listing page held {_largestListingPage} items; max_page_size is {max}.");
        return Task.CompletedTask;
    }

    private async Task StableTokenAsync(CancellationToken ct)
    {
        _ = Documents;
        if (_listingPageTokens is not [{ } token, ..])
            throw new CheckSkippedException("The listing fits on one page, so there is no page token to send again.");

        async Task<List<string>> PageIdsAsync()
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, DocumentsPath(Scope) + Query(("page_token", token)), ct);
            return Items(await ExpectJsonAsync(response, HttpStatusCode.OK), "items").Select(i => i["id"]!.GetValue<string>()).ToList();
        }

        List<string> first = await PageIdsAsync();
        List<string> second = await PageIdsAsync();
        if (!first.SequenceEqual(second, StringComparer.Ordinal))
            throw new CheckFailedException("The same page token returned different documents the second time.");
    }

    /// <summary>
    /// The failure the protocol warns of: with offset page tokens, deleting a document on a page already read
    /// shifts an unchanged one back across the boundary, so it appears in no page and Connapse removes it.
    /// Documents the hook creates are deleted mid-listing, and every other document must still appear once.
    /// </summary>
    private async Task DeletionWhilePagingAsync(CancellationToken ct)
    {
        _ = Documents;
        await WithOwnedDocumentsAsync(async owned =>
        {
            List<string> expected = (await ListIdsAsync(ct)).SelectMany(p => p).ToList();
            for (int i = 0; i < 3; i++) expected.Add(await CreateOwnedAsync(owned, ct));

            List<string> seen = [];
            string? deleted = null;
            string? pageToken = null;
            for (int page = 0; ; page++)
            {
                if (page == MaxPages) throw new CheckFailedException($"The listing did not end within {MaxPages} pages.");
                using HttpResponseMessage response = await SendAsync(HttpMethod.Get, DocumentsPath(Scope) + Query(("page_token", pageToken)), ct);
                JsonObject body = await ExpectJsonAsync(response, HttpStatusCode.OK);
                List<string> ids = Items(body, "items").Select(i => RequiredString(i, "id", "document")).ToList();
                seen.AddRange(ids);
                string? next = OptionalString(body, "next_page_token");
                if (string.IsNullOrEmpty(next)) break;

                if (deleted is null && ids.FirstOrDefault(owned.Contains) is { } victim)
                {
                    await DeleteOwnedAsync(owned, victim, ct);
                    deleted = victim;
                }

                pageToken = next;
            }

            if (deleted is null)
                throw new CheckSkippedException("No test document landed on a page before the last, so paging during a deletion went untested; it needs a listing of several pages.");

            string? missing = expected.Where(id => id != deleted).FirstOrDefault(id => seen.Count(s => s == id) != 1);
            if (missing is not null)
                throw new CheckFailedException(
                    $"Document '{missing}' appeared {seen.Count(s => s == missing)} times after a document on an earlier page was deleted: page tokens shift like offsets.");
        }, ct);
    }

    /// <summary>
    /// Runs <paramref name="body"/> with a list of the documents it creates, then deletes whichever are left,
    /// reporting any that could not be: test documents must not stay behind in someone's source.
    /// </summary>
    private async Task WithOwnedDocumentsAsync(Func<List<string>, Task> body, CancellationToken ct)
    {
        if (!_hooks.CanChangeDocuments)
            throw new CheckSkippedException("Needs hooks to create and delete documents (--create-command and --delete-command).");

        List<string> owned = [];
        Exception? failure = null;
        try
        {
            await body(owned);
        }
        catch (Exception ex) when (ex is CheckFailedException or CheckSkippedException or HttpRequestException or JsonException or InvalidOperationException)
        {
            failure = ex;
        }

        List<string> left = [];
        foreach (string id in owned)
        {
            try
            {
                if (!await _hooks.DeleteDocumentAsync(Scope, id, ct)) left.Add(id);
            }
            catch (CheckFailedException)
            {
                left.Add(id);
            }
        }

        string leftover = left.Count == 0 ? "" : $" Test documents {string.Join(", ", left)} could not be deleted; remove them by hand.";
        switch (failure)
        {
            case CheckSkippedException skipped when left.Count == 0:
                throw skipped;
            case null when left.Count == 0:
                return;
            case null:
                throw new CheckFailedException(leftover.Trim());
            default:
                throw new CheckFailedException((failure is CheckFailedException or CheckSkippedException ? failure.Message : $"{failure.GetType().Name}: {failure.Message}") + leftover);
        }
    }

    private async Task<string> CreateOwnedAsync(List<string> owned, CancellationToken ct)
    {
        string id = await _hooks.CreateDocumentAsync(Scope, ct)
            ?? throw new CheckFailedException("The create hook returned no document ID.");
        owned.Add(id);
        return id;
    }

    private async Task DeleteOwnedAsync(List<string> owned, string id, CancellationToken ct)
    {
        if (!await _hooks.DeleteDocumentAsync(Scope, id, ct)) throw new CheckFailedException($"The delete hook could not delete '{id}'.");
        owned.Remove(id);
    }

    /// <summary>The listing's document IDs, page by page.</summary>
    private async Task<List<List<string>>> ListIdsAsync(CancellationToken ct)
    {
        List<List<string>> pages = [];
        string? pageToken = null;
        for (int page = 0; ; page++)
        {
            if (page == MaxPages) throw new CheckFailedException($"The listing did not end within {MaxPages} pages.");
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, DocumentsPath(Scope) + Query(("page_token", pageToken)), ct);
            JsonObject body = await ExpectJsonAsync(response, HttpStatusCode.OK);
            pages.Add(Items(body, "items").Select(i => RequiredString(i, "id", "document")).ToList());
            string? next = OptionalString(body, "next_page_token");
            if (string.IsNullOrEmpty(next)) return pages;
            pageToken = next;
        }
    }

    private async Task UnknownPageTokenAsync(CancellationToken ct)
    {
        _ = Documents;
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get,
            DocumentsPath(Scope) + Query(("page_token", "connapse-conformance-unknown-" + Guid.NewGuid().ToString("N"))), ct);
        ExpectStatus(response, HttpStatusCode.Gone, "A listing page token the middle man never issued");
    }

    private async Task UnknownScopeAsync(CancellationToken ct)
    {
        _ = Scope;
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, DocumentsPath("connapse-conformance-" + Guid.NewGuid().ToString("N")), ct);
        ExpectStatus(response, HttpStatusCode.NotFound, "The listing of a scope that does not exist");
    }

    private string ContentPath(string id) => $"{DocumentsPath(Scope)}/{Uri.EscapeDataString(id)}/content";

    private async Task ContentEtagAsync(CancellationToken ct)
    {
        string id = Documents[0]["id"]!.GetValue<string>();
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, ContentPath(id), ct);
        ExpectStatus(response, HttpStatusCode.OK, $"Content of document '{id}'");
        if (response.Content.Headers.ContentType is null)
            throw new CheckFailedException($"Content of document '{id}' has no Content-Type.");
        if (response.Headers.ETag is not { } etag)
            throw new CheckFailedException($"Content of document '{id}' has no ETag.");

        using HttpRequestMessage again = Request(HttpMethod.Get, ContentPath(id), options.Token);
        again.Headers.IfNoneMatch.Add(etag);
        using HttpResponseMessage unchanged = await http.SendAsync(again, ct);
        ExpectStatus(unchanged, HttpStatusCode.NotModified, $"Content of document '{id}' with If-None-Match: {etag}");
    }

    private async Task UnknownDocumentAsync(CancellationToken ct)
    {
        _ = Scope;
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, ContentPath("connapse-conformance-" + Guid.NewGuid().ToString("N")), ct);
        if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone))
            throw new CheckFailedException($"Content of a document that does not exist answered {(int)response.StatusCode}, not 404 or 410.");
    }

    // ── Change feed ──────────────────────────────────────────────────────

    private string ChangesPath => $"/v1/scopes/{Uri.EscapeDataString(Scope)}/changes";

    private void RequireChanges()
    {
        _ = Documents;
        if (!Capability("changes")) throw new CheckSkippedException("Discovery does not declare changes.");
        if (_changeCursor is null) throw new CheckSkippedException("The listing gave no change_cursor.");
    }

    private async Task ChangeShapeAsync(CancellationToken ct)
    {
        RequireChanges();
        (_, string nextCursor) = await ReadChangesAsync(_changeCursor!, ct);
        if (string.IsNullOrEmpty(nextCursor)) throw new CheckFailedException("The change feed's last page has no next_cursor.");
    }

    private async Task UnknownCursorAsync(CancellationToken ct)
    {
        RequireChanges();
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get,
            ChangesPath + Query(("cursor", "connapse-conformance-unknown-" + Guid.NewGuid().ToString("N"))), ct);
        ExpectStatus(response, HttpStatusCode.Gone, "A change cursor the middle man never issued");
    }

    private async Task ChangeRoundTripAsync(CancellationToken ct)
    {
        RequireChanges();
        await WithOwnedDocumentsAsync(async owned =>
        {
            // The cursor is read before the change, so the change must come after it.
            (_, string cursor) = await ReadChangesAsync(_changeCursor!, ct);
            string newId = await CreateOwnedAsync(owned, ct);

            (List<JsonObject> created, string afterCreate) = await ReadChangesAsync(cursor, ct);
            JsonObject upsert = created.LastOrDefault(c => OptionalString(c, "op") == "upsert" && OptionalString(c, "id") == newId)
                ?? throw new CheckFailedException($"After document '{newId}' was created, the change feed reported no upsert for it.");
            CheckListingItem(upsert);

            using (HttpResponseMessage content = await SendAsync(HttpMethod.Get, ContentPath(newId), ct))
                ExpectStatus(content, HttpStatusCode.OK, $"Content of the created document '{newId}'");

            await DeleteOwnedAsync(owned, newId, ct);
            (List<JsonObject> deleted, _) = await ReadChangesAsync(afterCreate, ct);
            if (Capability("tombstones") && !deleted.Any(c => OptionalString(c, "op") == "delete" && OptionalString(c, "id") == newId))
                throw new CheckFailedException($"After document '{newId}' was deleted, the change feed reported no delete for it, though discovery declares tombstones.");

            using HttpResponseMessage gone = await SendAsync(HttpMethod.Get, ContentPath(newId), ct);
            if (gone.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone))
                throw new CheckFailedException($"Content of the deleted document '{newId}' answered {(int)gone.StatusCode}, not 404 or 410.");
        }, ct);
    }

    /// <summary>Every change after <paramref name="cursor"/>, across pages, and the cursor to continue from.</summary>
    private async Task<(List<JsonObject> Items, string NextCursor)> ReadChangesAsync(string cursor, CancellationToken ct)
    {
        List<JsonObject> all = [];
        string? pageToken = null;
        for (int page = 0; ; page++)
        {
            if (page == MaxPages) throw new CheckFailedException($"The change feed did not end within {MaxPages} pages.");
            string query = pageToken is null ? Query(("cursor", cursor)) : Query(("page_token", pageToken));
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, ChangesPath + query, ct);
            JsonObject body = await ExpectJsonAsync(response, HttpStatusCode.OK);
            foreach (JsonObject item in Items(body, "items"))
            {
                string op = RequiredString(item, "op", "change");
                RequiredString(item, "id", "change");
                if (op is not ("upsert" or "delete")) throw new CheckFailedException($"A change has op '{op}', not upsert or delete.");
                all.Add(item);
            }

            string? next = OptionalString(body, "next_page_token");
            string? nextCursor = OptionalString(body, "next_cursor");
            if (!string.IsNullOrEmpty(next) && !string.IsNullOrEmpty(nextCursor))
                throw new CheckFailedException("A change page carries both next_page_token and next_cursor.");
            if (string.IsNullOrEmpty(next))
                return (all, nextCursor ?? throw new CheckFailedException("The change feed's last page has no next_cursor."));
            if (next == pageToken) throw new CheckFailedException("The change feed returned the same next_page_token twice.");
            pageToken = next;
        }
    }

    // ── HTTP and JSON helpers ────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private Uri Url(string path) => new(options.BaseUrl.ToString().TrimEnd('/') + path);

    private HttpRequestMessage Request(HttpMethod method, string path, string? token, object? body = null)
    {
        HttpRequestMessage request = new(method, Url(path));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken ct, object? body = null) =>
        await SendAsync(method, path, ct, options.Token, body);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken ct, string? token, object? body = null)
    {
        using HttpRequestMessage request = Request(method, path, token, body);
        return await http.SendAsync(request, ct);
    }

    private static string Query(params (string Name, string? Value)[] parameters)
    {
        string[] parts = parameters.Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}").ToArray();
        return parts.Length == 0 ? "" : "?" + string.Join('&', parts);
    }

    private static void ExpectStatus(HttpResponseMessage response, HttpStatusCode expected, string what)
    {
        if (response.StatusCode != expected)
            throw new CheckFailedException($"{what} answered {(int)response.StatusCode}, not {(int)expected}.");
    }

    private static async Task<JsonObject> ExpectJsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        string url = response.RequestMessage?.RequestUri?.PathAndQuery ?? "the request";
        ExpectStatus(response, expected, url);
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject
                ?? throw new CheckFailedException($"{url} did not answer with a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new CheckFailedException($"{url} did not answer with valid JSON: {ex.Message}");
        }
    }

    private static List<JsonObject> Items(JsonObject body, string member)
    {
        if (body[member] is null) return [];
        if (body[member] is not JsonArray array) throw new CheckFailedException($"'{member}' is not an array.");
        return array.Select(n => n as JsonObject ?? throw new CheckFailedException($"An item in '{member}' is not an object.")).ToList();
    }

    private static string RequiredString(JsonObject item, string member, string what)
    {
        if (item[member] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } s)
            return s;
        throw new CheckFailedException($"A {what} has no '{member}'.");
    }

    private static string? OptionalString(JsonObject item, string member) =>
        item[member] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
