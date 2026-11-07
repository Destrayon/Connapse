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

        return app;
    }

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
