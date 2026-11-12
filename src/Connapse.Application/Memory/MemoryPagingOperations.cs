using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Memory;

/// <summary>One page of a space's facts, and the cursor for the next page (null on the last).</summary>
public sealed record MemoryFactsListed(IReadOnlyList<RecalledFact> Facts, string? NextCursor);

/// <summary>One page of a space's subjects, A to Z, and the cursor for the next page (null on the last).</summary>
public sealed record MemorySubjectsListed(IReadOnlyList<MemorySubject> Subjects, string? NextCursor);

/// <summary>One page of a fact's versions, newest first, how many the caller can see in all, and the next cursor.</summary>
public sealed record MemoryVersionsListed(IReadOnlyList<RecalledFact> Versions, long Total, string? NextCursor);

/// <summary>What to list: which facts, in what order, after which cursor, and how many.</summary>
public sealed record MemoryFactListing(MemoryFactFilter Filter, MemoryFactSort Sort = MemoryFactSort.Recorded, string? Cursor = null, int? Limit = null);

/// <summary>
/// Opaque page cursors. A cursor is the last row's key with a fingerprint of the request that made it, so a cursor
/// carried over to a different space, filter or sort is refused rather than silently paging something else.
/// </summary>
internal static class MemoryCursor
{
    // A 200-character subject and attribute of control characters, escaped, still encode well under this; it is also
    // Kestrel's request-line limit, so no longer cursor could arrive.
    private const int MaxLength = 8192;

    // Text stays raw UTF-8 (the cursor is base64url, never markup), so a CJK subject costs 3 bytes a character, not 6.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record Envelope<T>(string F, T K);

    /// <summary>A short hash of the request's parts, in order.</summary>
    public static string Fingerprint(params object?[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts, Json)));
        return Base64Url.EncodeToString(hash.AsSpan(0, 12));
    }

    public static string Encode<T>(T key, string fingerprint) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Envelope<T>(fingerprint, key), Json));

    /// <summary>
    /// The key in <paramref name="cursor"/>: default with true for no cursor, the key with true for one this request
    /// made, and false for anything else (not a cursor, or one from another space, filter or sort).
    /// </summary>
    public static bool TryDecode<T>(string? cursor, string fingerprint, out T? key)
    {
        key = default;
        if (string.IsNullOrEmpty(cursor))
            return true;
        if (cursor.Length > MaxLength)
            return false;
        try
        {
            Envelope<T>? envelope = JsonSerializer.Deserialize<Envelope<T>>(Base64Url.DecodeFromChars(cursor), Json);
            if (envelope is null || envelope.K is null || !string.Equals(envelope.F, fingerprint, StringComparison.Ordinal))
                return false;
            key = envelope.K;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}

internal static class MemoryPaging
{
    public const int DefaultLimit = 50;
    public const int DefaultHistoryLimit = 25;
    public const int MaxLimit = 100;

    public const string BadCursor = "cursor is not one this request returned. Start again without it.";

    public static Attempt<T, MemoryStatus> Unchecked<T>() =>
        MemoryRules.Fail<T>(MemoryStatus.Unchecked, "access to memory sources could not be checked; nothing is shown.");

    /// <summary>The limit asked for, the default when none, or null when out of range.</summary>
    public static int? Limit(int? asked, int fallback) => asked switch
    {
        null => fallback,
        >= 1 and <= MaxLimit => asked,
        _ => null,
    };

    /// <summary>Why a filter is refused, with the field at fault, or null when it is fine.</summary>
    public static (string Detail, string Field)? Refusal(MemoryFactFilter filter)
    {
        if (!Enum.IsDefined(filter.Cites))
            return ("cites must be \"any\", \"some\" or \"none\".", "cites");
        if (!Enum.IsDefined(filter.Status))
            return ("status must be \"current\" or \"all\".", "status");
        if (filter.From is { } from && filter.To is { } to && from > to)
            return ("from must not be after to.", "from");
        if (!Enum.IsDefined(filter.Check))
            return ("check must be \"due\" or \"fresh\".", "check");
        return null;
    }

    /// <summary>The filter's fingerprint parts, the way the store reads them.</summary>
    public static object?[] Parts(Guid space, MemoryFactFilter filter) =>
    [
        space, filter.Text?.Trim(), filter.Subject?.Trim().ToLowerInvariant(), filter.Author, filter.Cites,
        filter.From?.UtcTicks, filter.To?.UtcTicks, filter.Status, filter.Check,
    ];

    /// <summary>The space named, or the failure: forbidden without read access, invalid when refused, else not found.</summary>
    public static async Task<(Guid Space, MemoryCaller Caller, Attempt<T, MemoryStatus>? Failed)> SpaceAsync<T>(
        IContainerStore containers, IOperationAuthorizer authorizer, OperationContext context, string space, CancellationToken ct)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return (default, default!, MemoryRules.Fail<T>(MemoryStatus.Forbidden, "reading memory needs knowledge:read."));
        MemoryCaller caller = MemoryRules.Caller(context.User);
        (Guid? found, string? refused) = await MemoryRules.ContainerAsync(containers, space, caller, ct);
        if (refused is not null)
            return (default, caller, MemoryRules.Fail<T>(MemoryStatus.Invalid, refused, "space"));
        return found is { } id
            ? (id, caller, null)
            : (default, caller, MemoryRules.Fail<T>(MemoryStatus.ContainerNotFound, $"Space '{space}' not found."));
    }
}

