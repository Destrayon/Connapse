namespace Connapse.Web.Mcp;

/// <summary>
/// Centralizes the MCP server configuration strings that Program.cs ships to the
/// ModelContextProtocol SDK. Extracted here so the routing-rule text can be
/// regression-tested without lighting up an MCP integration fixture.
/// </summary>
public static class McpServerConfig
{
    /// <summary>
    /// ServerInstructions delivered to MCP clients on connect. Tells the agent how
    /// to route across container_list / search_knowledge / list_files / get_document.
    /// </summary>
    public const string McpServerInstructions =
        "Connapse is a retrieval-augmented knowledge base. For ANY question-answering or " +
        "research task over container contents, call `search_knowledge` FIRST — it returns " +
        "the relevant ranked passages directly with citations. " +
        "Use `list_files` ONLY when the user explicitly asks for a file inventory or names " +
        "a specific filename to look up. Use `get_document` ONLY after `search_knowledge` " +
        "returns a `DocumentId` you need to read in full. " +
        "Enumerating files and reading them one by one to answer content questions will " +
        "exceed context and produce worse answers than a single `search_knowledge` call.";
}
