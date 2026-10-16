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
        await RunCheckAsync("permissions.declared", "A middle man declaring any permission capability declares check with a positive max_batch", PermissionsDeclaredAsync, ct);
        await RunCheckAsync("check.missing-token", "A permission check without a token is refused with 401", CheckMissingTokenAsync, ct);
        await RunCheckAsync("check.unknown-user", "An unknown user is denied every document", CheckUnknownUserAsync, ct);
        await RunCheckAsync("check.unknown-document", "An unknown document is denied", CheckUnknownDocumentAsync, ct);
        await RunCheckAsync("check.order", "A batch answers one result per item, in order, as single checks do", CheckOrderAsync, ct);
        await RunCheckAsync("check.over-batch", "A batch larger than max_batch is refused with 400", CheckOverBatchAsync, ct);
        await RunCheckAsync("keys.every-document", "Every listed document carries at least one access key", KeysOnDocumentsAsync, ct);
        await RunCheckAsync("keys.unknown-user", "An unknown user holds no access keys", KeysUnknownUserAsync, ct);
        await RunCheckAsync("keys.pagination", "Key pages end, repeat nothing, and add up to the same keys at any page size", KeysPaginationAsync, ct);
    }

    private Task PermissionsDeclaredAsync(CancellationToken ct)
    {
        _ = Documents;
        if (Permissions is null) throw new CheckSkippedException("Discovery declares no permissions: a content-only middle man.");
        if (Permissions["check"] is not JsonObject)
            throw new CheckFailedException("Discovery declares permissions without permissions.check, which every custom provider needs.");
        if (MaxBatch is not > 0)
            throw new CheckFailedException("permissions.check declares no positive max_batch.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A content-only middle man (no <c>permissions</c>) skips the permission checks. Declaring any permission
    /// capability makes it a custom provider, which must answer them: a missing check fails, never skips.
    /// </summary>
    private void RequireCheck()
    {
        _ = Documents;
        if (Permissions is null) throw new CheckSkippedException("Discovery declares no permissions: a content-only middle man.");
        if (Permissions["check"] is not JsonObject || MaxBatch is not > 0)
            throw new CheckFailedException("Discovery declares permissions without permissions.check and a positive max_batch.");
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

    /// <summary>One result as answered: its boolean decision, and whether it carried <c>context.error</c>.</summary>
    private sealed record Decision(bool Value, bool HasError)
    {
        /// <summary>What Connapse shows: an explicit true without context.error.</summary>
        public bool Allowed => Value && !HasError;
    }

    /// <summary>The results, refusing any that lacks a boolean decision: a malformed result is not a denial to pass.</summary>
    private async Task<List<Decision>> EvaluateAsync(string userId, IReadOnlyList<string> documentIds, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, EvaluationsPath, ct, EvaluationsBody(userId, documentIds));
        JsonObject body = await ExpectJsonAsync(response, HttpStatusCode.OK);
        List<JsonObject> results = Items(body, "evaluations");
        if (results.Count != documentIds.Count)
            throw new CheckFailedException($"A batch of {documentIds.Count} was answered with {results.Count} results.");
        return results.Select((r, i) => r["decision"]?.GetValueKind() switch
        {
            JsonValueKind.True => new Decision(true, r["context"]?["error"] is not null),
            JsonValueKind.False => new Decision(false, r["context"]?["error"] is not null),
            _ => throw new CheckFailedException($"Result {i} has no boolean decision."),
        }).ToList();
    }

    /// <summary>Requires an explicit <c>"decision": false</c> for each item, as the protocol asks of a denial.</summary>
    private static void ExpectDenied(List<Decision> decisions, string what)
    {
        if (decisions.Any(d => d.Allowed)) throw new CheckFailedException($"{what} was allowed.");
        if (decisions.Any(d => d.Value)) throw new CheckFailedException($"{what} was answered true (with context.error), not an explicit false.");
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
        ExpectDenied(await EvaluateAsync(UnknownUser(), SampleIds(10), ct), "A user the middle man cannot know");
    }

    private async Task CheckUnknownDocumentAsync(CancellationToken ct)
    {
        RequireCheck();
        string user = options.UserId ?? UnknownUser();
        ExpectDenied(await EvaluateAsync(user, ["connapse-conformance-" + Guid.NewGuid().ToString("N")], ct), "A document that does not exist");
    }

    private async Task CheckOrderAsync(CancellationToken ct)
    {
        RequireCheck();
        string reader = Reader;
        List<string> ids = SampleIds(10);
        string unknown = "connapse-conformance-" + Guid.NewGuid().ToString("N");
        ids.Insert(Math.Min(1, ids.Count), unknown);

        List<Decision> batch = await EvaluateAsync(reader, ids, ct);
        ExpectDenied([batch[ids.IndexOf(unknown)]], "A document that does not exist, in a batch,");
        if (!batch.Any(d => d.Allowed))
            throw new CheckFailedException($"The reader '{reader}' may read none of the first listed documents; give --user a user who can read some.");

        for (int i = 0; i < ids.Count; i++)
        {
            bool single = (await EvaluateAsync(reader, [ids[i]], ct))[0].Allowed;
            if (single != batch[i].Allowed)
                throw new CheckFailedException($"Document '{ids[i]}' was {(batch[i].Allowed ? "allowed" : "denied")} at position {i} of a batch but {(single ? "allowed" : "denied")} on its own: results are out of order.");
        }
    }

    private async Task CheckOverBatchAsync(CancellationToken ct)
    {
        RequireCheck();
        int max = MaxBatch!.Value;
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