/// <summary>
/// Pages through a space's facts, a page at a time, showing each fact the caller can see exactly once and none it
/// cannot. Filters by text, subject, author, whether it cites anything, when it was recorded, and current or every
/// version; sorts newest first (the default), by subject, by when it became true, or by best match to the text.
/// </summary>
[Operation("memory.list", Scope = MemoryRules.ReadScope, Risk = RiskTier.Read)]
public sealed class ListMemoryFacts(IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<MemoryFactsListed, MemoryStatus>> RunAsync(
        OperationContext context, string space, MemoryFactListing listing, CancellationToken ct = default)
    {
        (Guid id, MemoryCaller caller, Attempt<MemoryFactsListed, MemoryStatus>? failed) =
            await MemoryPaging.SpaceAsync<MemoryFactsListed>(containers, authorizer, context, space, ct);
        if (failed is not null)
            return failed;
        if (MemoryPaging.Refusal(listing.Filter) is { } refusal)
            return MemoryRules.Fail<MemoryFactsListed>(MemoryStatus.Invalid, refusal.Detail, refusal.Field);
        if (!Enum.IsDefined(listing.Sort))
            return MemoryRules.Fail<MemoryFactsListed>(MemoryStatus.Invalid, "sort must be \"recorded\", \"subject\", \"validFrom\" or \"relevance\".", "sort");
        if (listing.Sort == MemoryFactSort.Relevance && string.IsNullOrWhiteSpace(listing.Filter.Text))
            return MemoryRules.Fail<MemoryFactsListed>(MemoryStatus.Invalid, "sort \"relevance\" needs search text (q).", "sort");
        if (MemoryPaging.Limit(listing.Limit, MemoryPaging.DefaultLimit) is not { } limit)
            return MemoryRules.Fail<MemoryFactsListed>(MemoryStatus.Invalid, $"limit must be 1 to {MemoryPaging.MaxLimit}.", "limit");

        string fingerprint = MemoryCursor.Fingerprint([.. MemoryPaging.Parts(id, listing.Filter), listing.Sort]);
        if (!MemoryCursor.TryDecode(listing.Cursor, fingerprint, out MemoryFactKey? after))
            return MemoryRules.Fail<MemoryFactsListed>(MemoryStatus.Invalid, MemoryPaging.BadCursor, "cursor");

        MemoryFactPage page = await memory.ListAsync(id, listing.Filter, listing.Sort, after, limit, caller, ct);
        return page.Denied
            ? MemoryPaging.Unchecked<MemoryFactsListed>()
            : Attempt<MemoryFactsListed, MemoryStatus>.Succeed(new MemoryFactsListed(
                page.Facts, page.Next is { } next ? MemoryCursor.Encode(next, fingerprint) : null));
    }
}

