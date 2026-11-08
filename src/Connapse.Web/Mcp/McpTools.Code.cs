using System.ComponentModel;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Code;
using Connapse.Core;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace Connapse.Web.Mcp;

/// <summary>
/// Code questions over a container or source (CON-37): where a symbol is defined, who uses it, what a change to it
/// reaches, and which files matter most. Every file shown is one the caller can read. Answers are JSON data under a
/// fixed notice, never prose an agent could take as instructions.
/// </summary>
public partial class McpTools
{
    internal const string CodeNotice = CodeAnswers.Notice;

    internal const string CodeDeniedError = "Error: access to the files could not be checked; nothing is shown.";

    [McpServerTool(Name = "code_find_symbol", ReadOnly = true, Idempotent = true),
     Authorize(Policy = "Scope:KnowledgeRead"), McpToolRisk(RiskTier.Read),
     Description("Find where a function, method, class or other symbol is defined in indexed code (C#, Python, TypeScript). match=\"exact\" (default) takes a name as far as you know it: \"Post\", \"Ledger.Post\" or \"Acme.Billing.Ledger.Post\". match=\"words\" takes words that all appear in the qualified name (\"ledger post\"). match=\"meaning\" takes a description (\"where invoices are written to the ledger\"). With follow=true, also returns what the first definition calls, constructs or inherits and the files defining those: the files to read to see how it works. Returns definitions with path, start and end line and signature, and ranked files.")]
    public static async Task<string> CodeFindSymbol(
        FindCodeSymbol find,
        IHttpContextAccessor http,
        [Description("Container or source ID or name")] string containerId,
        [Description("The symbol name, words, or a description, per `match`")] string query,
        [Description("\"exact\" (default), \"words\" or \"meaning\"")] string? match = null,
        [Description("Also follow what the definition calls into, and rank those files")] bool follow = false,
        [Description("At most this many definitions (1-100). Default 10")] int limit = 10,
        CancellationToken ct = default)
    {
        if (!CodeAnswers.TryMatch(match, out CodeMatch mode))
            return $"Error: {CodeAnswers.MatchError}";
        var found = await find.RunAsync(Mcp(http), containerId, query, mode, follow, limit, ct);
        if (!found.Succeeded)
            return Refused(found);
        return JsonSerializer.Serialize(CodeAnswers.Symbol(found.Result!, follow), MemoryJson);
    }

    [McpServerTool(Name = "code_references", ReadOnly = true, Idempotent = true),
     Authorize(Policy = "Scope:KnowledgeRead"), McpToolRisk(RiskTier.Read),
     Description("Find the files that use a symbol, best evidence first: files the code index links to it (a call, a base type, an import) with the lines, then files whose text names it. kind=\"calls\" for callers only, \"inherits\" for subtypes only, \"all\" (default) for every use and mention. Name the symbol as for code_find_symbol (\"Ledger.Post\"); qualify it further when the name is common.")]
    public static async Task<string> CodeReferences(
        FindCodeReferences references,
        IHttpContextAccessor http,
        [Description("Container or source ID or name")] string containerId,
        [Description("The symbol, e.g. \"Ledger.Post\"")] string symbol,
        [Description("\"all\" (default), \"calls\" or \"inherits\"")] string? kind = null,
        [Description("At most this many files (1-200). Default 20")] int limit = 20,
        CancellationToken ct = default)
    {
        if (!CodeAnswers.TryKind(kind, out CodeReferenceKind which))
            return $"Error: {CodeAnswers.KindError}";
        var found = await references.RunAsync(Mcp(http), containerId, symbol, which, limit, ct);
        if (!found.Succeeded)
            return Refused(found);
        return JsonSerializer.Serialize(CodeAnswers.References(found.Result!), MemoryJson);
    }

    [McpServerTool(Name = "code_impact", ReadOnly = true, Idempotent = true),
     Authorize(Policy = "Scope:KnowledgeRead"), McpToolRisk(RiskTier.Read),
     Description("What a change to a symbol could affect: the functions and types that call or inherit it, directly or through others, up to `depth` hops, with the files that hold them and which of those are tests. For a type, what uses its members counts too. Only links the code index resolved are followed; a call it could not resolve is missed, so check code_references for the full list of mentions.")]
    public static async Task<string> CodeImpact(
        FindCodeImpact impact,
        IHttpContextAccessor http,
        [Description("Container or source ID or name")] string containerId,
        [Description("The symbol, e.g. \"Ledger.Post\"")] string symbol,
        [Description("Hops to follow (1-3). Default 2")] int depth = 2,
        [Description("At most this many symbols and files (1-200). Default 30")] int limit = 30,
        CancellationToken ct = default)
    {
        var found = await impact.RunAsync(Mcp(http), containerId, symbol, depth, limit, ct);
        if (!found.Succeeded)
            return Refused(found);
        return JsonSerializer.Serialize(CodeAnswers.Impact(found.Result!), MemoryJson);
    }

    [McpServerTool(Name = "code_repo_map", ReadOnly = true, Idempotent = true),
     Authorize(Policy = "Scope:KnowledgeRead"), McpToolRisk(RiskTier.Read),
     Description("A map of indexed code: the files the rest of the code refers to most, each with its main definitions and how many other files use each. Start here in an unfamiliar repository, or pass `path` to map one folder.")]
    public static async Task<string> CodeRepoMap(
        MapCodeRepository mapper,
        IHttpContextAccessor http,
        [Description("Container or source ID or name")] string containerId,
        [Description("Only files under this folder, e.g. \"src/billing\"")] string? path = null,
        [Description("At most this many files (1-200). Default 30")] int maxFiles = 30,
        CancellationToken ct = default)
    {
        var mapped = await mapper.RunAsync(Mcp(http), containerId, path, maxFiles, ct);
        if (!mapped.Succeeded)
            return Refused(mapped);
        return JsonSerializer.Serialize(CodeAnswers.Map(mapped.Result!), MemoryJson);
    }
}
