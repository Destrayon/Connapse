# container_describe — Echo Server Instructions

**Date:** 2026-06-02
**Status:** Approved (design) — pending implementation plan
**Repo:** Connapse — `src/Connapse.Web/Mcp`

## Problem

The MCP `ServerInstructions` (the tool-routing rules in
`McpServerConfig.McpServerInstructions`, wired via `options.ServerInstructions`
in `Program.cs`) are delivered to clients exactly once, on connect. Many MCP
clients drop or ignore the `instructions` field from the `initialize` response,
so the routing rules never reach the agent. Agents on those clients then call
tools sub-optimally — e.g., enumerating files for content questions instead of
calling `search_knowledge`.

## Goal

Echo the canonical server routing rules inside the `container_describe` tool
output, so agents on instruction-dropping clients still receive them. Scope is
**`container_describe` only** — the other primary tools (`container_list`,
`search_knowledge`, `list_files`, `get_document`) are intentionally left lean.

## Non-Goals

- No per-container instructions field — the `Container` model is unchanged.
- No changes to any other MCP tool.
- No conditional / stateful "first call only" logic — the echo is unconditional
  (MCP tool calls are stateless; we cannot detect whether the client already
  received the connect-time instructions).
- No change to how `ServerInstructions` are delivered on connect — that path
  stays exactly as-is.

## Design

### Output shape

`container_describe` returns its existing facts unchanged, then appends a
delimited block:

```
Container: research-papers
ID: <guid>
Type: ManagedStorage
Description: Academic research papers
Summary: A collection of AI and machine learning research.
Summary generated: 2026-04-01 09:00:00Z
Documents: 12
Storage: 2.0 MB
Created: 2026-01-10 08:00:00Z

---
Server instructions:
Connapse is a retrieval-augmented knowledge base with four primary tools:
... (full McpServerConfig.McpServerInstructions text, verbatim) ...
```

### Source of truth

The appended text references `McpServerConfig.McpServerInstructions` directly.
`McpTools` and `McpServerConfig` share the `Connapse.Web.Mcp` namespace, so no
new `using` is required. The rules text is **never duplicated** —
`McpServerConfig` remains the single source, and its existing regression tests
continue to own the content.

### Implementation sketch

In `McpTools.ContainerDescribe` (`src/Connapse.Web/Mcp/McpTools.cs`), after the
final `Created:` line and immediately before `return text;`:

```csharp
text += $"Created: {container.CreatedAt:u}";
text += "\n\n---\nServer instructions:\n" + McpServerConfig.McpServerInstructions;
return text;
```

The current output ends with the `Created:` line and no trailing newline, so the
appended block leads with `\n\n` to separate cleanly from the facts.

### Tool metadata

Extend the `[Description(...)]` attribute on `ContainerDescribe` to note that it
also returns the server's tool-routing instructions, keeping the tool's
self-description honest — e.g. append a sentence such as: "Also echoes the
server's tool-routing instructions for clients that don't surface them on
connect."

### Error / edge paths

- The not-found error paths (`Error: Container '...' not found.`) return
  **before** the facts are built, so the instructions block is **not** appended
  to error responses. This is intentional — errors stay terse.
- No new failure modes. `McpServerInstructions` is a compile-time constant, so
  it is never null or empty.

## Testing

Add to `tests/Connapse.Core.Tests/Mcp/McpToolsContainerDescribeTests.cs`:

- `ContainerDescribe_AppendsServerInstructions` — asserts the success output
  `Contains(McpServerConfig.McpServerInstructions)` (constant-referenced, so it
  never goes brittle when the rules text changes) and contains the
  `Server instructions:` header.
- Assert the not-found path does **not** contain the `Server instructions:`
  header, locking in the errors-stay-terse behavior.

Existing describe tests (summary present/absent, status breakdown,
resolve-by-name, no-description) are unaffected — the facts portion of the
output is unchanged.

## Risks & tradeoffs

- **Token cost:** ~25 extra lines per `container_describe` response. Bounded to
  this one tool; accepted.
- **Redundancy for compliant clients:** clients that *do* honor connect-time
  instructions receive the rules twice. Harmless — the routing rules are
  idempotent guidance.
