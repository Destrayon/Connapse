using Connapse.Application.Ports;
using Connapse.Application.Search;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Application.Code;

/// <summary>Why a code question wasn't answered.</summary>
public enum CodeStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "search-scope-not-found", "Container or source not found")]
    NotFound,

    [Failure(400, "invalid-code-query", "Invalid code query")]
    Invalid,

    [Failure(503, "code-access-unchecked", "Access could not be checked")]
    Unchecked,
}

/// <summary>
/// Asks a code question of the container or source search would search for the caller, named by id or name. A source
/// the caller may not see is asked about only when search checks its every hit for them, and then an empty answer
/// reads as missing, as search's does, so a code question can't confirm that a hidden source exists.
/// </summary>
internal static class CodeAsker
{
    public static async Task<Attempt<T, CodeStatus>> AskAsync<T>(
        SearchKnowledge search, IOperationAuthorizer authorizer, OperationContext context, string? containerId, string? refusal,
        Func<CodeScope, Task<T>> ask, Func<T, bool> denied, Func<T, bool> empty, CancellationToken ct)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.KnowledgeRead))
            return Attempt<T, CodeStatus>.Fail(CodeStatus.Forbidden, "You don't have permission to search.");
        if (refusal is not null)
            return Attempt<T, CodeStatus>.Fail(CodeStatus.Invalid, refusal);
        if (string.IsNullOrWhiteSpace(containerId)
            || await search.ResolveAsync(context, new SearchScope(containerId.Trim()), ct) is not { } found)
            return NotFound<T>(containerId);

        T answer = await ask(new CodeScope(found.Id, SearchPrincipal.Resolve(context.User)));
        if (denied(answer))
            return Attempt<T, CodeStatus>.Fail(CodeStatus.Unchecked, "access to the files could not be checked; nothing is shown.");
        return !found.Discoverable && empty(answer) ? NotFound<T>(containerId) : Attempt<T, CodeStatus>.Succeed(answer);
    }

    private static Attempt<T, CodeStatus> NotFound<T>(string? containerId) =>
        Attempt<T, CodeStatus>.Fail(CodeStatus.NotFound, $"Container '{containerId}' not found.");
}

/// <summary>
/// Where a symbol is defined, by name, by words of its qualified name, or by meaning; with <c>follow</c>, also what
/// the first definition calls into and the files defining that.
/// </summary>
[Operation("code.findSymbol", Scope = SearchKnowledge.Scope, Risk = RiskTier.Read)]
public sealed class FindCodeSymbol(SearchKnowledge search, IOperationAuthorizer authorizer, ICodeTools code)
{
    public Task<Attempt<CodeSymbolResult, CodeStatus>> RunAsync(
        OperationContext context, string containerId, string query, CodeMatch match, bool follow, int limit, CancellationToken ct = default) =>
        CodeAsker.AskAsync(search, authorizer, context, containerId,
            string.IsNullOrWhiteSpace(query) ? "query is required."
            : !Enum.IsDefined(match) ? "match must be \"exact\", \"words\" or \"meaning\"."
            : null,
            scope => code.FindSymbolAsync(scope, query, match, follow, limit, ct),
            r => r.Denied, r => r.Definitions.Count == 0 && r.Files.Count == 0, ct);
}

/// <summary>The files that use a symbol, best evidence first.</summary>
[Operation("code.references", Scope = SearchKnowledge.Scope, Risk = RiskTier.Read)]
public sealed class FindCodeReferences(SearchKnowledge search, IOperationAuthorizer authorizer, ICodeTools code)
{
    public Task<Attempt<CodeReferencesResult, CodeStatus>> RunAsync(
        OperationContext context, string containerId, string symbol, CodeReferenceKind kind, int limit, CancellationToken ct = default) =>
        CodeAsker.AskAsync(search, authorizer, context, containerId,
            string.IsNullOrWhiteSpace(symbol) ? "symbol is required."
            : !Enum.IsDefined(kind) ? "kind must be \"all\", \"calls\" or \"inherits\"."
            : null,
            scope => code.ReferencesAsync(scope, symbol, kind, limit, ct),
            r => r.Denied, r => r.Definitions.Count == 0 && r.Files.Count == 0, ct);
}

/// <summary>What a change to a symbol could affect, up to a few hops through calls and bases.</summary>
[Operation("code.impact", Scope = SearchKnowledge.Scope, Risk = RiskTier.Read)]
public sealed class FindCodeImpact(SearchKnowledge search, IOperationAuthorizer authorizer, ICodeTools code)
{
    public Task<Attempt<CodeImpactResult, CodeStatus>> RunAsync(
        OperationContext context, string containerId, string symbol, int depth, int limit, CancellationToken ct = default) =>
        CodeAsker.AskAsync(search, authorizer, context, containerId,
            string.IsNullOrWhiteSpace(symbol) ? "symbol is required." : null,
            scope => code.ImpactAsync(scope, symbol, depth, limit, ct),
            r => r.Denied, r => r.Definitions.Count == 0 && r.Symbols.Count == 0 && r.Files.Count == 0, ct);
}

/// <summary>The files the rest of the code refers to most, with their main definitions.</summary>
[Operation("code.repoMap", Scope = SearchKnowledge.Scope, Risk = RiskTier.Read)]
public sealed class MapCodeRepository(SearchKnowledge search, IOperationAuthorizer authorizer, ICodeTools code)
{
    public Task<Attempt<CodeRepoMap, CodeStatus>> RunAsync(
        OperationContext context, string containerId, string? path, int maxFiles, CancellationToken ct = default) =>
        CodeAsker.AskAsync(search, authorizer, context, containerId, null,
            scope => code.RepoMapAsync(scope, path, maxFiles, ct),
            r => r.Denied, r => r.Files.Count == 0, ct);
}
