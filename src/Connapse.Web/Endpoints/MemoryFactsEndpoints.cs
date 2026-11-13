using System.Globalization;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Core;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Paged reads of a memory space: its facts, how many there are, its subjects, and a fact's history. Every list pages
/// by an opaque cursor that only the request that returned it accepts.
/// </summary>
public static class MemoryFactsEndpoints
{
    public static IEndpointRouteBuilder MapMemoryFactsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/memory").WithTags("Memory")
            .RequireAuthorization("RequireViewer")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/memory/spaces/{id}/facts - One page of a space's facts
        group.MapGet("/spaces/{id}/facts", async (
            HttpContext http,
            string id,
            [FromQuery] string? q,
            [FromQuery] string? subject,
            [FromQuery] string? author,
            [FromQuery] string? cites,
            [FromQuery] string? from,
            [FromQuery] string? to,
            [FromQuery] string? status,
            [FromQuery] string? sort,
            [FromQuery] string? cursor,
            [FromQuery] int? limit,
            [FromServices] ListMemoryFacts list,
            CancellationToken ct) =>
        {
            if (Filter(q, subject, author, cites, from, to, status) is not { } filter)
                return FilterProblem(author, cites, from, to, status);
            if (!TryEnum(sort, MemoryFactSort.Recorded, out MemoryFactSort order))
                return ApiProblem.BadRequest("sort must be \"recorded\", \"subject\", \"validFrom\" or \"relevance\".", field: "sort");

            var listed = await list.RunAsync(Rest(http), id, new MemoryFactListing(filter, order, cursor, limit), ct);
            return listed.Succeeded ? Results.Ok(listed.Result) : OperationProblems.Result(listed);
        })
        .Produces<MemoryFactsListed>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("memory.list")
        .WithDescription("One page of the facts in a memory space the caller can see, filtered and sorted, with the cursor for the next page");

        // GET /api/memory/spaces/{id}/facts/count - How many facts match
        group.MapGet("/spaces/{id}/facts/count", async (
            HttpContext http,
            string id,
            [FromQuery] string? q,
            [FromQuery] string? subject,
            [FromQuery] string? author,
            [FromQuery] string? cites,
            [FromQuery] string? from,
            [FromQuery] string? to,
            [FromQuery] string? status,
            [FromServices] CountMemoryFacts count,
            CancellationToken ct) =>
        {
            if (Filter(q, subject, author, cites, from, to, status) is not { } filter)
                return FilterProblem(author, cites, from, to, status);

            var counted = await count.RunAsync(Rest(http), id, filter, ct);
            return counted.Succeeded
                ? Results.Ok(new MemoryFactCountResponse(counted.Result!.Count, counted.Result.Capped))
                : OperationProblems.Result(counted);
        })
        .Produces<MemoryFactCountResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("memory.count")
        .WithDescription("How many facts in a memory space the caller can see, with the same filters as listing; capped when counting stopped early");

        // GET /api/memory/spaces/{id}/subjects - One page of a space's subjects
        group.MapGet("/spaces/{id}/subjects", async (
            HttpContext http,
            string id,
            [FromQuery] string? q,
            [FromQuery] string? status,
            [FromQuery] string? cursor,
            [FromQuery] int? limit,
            [FromServices] ListMemorySubjects subjects,
            CancellationToken ct) =>
        {
            if (!TryEnum(status, MemoryFactStatus.Current, out MemoryFactStatus which))
                return ApiProblem.BadRequest("status must be \"current\" or \"all\".", field: "status");

            var listed = await subjects.RunAsync(Rest(http), id, q, which, cursor, limit, ct);
            return listed.Succeeded ? Results.Ok(listed.Result) : OperationProblems.Result(listed);
        })
        .Produces<MemorySubjectsListed>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("memory.subjects")
        .WithDescription("One page of a memory space's subjects, A to Z, each with how many facts the caller can see under it");

