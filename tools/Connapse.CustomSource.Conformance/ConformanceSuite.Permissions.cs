using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Connapse.CustomSource.Conformance;

public sealed partial class ConformanceSuite
{
    private const string EvaluationsPath = "/access/v1/evaluations";
    private const string KeySearchPath = "/access/v1/search/resource";

    private JsonObject? Permissions => _capabilities?["permissions"] as JsonObject;

    private int? MaxBatch => Permissions?["check"]?["max_batch"] is JsonValue v && v.TryGetValue(out int max) ? max : null;

    private async Task RunPermissionChecksAsync(CancellationToken ct)
    {
        await RunCheckAsync("check.missing-token", "A permission check without a token is refused with 401", CheckMissingTokenAsync, ct);
        await RunCheckAsync("check.unknown-user", "An unknown user is denied every document", CheckUnknownUserAsync, ct);
        await RunCheckAsync("check.unknown-document", "An unknown document is denied", CheckUnknownDocumentAsync, ct);
        await RunCheckAsync("check.order", "A batch answers one result per item, in order, as single checks do", CheckOrderAsync, ct);
        await RunCheckAsync("check.over-batch", "A batch larger than max_batch is refused with 400", CheckOverBatchAsync, ct);
        await RunCheckAsync("keys.every-document", "Every listed document carries at least one access key", KeysOnDocumentsAsync, ct);
        await RunCheckAsync("keys.unknown-user", "An unknown user holds no access keys", KeysUnknownUserAsync, ct);
        await RunCheckAsync("keys.pagination", "Key pages end, repeat nothing, and add up to the same keys at any page size", KeysPaginationAsync, ct);
    }

    private void RequireCheck()
    {
        _ = Documents;
        if (Permissions?["check"] is null) throw new CheckSkippedException("Discovery declares no permissions.check: a content-only middle man.");
    }

    private void RequireKeys()
    {
        RequireCheck();
        if (Permissions?["access_keys"] is null) throw new CheckSkippedException("Discovery declares no permissions.access_keys.");
    }

    private string Reader => options.UserId ?? throw new CheckSkippedException("No reader to check with (--user).");

    private static string UnknownUser() => "connapse-conformance-" + Guid.NewGuid().ToString("N");

    private object EvaluationsBody(string userId, IEnumerable<string> documentIds) => new
    {
        subject = new { type = "user", id = userId },
        action = new { name = "read" },
        evaluations = documentIds.Select(id => new { resource = new { type = "document", id, properties = new { scope = Scope } } }).ToArray(),
    };

