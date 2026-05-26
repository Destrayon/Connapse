# Manual QA: Container auto-summary steering

Verify that the auto-generated container summary biases an MCP-connected agent toward calling `search_knowledge` with appropriate queries instead of enumerating files.

## Setup

1. Run a local instance with an `ILlmProvider` configured (any of: Anthropic, OpenAI, Ollama, Azure).
2. Create a test container and upload ~10 documents on a coherent topic (e.g., Apple earnings reports, or any set of related markdown).
3. Wait for per-doc summaries to generate (check logs for `PerDocSummaryCompleted` events).
4. Wait for or trigger a container rollup (or lower `Summary.DebounceCount` in container settings for the test).
5. Verify `Container.Summary` is non-null via the `container_describe` MCP tool.

## Test scenarios

1. **Routing test.** Connect Claude Code (or an equivalent MCP client). Ask: "What did Apple report for Q3 2025 iPhone sales?" Expected: agent calls `container_list` → `search_knowledge(container=<test>, query="iPhone Q3 2025 sales")`. Should NOT use `list_files` + `get_document`.

2. **Out-of-scope test.** Ask the agent something the container should NOT route to. Expected: agent does not route based on the summary's "Does not cover…" section.

3. **Query-term lift.** Compare agent query terms with/without summary. Agent should use phrases from the "Query hints" section more often when summary is present.