        // GET /api/memory/facts/{id}/history - One page of a fact's versions
        group.MapGet("/facts/{id}/history", async (
            HttpContext http,
            string id,
            [FromQuery] string? cursor,
            [FromQuery] int? limit,
            [FromServices] ListMemoryHistory history,
            CancellationToken ct) =>
        {
            var listed = await history.RunByFactAsync(Rest(http), id, cursor, limit, ct);
            return listed.Succeeded ? Results.Ok(listed.Result) : OperationProblems.Result(listed);
        })
        .Produces<MemoryVersionsListed>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("memory.history")
        .WithDescription("One page of a fact's versions the caller can see, newest first, with how many there are in all");

        // GET /api/memory/facts - Recall facts, as the memory_recall tool does
        group.MapGet("/facts", async (
            HttpContext http,
            [FromQuery] string? space,
            [FromQuery] string? q,
            [FromQuery] string? subject,
            [FromQuery] string? attribute,
            [FromQuery] string? asOf,
            [FromQuery] bool? includeHistory,
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            [FromQuery] string? order,
            [FromServices] RecallMemory recall,
            CancellationToken ct) =>
        {
            if (!MemoryInput.TryDate(asOf, out DateTimeOffset? at))
                return ApiProblem.BadRequest("asOf must be an ISO 8601 date or time.", field: "asOf");
            if (MemoryInput.Order(order) is not { } sorted)
                return ApiProblem.BadRequest("order must be \"newest\" or left out.", field: "order");

            var recalled = await recall.RecallAsync(Rest(http),
                new MemoryRecallRequest(space, q, subject, attribute, at, includeHistory ?? false, limit ?? 10, cursor, sorted), ct);
            return recalled.Succeeded ? Results.Ok(RecallResponse(recalled.Result!)) : OperationProblems.Result(recalled);
        })
        .Produces<MemoryRecallResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("memory.recall")
        .WithDescription("Facts true now, or at asOf, in one space (by id or name) or in every space the caller can see. Reading one space without asOf or attribute pages: pass nextCursor back as cursor. order=newest puts the facts that took effect most recently first; it is ignored with q, which always returns the best matches first. Each fact is shown only when the caller can read every source it cites");

        // GET /api/memory/timeline - Facts in date order, as the memory_timeline tool reads them
        group.MapGet("/timeline", async (
            HttpContext http,
            [FromQuery] string? space,
            [FromQuery] string? q,
            [FromQuery] string? subject,
            [FromQuery] string? attribute,
            [FromQuery] string? from,
            [FromQuery] string? to,
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            [FromServices] RecallMemory recall,
            CancellationToken ct) =>
        {
            if (!MemoryInput.TryDate(from, out DateTimeOffset? since))
                return ApiProblem.BadRequest("from must be an ISO 8601 date or time.", field: "from");
            if (!MemoryInput.TryDate(to, out DateTimeOffset? until))
                return ApiProblem.BadRequest("to must be an ISO 8601 date or time.", field: "to");

            var read = await recall.TimelineAsync(Rest(http),
                new MemoryTimelineRequest(space, q, subject, attribute, since, until, limit ?? 50, cursor), ct);
            return read.Succeeded ? Results.Ok(RecallResponse(read.Result!)) : OperationProblems.Result(read);
        })
        .Produces<MemoryRecallResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("memory.timeline")
        .WithDescription("Facts in date order, ended and corrected ones included, in one space (by id or name) or in every space the caller can see. With from/to, only the facts that started or ended in that window. Reading one space without from, to or attribute pages newest first, each page in date order: pass nextCursor back as cursor. Each fact is shown only when the caller can read every source it cites");

        // Writes declare their own policy, so they don't also carry the reads' RequireViewer.
        var writes = app.MapGroup("/api/memory").WithTags("Memory")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // POST /api/memory/facts/{id}/copy - Copy a personal fact to a team space
        writes.MapPost("/facts/{id}/copy", async (
            HttpContext http,
            string id,
            [FromBody] CopyMemoryFactRequest request,
            [FromServices] CopyMemoryFact copy,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Destination))
                return ApiProblem.BadRequest("destination is required: a team space, by id or name.", field: "destination");

