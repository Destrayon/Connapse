using System.Collections.Frozen;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Connapse.Web.Mcp;

/// <summary>
/// Which tools <c>tools/list</c> shows (CON-346). The compact list shows the four question-answering tools plus
/// <c>find_tools</c> and the call tools; every other tool is found by search. <c>Mcp:ToolList = full</c>, or a
/// connection to <c>/mcp/full</c>, shows every tool directly and hides the search and call tools instead.
/// </summary>
public static class McpToolList
{
    public const string SettingKey = "Mcp:ToolList";
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
        want to do, or for a tool's exact name:

        - Containers: container_create, container_delete, container_stats, container_describe
        - Files: upload_file, bulk_upload, append_to_file, get_documents, delete_file, bulk_delete
        - Code: code_find_symbol, code_references, code_impact, code_repo_map
        - Memory: memory_spaces, memory_space_create, memory_remember, memory_correct, memory_forget, memory_recall, memory_timeline
        - Approvals: approval_status
        - Admin API (operation ids): users, agents and their keys, settings, connections, providers, sources, folders, audit
        """;

    /// <summary>Whether this request gets every tool directly rather than the compact list.</summary>
    public static bool IsFull(HttpContext? http, IConfiguration configuration) =>
        (http is not null && http.Request.Path.StartsWithSegments(FullPath, StringComparison.OrdinalIgnoreCase))
        || string.Equals(configuration[SettingKey], "full", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a tool appears in <c>tools/list</c> for the given list.</summary>
    public static bool Shows(string name, bool full) =>
        full ? !McpToolSearch.Names.Contains(name) : Core.Contains(name) || McpToolSearch.Names.Contains(name);

    /// <summary>Hides the tools the current list doesn't show. Hidden tools can still be called by name.</summary>
    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> Filter(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (context, ct) =>
        {
            var result = await next(context, ct);
            var services = context.Services!;
            bool full = IsFull(services.GetService<IHttpContextAccessor>()?.HttpContext, services.GetRequiredService<IConfiguration>());
            result.Tools = [.. result.Tools.Where(t => Shows(t.Name, full))];
            return result;
        };

    /// <summary>Sets the instructions for the list this session gets.</summary>
    public static Task ConfigureSession(HttpContext http, McpServerOptions options, CancellationToken ct)
    {
        if (!IsFull(http, http.RequestServices.GetRequiredService<IConfiguration>()))
            options.ServerInstructions = CompactInstructions;
        return Task.CompletedTask;
    }
}
