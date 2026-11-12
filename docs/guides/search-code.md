---
title: Search code with an agent
description: Index C#, TypeScript and Python code in Connapse and let an AI agent find definitions, callers and the impact of a change over MCP.
type: how-to
audience: user
---

# Search code with an agent

Use this when you want an AI agent to find its way around a codebase through Connapse: where a symbol is defined, what calls it, and what a change to it could break. Connapse parses C#, TypeScript and Python with tree-sitter, a syntax-tree parser, so this works without compiling or running the code.

## Prerequisites

- An MCP client connected with a key that holds `knowledge:read`, as in [Connect an MCP client](mcp-clients.md).
- Code files (`.cs`, `.ts`, `.mts`, `.cts`, `.tsx`, `.py`, `.pyi`) in a container or source. Each definition becomes its own passage, and the parser records the definitions, imports, calls and base types that link files together.

## Steps

1. Get the code into Connapse, in one of three ways:
   - Upload the files to a container, on the **Files** page or with the `upload_file` MCP tool and its `files` list.
   - Add a GitHub **Docs** source and add patterns such as `*.cs` or `*.py` to its file patterns. See [GitHub setup](../sources/github-setup.md#what-gets-indexed).
   - Add an SFTP source whose patterns include the code's extensions. See [Index a folder over SFTP](../sources/sftp.md).

2. Wait until the files show **Ready**, or the source's sync finishes.

3. Ask the agent about the code, naming the container or source. For example:

   ```text
   In the billing-service container, map the code, then find where Ledger.Post is defined and what a change to it would affect.
   ```

   The agent picks from four tools:

   | Tool | Use it to |
   |------|-----------|
   | `code_repo_map` | Start in an unfamiliar repository: the files the rest of the code refers to most, with their main definitions. Pass `path` to map one folder. |
   | `code_find_symbol` | Find a definition by name (`Ledger.Post`), by words of its name (`match="words"`), or by a description (`match="meaning"`). `follow=true` also returns what it calls into. |
   | `code_references` | List the files that use a symbol: calls, base types and imports with their lines first, then files that only mention it. |
   | `code_impact` | List what a change could affect, up to three hops through callers and subtypes, with the tests among them. |

   Ordinary `search_knowledge` searches code passages too, alongside any docs in the same container.

## Limits

- Only links the parser could resolve are followed. A call made through reflection, dependency injection or a dynamic import may be missing from `code_impact`, so check `code_references` for every mention.
- Other languages can be indexed as plain text only if their extension is a [supported file type](../reference/file-types.md); the `code_*` tools don't see them.
- Results are trimmed to what the caller may read, like every search.

## Related

- [MCP tools](../reference/mcp-tools.md#named-tools) lists every parameter of the `code_*` tools.
- [About ingestion](../concepts/ingestion.md) explains how code is parsed.
- [Following links](../concepts/search.md#following-links) uses the same code links in ordinary search.