/// <summary>
/// How many of a space's facts the caller can see, with the same filters as listing. Hidden facts never count. A
/// count that would run past two seconds answers with what it has counted, marked capped; so does a search count past
/// the best 1,000 matches a list shows.
/// </summary>
[Operation("memory.count", Scope = MemoryRules.ReadScope, Risk = RiskTier.Read)]
public sealed class CountMemoryFacts(IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<MemoryFactCount, MemoryStatus>> RunAsync(
        OperationContext context, string space, MemoryFactFilter filter, CancellationToken ct = default)
    {
        (Guid id, MemoryCaller caller, Attempt<MemoryFactCount, MemoryStatus>? failed) =
            await MemoryPaging.SpaceAsync<MemoryFactCount>(containers, authorizer, context, space, ct);
        if (failed is not null)
            return failed;
        if (MemoryPaging.Refusal(filter) is { } refusal)
            return MemoryRules.Fail<MemoryFactCount>(MemoryStatus.Invalid, refusal.Detail, refusal.Field);

        MemoryFactCount count = await memory.CountAsync(id, filter, caller, ct);
        return count.Denied ? MemoryPaging.Unchecked<MemoryFactCount>() : Attempt<MemoryFactCount, MemoryStatus>.Succeed(count);
    }
}

/// <summary>
/// Pages through the subjects of a space's facts, A to Z, each with how many facts the caller can see under it.
/// Subjects that differ only in case are one subject; a subject with no visible fact is not listed.
/// </summary>
[Operation("memory.subjects", Scope = MemoryRules.ReadScope, Risk = RiskTier.Read)]
public sealed class ListMemorySubjects(IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<MemorySubjectsListed, MemoryStatus>> RunAsync(
        OperationContext context, string space, string? text, MemoryFactStatus status = MemoryFactStatus.Current,
        string? cursor = null, int? limit = null, CancellationToken ct = default)
    {
        (Guid id, MemoryCaller caller, Attempt<MemorySubjectsListed, MemoryStatus>? failed) =
            await MemoryPaging.SpaceAsync<MemorySubjectsListed>(containers, authorizer, context, space, ct);
        if (failed is not null)
            return failed;
        if (!Enum.IsDefined(status))
            return MemoryRules.Fail<MemorySubjectsListed>(MemoryStatus.Invalid, "status must be \"current\" or \"all\".", "status");
        if (MemoryPaging.Limit(limit, MemoryPaging.DefaultLimit) is not { } take)
            return MemoryRules.Fail<MemorySubjectsListed>(MemoryStatus.Invalid, $"limit must be 1 to {MemoryPaging.MaxLimit}.", "limit");

        string fingerprint = MemoryCursor.Fingerprint(id, text?.Trim(), status);
        if (!MemoryCursor.TryDecode(cursor, fingerprint, out string? after))
            return MemoryRules.Fail<MemorySubjectsListed>(MemoryStatus.Invalid, MemoryPaging.BadCursor, "cursor");

        MemorySubjectPage page = await memory.SubjectsAsync(id, text, status, after, take, caller, ct);
        return page.Denied
            ? MemoryPaging.Unchecked<MemorySubjectsListed>()
            : Attempt<MemorySubjectsListed, MemoryStatus>.Succeed(new MemorySubjectsListed(
                page.Subjects, page.Next is { } next ? MemoryCursor.Encode(next, fingerprint) : null));
    }
}

