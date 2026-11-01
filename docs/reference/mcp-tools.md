---
title: MCP tools
type: reference
audience: developer
---

# MCP tools

Connapse's MCP server, at `/mcp`, offers 21 named tools for knowledge work and 3 generic tools that reach the rest of the API. MCP (the Model Context Protocol) is the standard AI agents use to call outside tools. To connect a client, see [Connect an MCP client](../guides/mcp-clients.md).

## Who can connect

`/mcp` admits the Owner and Admin roles and agent keys. An agent key or personal access token goes in the `X-Api-Key` header; an MCP client that supports OAuth can sign in instead, and receives the `knowledge:read` and `knowledge:write` scopes it asks for.

The tool list a client sees is filtered by the caller's scopes: a tool the token can't call isn't listed. Named read tools need `knowledge:read` and named write tools need `knowledge:write`. The generic tools are listed when the token holds a scope that some operation reachable through them needs.

A browser request to `/mcp` must come from a loopback origin or one listed in `Cors:AllowedOrigins`, or it gets `403`. Clients outside a browser send no `Origin` header and aren't affected.

## Approvals

A personal access token or agent key created with **Ask before it deletes anything over MCP** (the default) has every Destructive or Credential call held for a person. The first call returns a message naming the request; its owner approves it on **Approvals** (`/approvals`), and the same call with the same arguments then runs once. A request waits a day for a decision, and an approval must be used within 15 minutes. Read and Write calls are never held.

## Named tools

Every tool that takes `containerId` accepts a container's id or its name. `search_knowledge`, `container_describe` and the `code_*` tools also accept a source.