    /// <summary>Each item's effective decision: true only for an explicit true without context.error.</summary>
    private async Task<List<bool>> EvaluateAsync(string userId, IReadOnlyList<string> documentIds, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, EvaluationsPath, ct, EvaluationsBody(userId, documentIds));
        JsonObject body = await ExpectJsonAsync(response, HttpStatusCode.OK);
        List<JsonObject> results = Items(body, "evaluations");
        if (results.Count != documentIds.Count)
            throw new CheckFailedException($"A batch of {documentIds.Count} was answered with {results.Count} results.");
        return results.Select(r => r["decision"]?.GetValueKind() == JsonValueKind.True && r["context"]?["error"] is null).ToList();
    }

    private List<string> SampleIds(int count) =>
        Documents.Take(Math.Max(1, Math.Min(count, (MaxBatch ?? count) - 1))).Select(d => d["id"]!.GetValue<string>()).ToList();

    private async Task CheckMissingTokenAsync(CancellationToken ct)
    {
        RequireCheck();
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, EvaluationsPath, ct, token: null,
            EvaluationsBody(UnknownUser(), SampleIds(1)));
        ExpectStatus(response, HttpStatusCode.Unauthorized, "A permission check without a token");
    }

    private async Task CheckUnknownUserAsync(CancellationToken ct)
    {
        RequireCheck();
        List<string> ids = SampleIds(10);
        if ((await EvaluateAsync(UnknownUser(), ids, ct)).Any(allowed => allowed))
            throw new CheckFailedException("A user the middle man cannot know was allowed to read a document.");
    }

    private async Task CheckUnknownDocumentAsync(CancellationToken ct)
    {
        RequireCheck();
        string user = options.UserId ?? UnknownUser();
        if ((await EvaluateAsync(user, ["connapse-conformance-" + Guid.NewGuid().ToString("N")], ct))[0])
            throw new CheckFailedException("A document that does not exist was allowed.");
    }

    private async Task CheckOrderAsync(CancellationToken ct)
    {
        RequireCheck();
        string reader = Reader;
        List<string> ids = SampleIds(10);
        string unknown = "connapse-conformance-" + Guid.NewGuid().ToString("N");
        ids.Insert(Math.Min(1, ids.Count), unknown);

        List<bool> batch = await EvaluateAsync(reader, ids, ct);
        if (batch[ids.IndexOf(unknown)]) throw new CheckFailedException("A document that does not exist was allowed in a batch.");
        if (!batch.Any(allowed => allowed))
            throw new CheckFailedException($"The reader '{reader}' may read none of the first listed documents; give --user a user who can read some.");

        for (int i = 0; i < ids.Count; i++)
        {
            bool single = (await EvaluateAsync(reader, [ids[i]], ct))[0];
            if (single != batch[i])
                throw new CheckFailedException($"Document '{ids[i]}' was {(batch[i] ? "allowed" : "denied")} at position {i} of a batch but {(single ? "allowed" : "denied")} on its own: results are out of order.");
        }
    }

    private async Task CheckOverBatchAsync(CancellationToken ct)
    {
        RequireCheck();
        if (MaxBatch is not { } max) throw new CheckFailedException("permissions.check declares no max_batch.");
        string id = Documents[0]["id"]!.GetValue<string>();
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, EvaluationsPath, ct,
            EvaluationsBody(options.UserId ?? UnknownUser(), Enumerable.Repeat(id, max + 1)));
        ExpectStatus(response, HttpStatusCode.BadRequest, $"A batch of {max + 1}, one over max_batch,");
    }

    private Task KeysOnDocumentsAsync(CancellationToken ct)
    {
        RequireKeys();
        JsonObject? keyless = Documents.FirstOrDefault(d => d["access_keys"] is not JsonArray { Count: > 0 });
        if (keyless is not null)
            throw new CheckFailedException($"Document '{keyless["id"]}' has no access_keys, so it would be ranked for no one and never appear in search.");
        return Task.CompletedTask;
    }

    private async Task KeysUnknownUserAsync(CancellationToken ct)
    {
        RequireKeys();
        if ((await KeysAsync(UnknownUser(), 1000, ct)).Count > 0)
            throw new CheckFailedException("A user the middle man cannot know holds access keys.");
    }

    private async Task KeysPaginationAsync(CancellationToken ct)
    {
        RequireKeys();
        string reader = Reader;
        List<string> whole = await KeysAsync(reader, 1000, ct);
        List<string> paged = await KeysAsync(reader, 1, ct);
        if (whole.Count == 0) throw new CheckFailedException($"The reader '{reader}' holds no access keys in '{Scope}'.");
        if (paged.Distinct(StringComparer.Ordinal).Count() != paged.Count)
            throw new CheckFailedException("Paging one key at a time returned a key more than once.");
        if (!whole.ToHashSet(StringComparer.Ordinal).SetEquals(paged))
            throw new CheckFailedException("Paging one key at a time returned different keys than one large page.");
    }

    private async Task<List<string>> KeysAsync(string userId, int limit, CancellationToken ct)
    {
        List<string> keys = [];
        string token = "";
        for (int page = 0; ; page++)
        {
            if (page == MaxPages) throw new CheckFailedException($"Key search did not end within {MaxPages} pages.");
            using HttpResponseMessage response = await SendAsync(HttpMethod.Post, KeySearchPath, ct, new
            {
                subject = new { type = "user", id = userId },
                action = new { name = "read" },
                resource = new { type = "access_key", properties = new { scope = Scope } },
                page = new { token, limit },
            });
            JsonObject body = await ExpectJsonAsync(response, HttpStatusCode.OK);
            List<JsonObject> results = Items(body, "results");
            if (results.Count > limit) throw new CheckFailedException($"A key page held {results.Count} keys; the limit asked for was {limit}.");
            keys.AddRange(results.Select(r => RequiredString(r, "id", "access key")));

            string next = body["page"] is JsonObject p ? OptionalString(p, "next_token") ?? "" : "";
            if (next.Length == 0) return keys;
            if (next == token) throw new CheckFailedException("Key search returned the same next_token twice.");
            token = next;
        }
    }
}
