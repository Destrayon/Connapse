namespace Connapse.Web.Mcp;

/// <summary>
/// Centralizes the MCP server configuration strings that Program.cs ships to the
/// ModelContextProtocol SDK. Extracted here so the routing-rule text can be
/// regression-tested without lighting up an MCP integration fixture.
/// </summary>
public static class McpServerConfig
{
    /// <summary>
    /// ServerInstructions delivered to MCP clients on connect. Numbered disjoint
    /// conditional rules — designed for literally-compliant reasoning models
    /// (e.g., Claude Opus 4.7) per the v2 spec.
    /// </summary>
    public const string McpServerInstructions =
        """
        Connapse is a retrieval-augmented knowledge base with four primary tools:
        `container_list`, `search_knowledge`, `list_files`, and `get_document`.

        Routing rules for question-answering:

        1. If the user's question (or the prior conversation) names a specific
           container, call `search_knowledge` directly on that container.
           Do NOT call `container_list` first — the container is already known.

        2. Only call `container_list` when the relevant container is genuinely
           unknown and cannot be inferred from context. After it returns, call
           `search_knowledge` on the chosen container.

        3. Use `list_files` only for inventory questions ("what files are in X?",
           "list the docs under /folder"). Never use `list_files` as a substitute
           for `search_knowledge` when the user is asking about file CONTENT.

        4. Use `get_document` only when `search_knowledge` has returned a specific
           `DocumentId` you need to read in full, or when the user named an exact
           file by path.

        Enumerating files and reading them one by one to answer content questions
        will exceed context and produce worse answers than a single
        `search_knowledge` call.
        """;
}