/// <summary>
/// Pages through every version of a fact the caller can see, newest first, with the exact number of them. A fact the
/// caller cannot see reads as missing.
/// </summary>
[Operation("memory.history", Scope = MemoryRules.ReadScope, Risk = RiskTier.Read)]
public sealed class ListMemoryHistory(IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<MemoryVersionsListed, MemoryStatus>> RunAsync(
        OperationContext context, string space, string factId, string? cursor = null, int? limit = null, CancellationToken ct = default)
    {
        (Guid id, MemoryCaller caller, Attempt<MemoryVersionsListed, MemoryStatus>? failed) =
            await MemoryPaging.SpaceAsync<MemoryVersionsListed>(containers, authorizer, context, space, ct);
        if (failed is not null)
            return failed;
        if (!Guid.TryParse(factId, out Guid fact))
            return MemoryRules.FactNotFound<MemoryVersionsListed>(factId);
        if (MemoryPaging.Limit(limit, MemoryPaging.DefaultHistoryLimit) is not { } take)
            return MemoryRules.Fail<MemoryVersionsListed>(MemoryStatus.Invalid, $"limit must be 1 to {MemoryPaging.MaxLimit}.", "limit");

        string fingerprint = MemoryCursor.Fingerprint(id, fact);
        if (!MemoryCursor.TryDecode(cursor, fingerprint, out MemoryVersionKey? after))
            return MemoryRules.Fail<MemoryVersionsListed>(MemoryStatus.Invalid, MemoryPaging.BadCursor, "cursor");

        MemoryHistoryPage page = await memory.HistoryAsync(id, fact, after, take, caller, ct);
        if (page.Denied)
            return MemoryPaging.Unchecked<MemoryVersionsListed>();
        return page.NotFound
            ? MemoryRules.FactNotFound<MemoryVersionsListed>(factId)
            : Attempt<MemoryVersionsListed, MemoryStatus>.Succeed(new MemoryVersionsListed(
                page.Versions, page.Total, page.Next is { } next ? MemoryCursor.Encode(next, fingerprint) : null));
    }

    /// <summary>
    /// As <see cref="RunAsync"/>, finding the space from the fact. A fact in a space the caller can't open reads as
    /// missing, the same as one that isn't there, so a fact id can't be used to learn that a hidden space holds it.
    /// </summary>
    public async Task<Attempt<MemoryVersionsListed, MemoryStatus>> RunByFactAsync(
        OperationContext context, string factId, string? cursor = null, int? limit = null, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return MemoryRules.Fail<MemoryVersionsListed>(MemoryStatus.Forbidden, "reading memory needs knowledge:read.");
        if (!Guid.TryParse(factId, out Guid fact) || await memory.SpaceOfAsync(fact, ct) is not { } space)
            return MemoryRules.FactNotFound<MemoryVersionsListed>(factId);
        Attempt<MemoryVersionsListed, MemoryStatus> read = await RunAsync(context, space.ToString(), factId, cursor, limit, ct);
        return read.Status == MemoryStatus.ContainerNotFound ? MemoryRules.FactNotFound<MemoryVersionsListed>(factId) : read;
    }

    /// <summary>
    /// As <see cref="RunAsync"/>, reading page after page until one holds <paramref name="versionId"/> or the history
    /// ends, so a version deep in a long chain can be opened. The versions read are joined newest first, and the cursor
    /// continues past them. The first page that fails fails the whole read.
    /// </summary>
    public async Task<Attempt<MemoryVersionsListed, MemoryStatus>> BackToAsync(
        OperationContext context, string space, string factId, Guid versionId, int? limit = null, CancellationToken ct = default)
    {
        List<RecalledFact> versions = [];
        Attempt<MemoryVersionsListed, MemoryStatus> read;
        string? cursor = null;
        do
        {
            read = await RunAsync(context, space, factId, cursor, limit, ct);
            if (!read.Succeeded)
                return read;
            versions.AddRange(read.Result!.Versions);
            cursor = read.Result.NextCursor;
        }
        while (cursor is not null && versions.TrueForAll(v => v.Fact.Id != versionId));
        return Attempt<MemoryVersionsListed, MemoryStatus>.Succeed(read.Result with { Versions = versions });
    }
}
