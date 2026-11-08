using Connapse.Application;
using Connapse.Application.Code;
using Connapse.Core;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Code questions over a container or source (CON-284), as the code tools ask them: where a symbol is defined, who
/// uses it, what a change to it reaches, and which files matter most. Each route runs the same operation as its MCP
/// tool and answers with the same JSON, so a script and an agent acting as the same user see the same files.
/// </summary>
public static class CodeEndpoints
{
    public static IEndpointRouteBuilder MapCodeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/code").WithTags("Code")
            .RequireAuthorization("RequireViewer")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /api/code/symbols - Where a symbol is defined, as the code_find_symbol tool answers
        group.MapGet("/symbols", async (
            HttpContext http,
            [FromQuery] string? containerId,
            [FromQuery] string? query,
            [FromQuery] string? match,
            [FromQuery] bool? follow,
            [FromQuery] int? limit,
            [FromServices] FindCodeSymbol find,
            CancellationToken ct) =>
        {
            if (!CodeAnswers.TryMatch(match, out CodeMatch mode))
                return ApiProblem.BadRequest(CodeAnswers.MatchError, field: "match");
            bool following = follow ?? false;
            var found = await find.RunAsync(Rest(http), containerId ?? "", query ?? "", mode, following, limit ?? 10, ct);
            return found.Succeeded ? Results.Ok(CodeAnswers.Symbol(found.Result!, following)) : OperationProblems.Result(found);
        })
        .Produces<CodeSymbolAnswer>()
        .WithName("code.findSymbol")
        .WithDescription("Where a function, method, class or other symbol is defined in indexed code (C#, Python, TypeScript), in the container or source named by containerId (an id or a name). match=exact (default) takes a name as far as you know it (Post, Ledger.Post); match=words takes words that all appear in the qualified name; match=meaning takes a description. With follow=true, also what the first definition calls, constructs or inherits, and the files defining those. limit caps the definitions (1-100, default 10). Only files the caller can read are shown");

        // GET /api/code/references - The files that use a symbol, as the code_references tool answers
        group.MapGet("/references", async (
            HttpContext http,
            [FromQuery] string? containerId,
            [FromQuery] string? symbol,
            [FromQuery] string? kind,
            [FromQuery] int? limit,
            [FromServices] FindCodeReferences references,
            CancellationToken ct) =>
        {
            if (!CodeAnswers.TryKind(kind, out CodeReferenceKind which))
                return ApiProblem.BadRequest(CodeAnswers.KindError, field: "kind");
            var found = await references.RunAsync(Rest(http), containerId ?? "", symbol ?? "", which, limit ?? 20, ct);
            return found.Succeeded ? Results.Ok(CodeAnswers.References(found.Result!)) : OperationProblems.Result(found);
        })
        .Produces<CodeReferencesAnswer>()
        .WithName("code.references")
        .WithDescription("The files that use a symbol, best evidence first: files the code index links to it (a call, a base type, an import) with the lines, then files whose text names it. kind=calls for callers only, inherits for subtypes only, all (default) for every use and mention. limit caps the files (1-200, default 20). Only files the caller can read are shown");

        // GET /api/code/impact - What a change to a symbol could affect, as the code_impact tool answers
        group.MapGet("/impact", async (
            HttpContext http,
            [FromQuery] string? containerId,
            [FromQuery] string? symbol,
            [FromQuery] int? depth,
            [FromQuery] int? limit,
            [FromServices] FindCodeImpact impact,
            CancellationToken ct) =>
        {
            var found = await impact.RunAsync(Rest(http), containerId ?? "", symbol ?? "", depth ?? 2, limit ?? 30, ct);
            return found.Succeeded ? Results.Ok(CodeAnswers.Impact(found.Result!)) : OperationProblems.Result(found);
        })
        .Produces<CodeImpactAnswer>()
        .WithName("code.impact")
        .WithDescription("What a change to a symbol could affect: the functions and types that call or inherit it, directly or through others, up to depth hops (1-3, default 2), with the files that hold them and which of those are tests. limit caps the symbols and files (1-200, default 30). Only links the code index resolved are followed; use code references for every mention. Only files the caller can read are shown");

        // GET /api/code/map - The files the rest of the code refers to most, as the code_repo_map tool answers
        group.MapGet("/map", async (
            HttpContext http,
            [FromQuery] string? containerId,
            [FromQuery] string? path,
            [FromQuery] int? maxFiles,
            [FromServices] MapCodeRepository mapper,
            CancellationToken ct) =>
        {
            var mapped = await mapper.RunAsync(Rest(http), containerId ?? "", path, maxFiles ?? 30, ct);
            return mapped.Succeeded ? Results.Ok(CodeAnswers.Map(mapped.Result!)) : OperationProblems.Result(mapped);
        })
        .Produces<CodeMapAnswer>()
        .WithName("code.repoMap")
        .WithDescription("A map of indexed code: the files the rest of the code refers to most, each with its main definitions and how many other files use each. path maps one folder; maxFiles caps the files (1-200, default 30). Only files the caller can read are shown");

        return app;
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);
}