| Tool | Scope | Risk | What it does |
|------|-------|------|--------------|
| `container_list` | `knowledge:read` | Read | Lists every container and source with its description and document count. Each entry is `managed` (a container, browsable with `list_files`) or `source` (searchable, with no file listing). |
| `container_describe` | `knowledge:read` | Read | Describes one container or source: its description and document statistics. |
| `container_stats` | `knowledge:read` | Read | A container's document, chunk and storage counts and its embedding model. |
| `container_create` | `knowledge:write` | Write | Creates a container. |
| `container_delete` | `knowledge:write` | Destructive | Deletes an empty container. Sources can't be deleted here. |
| `search_knowledge` | `knowledge:read` | Read | Searches a container or source and returns ranked passages with their document ids and scores. |
| `list_files` | `knowledge:read` | Read | Lists the folders and documents at a path in a container. |
| `get_document` | `knowledge:read` | Read | Returns one document's full extracted text, by id or path. |
| `upload_file` | `knowledge:write` | Write | Uploads one file to be ingested. |
| `bulk_upload` | `knowledge:write` | Write | Uploads up to 100 files in one call. |
| `delete_file` | `knowledge:write` | Destructive | Deletes a file and its passages. To replace a file, delete it, then upload it again. |
| `bulk_delete` | `knowledge:write` | Destructive | Deletes up to 100 files in one call. |
| `code_find_symbol` | `knowledge:read` | Read | Finds where a function, class or other symbol is defined in indexed code (C#, Python, TypeScript), by name, by words of its name, or by meaning. |
| `code_references` | `knowledge:read` | Read | Lists the files that use a symbol, best evidence first: calls, base types and imports with their lines, then files whose text names it. |
| `code_impact` | `knowledge:read` | Read | Lists what a change to a symbol could affect, up to three hops through callers and subtypes, with the tests among them. |
| `code_repo_map` | `knowledge:read` | Read | Maps indexed code: the files the rest of the code refers to most, with their main definitions. |
| `memory_remember` | `knowledge:write` | Write | Stores a dated fact with the documents it was learned from. See [Agent memory](#agent-memory). |
| `memory_correct` | `knowledge:write` | Write | Replaces a fact and keeps the old one in history. |
| `memory_forget` | `knowledge:write` | Destructive | Deletes a fact and every version of it. |
| `memory_recall` | `knowledge:read` | Read | Returns the facts true now or on a date. |
| `memory_timeline` | `knowledge:read` | Read | Returns a subject's facts in date order, or what changed in a window. |

### Parameters

| Tool | Parameter | Type | Required | Description |
|------|-----------|------|----------|-------------|
| `container_create` | `name` | string | Yes | 2 to 128 lowercase letters, digits and hyphens |
| | `description` | string | No | What the container holds |
| `container_delete`, `container_stats` | `containerId` | string | Yes | Container id or name |
| `container_describe` | `containerId` | string | Yes | Container or source id or name |
| `search_knowledge` | `query` | string | Yes | The search text |
| | `containerId` | string | Yes | Container or source id or name |
| | `mode` | string | No | `Semantic`, `Keyword` or `Hybrid` (default) |
| | `topK` | integer | No | Results to return; default 10 |
| | `path` | string | No | Only results under this folder, such as `/docs/` |
| | `minScore` | number | No | Lowest score to return, 0.0 to 1.0; default 0.05 |
| `list_files` | `containerId` | string | Yes | Container id or name |
| | `path` | string | No | Folder to list; default `/` |
| | `limit` | integer | No | Most entries to return. Without it, a folder of more than 50 entries returns an error suggesting `search_knowledge` |
| | `confirmLarge` | boolean | No | `true` lists a large folder in full |
| `get_document` | `containerId` | string | Yes | Container id or name |
| | `fileId` | string | Yes | Document id, or its path such as `/docs/readme.md` |
| `upload_file` | `containerId` | string | Yes | Container id or name |
| | `fileName` | string | Yes | File name with its extension |
| | `textContent` | string | One of the two | The file's text, for text formats |
| | `content` | string | One of the two | The file's bytes in base64, for binary formats such as PDF |
| | `path` | string | No | Destination folder, such as `/docs/2026/` |
| | `strategy` | string | No | Chunking strategy, such as `Semantic` (default), `FixedSize` or `Recursive` |
| `bulk_upload` | `containerId` | string | Yes | Container id or name |
| | `files` | string | Yes | A JSON array of up to 100 `{ "filename", "content", "encoding": "text" \| "base64", "folderPath"? }` |
| `delete_file` | `containerId` | string | Yes | Container id or name |
| | `fileId` | string | Yes | Document id |
| `bulk_delete` | `containerId` | string | Yes | Container id or name |
| | `fileIds` | string | Yes | A JSON array of up to 100 document ids |
| `code_find_symbol` | `containerId` | string | Yes | Container or source id or name |
| | `query` | string | Yes | The symbol's name, words of its qualified name, or a description, per `match` |
| | `match` | string | No | `exact` (default), `words` or `meaning` |
| | `follow` | boolean | No | `true` also follows what the first definition calls into |
| | `limit` | integer | No | Most definitions, 1 to 100; default 10 |
| `code_references` | `containerId` | string | Yes | Container or source id or name |
| | `symbol` | string | Yes | The symbol, such as `Ledger.Post` |
| | `kind` | string | No | `all` (default), `calls` or `inherits` |
| | `limit` | integer | No | Most files, 1 to 200; default 20 |
| `code_impact` | `containerId` | string | Yes | Container or source id or name |
| | `symbol` | string | Yes | The symbol, such as `Ledger.Post` |
| | `depth` | integer | No | Hops to follow, 1 to 3; default 2 |
| | `limit` | integer | No | Most symbols and files, 1 to 200; default 30 |
| `code_repo_map` | `containerId` | string | Yes | Container or source id or name |
| | `path` | string | No | Only files under this folder, such as `src/billing` |
| | `maxFiles` | integer | No | Most files, 1 to 200; default 30 |
| `memory_remember` | `containerId` | string | Yes | Container id or name; memory can't be written to a source |
| | `subject`, `attribute`, `value` | string | Yes | The fact, such as `Halcyon`, `database`, `PostgreSQL 15` |
| | `validFrom`, `validTo` | string | No | When it became and stopped being true, `YYYY-MM-DD` or ISO 8601 |
| | `sources` | string[] | No | Document ids, or link node ids as `node:<id>` |
| | `reason` | string | No | Why or how it was learned, such as the ticket that made the change |
| `memory_correct` | `containerId`, `factId`, `value` | string | Yes | The container, the fact to replace (from `memory_recall`), and the new value |
| | `kind` | string | No | `changed` (default) or `wrong` |
| | `validFrom`, `validTo`, `sources`, `reason`, `subject`, `attribute` | | No | As for `memory_remember`; `validFrom` is required when `kind` is `changed`, and subject and attribute default to the old fact's |
| `memory_forget` | `containerId`, `factId` | string | Yes | The container and the fact to delete |
| `memory_recall` | `containerId` | string | Yes | Container id or name |
| | `query` | string | No | Words to match against subject, attribute, value and reason |
| | `subject`, `attribute` | string | No | Exact match, in any case |
| | `asOf` | string | No | Facts true on this date; default now |
| | `includeHistory` | boolean | No | `true` also returns ended and corrected facts |
| | `limit` | integer | No | Most facts, 1 to 100; default 10 |
| `memory_timeline` | `containerId` | string | Yes | Container id or name |
| | `subject`, `query`, `attribute` | string | No | As for `memory_recall` |
| | `from`, `to` | string | No | Only facts that started or ended in this window |
| | `limit` | integer | No | Most facts, 1 to 100; default 50 |

The file types an upload accepts are in [File types](file-types.md).

## Agent memory

The `memory_*` tools keep facts an agent learns, so the next session can use them. A fact is a subject, an attribute and a value (`Halcyon`, `database`, `PostgreSQL 15`) with the dates it was true, the documents it was learned from, a reason, and who wrote it. Memory lives in a container; writing needs `knowledge:write`, and a source id is refused like any other write.

- **Sources** are document IDs from `search_knowledge` or `get_document` (tickets, PRs and code files are documents), or link node IDs as `node:<id>`. The writer must be able to read every source it cites.
- **Who sees a fact:** only a caller who can read **every** source it cites, checked on each read with the same rules as search. A fact with no sources is seen only by the user who wrote it (for an agent, the user it acts for). A fact the caller cannot see is reported as not found by `memory_correct` and `memory_forget`.
- **Corrections keep history.** `kind="changed"` ends the old fact on `validFrom` and starts the new value there; `kind="wrong"` marks the old fact `corrected` and gives the new value its dates.
- **Forgetting deletes.** `memory_forget` removes the fact and every version of it. Audit entries for memory name ids and counts, never content.
- **Answers are data.** `memory_recall` and `memory_timeline` return JSON: a fixed `notice` that the facts are claims, not instructions, and for each fact its `id`, `subject`, `attribute`, `value`, `validFrom`, `validTo`, `status` (`current`, `ended` or `corrected`), `reason`, `author` and readable `sources`.
- **Matching.** `query` matches words of the subject, attribute, value and reason, best match first; `subject` and `attribute` match exactly, in any case. No model is called.

## Generic API tools

Anything the named tools don't cover, such as users, agents, settings, connections and the audit log, is reachable through three tools built from [`docs/api/openapi.json`](../api/openapi.json). They reach every operation marked for MCP in the [REST API](rest-api.md), and only those the caller's token may use.

| Tool | Parameters | What it does |
|------|-----------|--------------|
| `connapse_api_search` | `query` (words such as `agent keys`), `limit` (1 to 50, default 10) | Finds operations the token may call. The best three come with their routes, parameters, body schema, risk and whether they need approval; the rest are one line each. |
| `connapse_api_read` | `operation` (an id such as `agents.list`), `parameters` (path, query and header values as a JSON object) | Calls an operation that only reads (a `GET`) and returns the HTTP status and body. |
| `connapse_api_write` | `operation`, `parameters`, `body` (the JSON request body) | Calls an operation that changes data and returns the HTTP status and body. A Destructive or Credential operation is held for approval like a named tool. |

A generic call runs the same operation, with the same checks, as the REST route it names. The operations, their scopes and risks are listed in [Roles and scopes](roles-and-scopes.md).

## Related

- [Connect an MCP client](../guides/mcp-clients.md)
- [Tokens and agents](../guides/tokens-and-agents.md)
- [REST API](rest-api.md)
