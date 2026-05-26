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

## v2 — Configurable settings scenarios

### Scenario: Toggle enable/disable

1. Go to `/settings` → Summary tab
2. Confirm "Enable summary generation" checkbox is unchecked (opt-in default)
3. Upload a document to any container — verify no per-doc summary generated (DB: `documents.summary IS NULL` for the new row)
4. Check the box, click "Save Summary Settings"
5. Upload a document — verify per-doc summary IS generated within ingestion latency

### Scenario: Per-doc prompt override (fixes Qwen 3 script-bleed)

1. With Ollama + qwen3:14b configured and enabled, upload an engineering-flavored document and verify the summary may contain a Chinese character (`流程` etc.)
2. Go to `/settings` → Summary tab → "Per-document system prompt" textarea
3. Append "Respond entirely in English. /no_think" to the end of the default text
4. Save
5. Upload a fresh engineering document — verify the summary is entirely in English with no thinking preamble

### Scenario: Per-container override via FileBrowser

1. Open a specific container at `/{slug}/files`
2. Click the Settings tab
3. In the Summary Generation section: enable, set LLM Model to a non-default
4. Click Save
5. Upload a document to THIS container — verify the per-doc summary uses the override model (check the `documents.summary` or LLM provider logs)
6. Upload a document to a DIFFERENT container — verify it uses the global model (not the per-container override)

### Scenario: "Restore default" link behavior

1. Edit the per-doc prompt textarea
2. Click "Restore default" — verify the textarea repopulates with the in-code default text
3. Save — verify no override is stored (DB JSON for that container has `summary.perDocSystemPrompt: null` or the field absent)
