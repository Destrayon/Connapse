using System.Collections.Frozen;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Connapse.Web.Mcp;

/// <summary>
/// Which tools <c>tools/list</c> shows (CON-346). The compact list shows the four question-answering tools plus
/// <c>find_tools</c> and the call tools; every other tool is found by search. A
/// connection to <c>/mcp/full</c> shows every tool directly and hides the search and call tools instead.
/// </summary>
public static class McpToolList
{
    public const string FullPath = "/mcp/full";

    /// <summary>The tools the compact list shows besides the search and call tools.</summary>
    public static readonly FrozenSet<string> Core = new[] { "container_list", "search_knowledge", "list_files", "get_document" }
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The server instructions for the compact list: the routing rules, then every hidden tool by area, so an agent
    /// knows what it can find before it searches (CON-343 section 4.1; a unit test checks every tool is named).
    /// </summary>
    public const string CompactInstructions = McpServerConfig.McpServerInstructions
        + """


        More tools are available through `find_tools`; run each with the call tool it names. Search for what you
        want to do, or for a tool's exact name. Prefer a fitting tool here to search_knowledge; for where a symbol is
        defined or used, use the Code tools.

        - Containers: container_create, container_delete, container_stats, container_describe
        - Files: upload_file, bulk_upload, append_to_file, get_documents, delete_file, bulk_delete
        - Code: code_find_symbol, code_references, code_impact, code_repo_map
        - Memory: memory_spaces, memory_space_create, memory_remember, memory_correct, memory_forget, memory_recall, memory_timeline
        - Approvals: approval_status
        - Admin API (operation ids): users, agents and their keys, settings, connections, providers, sources, folders, audit
        """;

    /// <summary>Whether this request gets every tool directly rather than the compact list.</summary>
    public static bool IsFull(HttpContext? http) =>
        http is not null && http.Request.Path.StartsWithSegments(FullPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a tool appears in <c>tools/list</c> for the given list.</summary>
    public static bool Shows(string name, bool full) =>
        full ? !McpToolSearch.Names.Contains(name) : Core.Contains(name) || McpToolSearch.Names.Contains(name);

    /// <summary>Sets the instructions for the list this session gets.</summary>
    public static Task ConfigureSession(HttpContext http, McpServerOptions options, CancellationToken cancellationToken)
    {
        if (!IsFull(http))
            options.ServerInstructions = CompactInstructions;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Narrows <c>tools/list</c> to the current list and to what the caller may use. The SDK's authorization filter
/// already drops each tool whose scope the caller lacks; this drops <c>find_tools</c> and the call tools when
/// nothing behind them is usable. Hidden tools can still be called by name, and each still checks its caller.
/// </summary>
public sealed class McpToolListFilter(IHttpContextAccessor http, McpToolSearch search)
{
    public async Task<ListToolsResult> ApplyAsync(RequestContext<ListToolsRequestParams> context, ListToolsResult result)
    {
        bool full = McpToolList.IsFull(http.HttpContext);
        var shown = result.Tools.Where(t => McpToolList.Shows(t.Name, full)).ToList();
        // An anonymous registry reads every schema (Mcp:AllowAnonymousDiscovery), the search tools included.
        if (!full && context.User?.Identity?.IsAuthenticated == true)
        {
            var listed = await search.ListedSearchToolsAsync(context.Server, context.User, context);
            shown.RemoveAll(t => McpToolSearch.Names.Contains(t.Name) && !listed.Contains(t.Name));
        }
        result.Tools = shown;
        return result;
    }
}