            var copied = await copy.RunAsync(Rest(http), id, request.Destination, request.Anyway, ct: ct);
            return copied.Succeeded
                ? Results.Ok(new MemoryCopyResponse(copied.Result!.Copy, copied.Result.CopiedFrom, copied.Result.SavedTo.Id))
                : OperationProblems.Result(copied);
        })
        .RequireAuthorization("RequireEditor", "Scope:KnowledgeWrite")
        .Produces<MemoryCopyResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("memory.copy")
        .WithDescription("Copies the current version of a fact in your own personal space to a team space, as a new fact you wrote; the personal fact is unchanged. Needs knowledge:read as well as knowledge:write");

        // POST /api/memory/spaces/{id}/facts - Remember a fact, as the memory_remember tool does
        writes.MapPost("/spaces/{id}/facts", async (
            HttpContext http,
            string id,
            [FromBody] RememberMemoryFactRequest request,
            [FromServices] RememberFact remember,
            CancellationToken ct) =>
        {
            (NewMemoryFact? fact, IResult? problem) = NewFact(request.Subject, request.Attribute, request.Value,
                request.ValidFrom, request.ValidTo, request.Sources, request.Reason, keepSourcesWhenNone: false);
            if (fact is null)
                return problem!;

            var written = await remember.RunAsync(Rest(http), MemoryTarget.Space(id), fact, ct);
            return written.Succeeded
                ? Results.Created($"/api/memory/facts/{written.Result!.Fact.Id}/history",
                    new MemoryRememberedResponse(written.Result.Fact, Space(written.Result.SavedTo!),
                        MemoryExistingFact.From(written.Result.Existing)))
                : OperationProblems.Result(written);
        })
        .RequireAuthorization("RequireEditor", "Scope:KnowledgeWrite")
        .Produces<MemoryRememberedResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("memory.remember")
        .WithDescription("Stores one fact in a memory space, by id or name. The space must exist: none is made by saving to it. A personal space's facts only you see; a team space's everyone sees, except that a fact citing documents is shown only to people who can read all of them. The save always adds a fact; when other current facts with the same subject and attribute that you can see are in the space, `existing` lists them (id, value, dates). If you meant to replace one, correct it instead of leaving both");

        // POST /api/memory/facts/{id}/correct - Replace a fact, keeping the old one in history
        writes.MapPost("/facts/{id}/correct", async (
            HttpContext http,
            string id,
            [FromBody] CorrectMemoryFactRequest request,
            [FromServices] CorrectFact correct,
            CancellationToken ct) =>
        {
            if (MemoryInput.Correction(request.Kind) is not { } kind)
                return ApiProblem.BadRequest("kind must be \"changed\" or \"wrong\".", field: "kind");
            (NewMemoryFact? fact, IResult? problem) = NewFact(request.Subject, request.Attribute, request.Value,
                request.ValidFrom, request.ValidTo, request.Sources, request.Reason, keepSourcesWhenNone: true);
            if (fact is null)
                return problem!;

            var written = await correct.RunAsync(Rest(http), null, id, kind, fact, ct);
            return written.Succeeded
                ? Results.Ok(new MemoryCorrectedResponse(written.Result!.Fact, written.Result.Replaced!))
                : OperationProblems.Result(written);
        })
        .RequireAuthorization("RequireEditor", "Scope:KnowledgeWrite")
        .Produces<MemoryCorrectedResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("memory.correct")
        .WithDescription("Replaces a fact and keeps the old one in history. kind \"changed\" (the default): the old fact ends at validFrom, which is required. kind \"wrong\": the old fact is marked superseded and the new value takes its dates. Subject, attribute and sources default to the old fact's. Needs knowledge:read as well as knowledge:write");

        // DELETE /api/memory/facts/{id} - Forget a fact and every version of it
        writes.MapDelete("/facts/{id}", async (
            HttpContext http,
            string id,
            [FromServices] ForgetFact forget,
            CancellationToken ct) =>
        {
            var forgotten = await forget.RunAsync(Rest(http), null, id, ct);
            return forgotten.Succeeded ? Results.Ok(new MemoryForgottenResponse(forgotten.Result)) : OperationProblems.Result(forgotten);
        })
        .RequireAuthorization("RequireEditor", "Scope:KnowledgeWrite")
        .Produces<MemoryForgottenResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("memory.forget")
        .WithDescription("Deletes a fact for good, with every earlier and later version of it from corrections, and says how many versions went. To record that a value changed, correct it instead");

        return app;
    }

    /// <summary>
    /// The fact a remember or correct body describes, read as the memory tools read theirs, or the problem with the
    /// first value that can't be read. <paramref name="keepSourcesWhenNone"/>: no sources given means "keep the old
    /// fact's" (a correction) rather than "none".
    /// </summary>
    internal static (NewMemoryFact? Fact, IResult? Problem) NewFact(string? subject, string? attribute, string? value,
        string? validFrom, string? validTo, string?[]? sources, string? reason, bool keepSourcesWhenNone)
    {
        if (!MemoryInput.TryDate(validFrom, out DateTimeOffset? from))
            return (null, ApiProblem.BadRequest("validFrom must be an ISO 8601 date or time.", field: "validFrom"));
        if (!MemoryInput.TryDate(validTo, out DateTimeOffset? to))
            return (null, ApiProblem.BadRequest("validTo must be an ISO 8601 date or time.", field: "validTo"));
        if (MemoryInput.Sources(sources) is not { } cited)
            return (null, ApiProblem.BadRequest("each source must be a document id (UUID) or \"node:<id>\".", field: "sources"));
        return (new NewMemoryFact(subject ?? "", attribute ?? "", value ?? "", from, to,
            keepSourcesWhenNone && sources is null ? null : cited, reason), null);
    }

    internal static MemoryRecallResponse RecallResponse(SpacedRecall recalled) => new(
        recalled.Facts,
        [.. recalled.Spaces.Values.Select(Space).OrderBy(s => s.Name, StringComparer.Ordinal)],
        recalled.SkippedSpaces,
        recalled.NextCursor);

    private static MemorySpaceRef Space(Container space) => new(space.Id, space.Name, space.Audience);

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);

    /// <summary>The filter the query names, or null when one of its values can't be read.</summary>
    private static MemoryFactFilter? Filter(
        string? q, string? subject, string? author, string? cites, string? from, string? to, string? status)
    {
        if (!TryGuid(author, out Guid? by) || !TryEnum(cites, MemoryCites.Any, out MemoryCites cited)
            || !TryDate(from, out DateTimeOffset? since) || !TryDate(to, out DateTimeOffset? until)
            || !TryEnum(status, MemoryFactStatus.Current, out MemoryFactStatus which))
            return null;
        return new MemoryFactFilter(q, subject, by, cited, since, until, which);
    }

    /// <summary>Which of the filter's values couldn't be read, as the first failing check in <see cref="Filter"/> finds it.</summary>
    private static IResult FilterProblem(string? author, string? cites, string? from, string? to, string? status)
    {
        if (!TryGuid(author, out _))
            return ApiProblem.BadRequest("author must be a user or agent id.", field: "author");
        if (!TryEnum(cites, MemoryCites.Any, out MemoryCites _))
            return ApiProblem.BadRequest("cites must be \"any\", \"some\" or \"none\".", field: "cites");
        if (!TryDate(from, out _))
            return ApiProblem.BadRequest("from must be an ISO 8601 date or time.", field: "from");
        if (!TryDate(to, out _))
            return ApiProblem.BadRequest("to must be an ISO 8601 date or time.", field: "to");
        return ApiProblem.BadRequest("status must be \"current\" or \"all\".", field: "status");
    }

    /// <summary>A named value of <typeparamref name="T"/> in any case, the fallback when blank; numbers are refused.</summary>
    private static bool TryEnum<T>(string? raw, T fallback, out T value) where T : struct, Enum
    {
        value = fallback;
        if (string.IsNullOrWhiteSpace(raw))
            return true;
        string trimmed = raw.Trim();
        return !char.IsAsciiDigit(trimmed[0]) && trimmed[0] != '-' && trimmed[0] != '+'
            && Enum.TryParse(trimmed, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    private static bool TryGuid(string? raw, out Guid? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;
        if (!Guid.TryParse(raw, out Guid parsed))
            return false;
        value = parsed;
        return true;
    }

    /// <summary>An ISO 8601 date or time; one without an offset is UTC.</summary>
    private static bool TryDate(string? raw, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            return false;
        value = parsed;
        return true;
    }
}

/// <summary>
/// Where to copy a fact: a team space by id or name. <paramref name="Anyway"/>: copy even when
/// the destination already has a current fact with the same subject and attribute.
/// </summary>
public sealed record CopyMemoryFactRequest(string? Destination, bool Anyway = false);

/// <summary>The new team fact, the personal version it was copied from, and the space it is in.</summary>
public sealed record MemoryCopyResponse(MemoryFact Fact, Guid CopiedFrom, string SpaceId);

/// <summary>How many facts the caller can see; <paramref name="Capped"/>: counting stopped early, so there are at least this many.</summary>
public sealed record MemoryFactCountResponse(long Count, bool Capped);

/// <summary>
/// A fact to remember: what it is about, which property, and its value; the dates it became and stopped being true
/// (ISO 8601, a bare date being midnight UTC); the document ids or "node:&lt;id&gt;" link nodes it was learned from;
/// and why.
/// </summary>
public sealed record RememberMemoryFactRequest(
    string? Subject, string? Attribute, string? Value,
    string? ValidFrom = null, string? ValidTo = null, string?[]? Sources = null, string? Reason = null);

/// <summary>
/// A correction: the new value, and whether the world <c>changed</c> (the default) or the old fact was <c>wrong</c>.
/// Left out, subject, attribute and sources stay the old fact's.
/// </summary>
public sealed record CorrectMemoryFactRequest(
    string? Value, string? Kind = null,
    string? ValidFrom = null, string? ValidTo = null, string?[]? Sources = null, string? Reason = null,
    string? Subject = null, string? Attribute = null);

/// <summary>A memory space a fact is in: its id, name, and whether it is personal (only its owner sees it) or team.</summary>
public sealed record MemorySpaceRef(string Id, string Name, MemoryScope Audience);

/// <summary>
/// The fact stored and the space it went into. <c>existing</c>, present only when there are some, lists other current
/// facts with the same subject and attribute that you can see: if you meant to replace one, correct it (CON-341).
/// </summary>
public sealed record MemoryRememberedResponse(
    MemoryFact Fact, MemorySpaceRef SavedTo,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<MemoryExistingFact>? Existing = null);

/// <summary>A current fact a save landed beside: its id, value, the dates it is true, and when it was recorded.</summary>
public sealed record MemoryExistingFact(Guid Id, string Value, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, DateTimeOffset RecordedAt)
{
    /// <summary>The listed facts, or null when there are none, so a save beside nothing answers as before.</summary>
    public static IReadOnlyList<MemoryExistingFact>? From(IReadOnlyList<MemoryFact>? facts) =>
        facts is { Count: > 0 } ? [.. facts.Select(f => new MemoryExistingFact(f.Id, f.Value, f.ValidFrom, f.ValidTo, f.RecordedAt))] : null;
}

/// <summary>The new version of the fact and the one it replaced, which history keeps.</summary>
public sealed record MemoryCorrectedResponse(MemoryFact Fact, MemoryFact Replaced);

/// <summary>How many versions of the fact were deleted.</summary>
public sealed record MemoryForgottenResponse(int Deleted);

/// <summary>
/// The facts recalled, newest or best first; the spaces they are in (match each fact's containerId); how many spaces
/// an unnamed read left out for being past its limit; and the cursor for the next page of a one-space read.
/// </summary>
public sealed record MemoryRecallResponse(
    IReadOnlyList<RecalledFact> Facts, IReadOnlyList<MemorySpaceRef> Spaces, int SkippedSpaces, string? NextCursor);
