---
title: MCP tools
type: reference
audience: developer
---

# MCP tools

Connapse's MCP server, at `/mcp`, offers 12 named tools for knowledge work and 3 generic tools that reach the rest of the API. MCP (the Model Context Protocol) is the standard AI agents use to call outside tools. To connect a client, see [Connect an MCP client](../guides/mcp-clients.md).

## Who can connect

`/mcp` admits the Owner and Admin roles and agent keys. An agent key or personal access token goes in the `X-Api-Key` header; an MCP client that supports OAuth can sign in instead, and receives the `knowledge:read` and `knowledge:write` scopes it asks for.

The tool list a client sees is filtered by the caller's scopes: a tool the token can't call isn't listed. Named read tools need `knowledge:read` and named write tools need `knowledge:write`. The generic tools are listed when the token holds a scope that some operation reachable through them needs.

A browser request to `/mcp` must come from a loopback origin or one listed in `Cors:AllowedOrigins`, or it gets `403`. Clients outside a browser send no `Origin` header and aren't affected.

## Approvals

A personal access token or agent key created with **Ask before it deletes anything over MCP** (the default) has every Destructive or Credential call held for a person. The first call returns a message naming the request; its owner approves it on **Approvals** (`/approvals`), and the same call with the same arguments then runs once. A request waits a day for a decision, and an approval must be used within 15 minutes. Read and Write calls are never held.

## Named tools

Every tool that takes `containerId` accepts a container's id or its name. `search_knowledge` and `container_describe` also accept a source.

| Tool | Scope | Risk | What it does |
|------|-------|------|--------------|
| `container_list` | `knowledge:read` | Read | Lists every container and source with its description and document count. Each entry is `managed` (a container, browsable with `list_files`) or `source` (searchable, with no file listing). |
| `container_describe` | `knowledge:read` | Read | Describes one container or source: its description, its generated summary if summaries are on, and document statistics. |
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

The file types an upload accepts are in [File types](file-types.md).

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
