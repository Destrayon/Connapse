# MCP Tools Reference

Connapse exposes 11 tools via the [Model Context Protocol](https://modelcontextprotocol.io/) server at `/mcp`. All tools require authentication — see [Using with Claude (MCP)](../README.md#using-with-claude-mcp) for setup.

## Quick Reference

| Tool | Description | Access |
|------|-------------|--------|
| `container_create` | Create a new container for organizing documents | Write |
| `container_list` | List all containers with document counts | Read |
| `container_delete` | Delete a container | Write |
| `container_stats` | Get container statistics | Read |
| `upload_file` | Upload a single file | Write |
| `bulk_upload` | Upload up to 100 files | Write |
| `list_files` | List files and folders at a path | Read |
| `get_document` | Retrieve full text of a document | Read |
| `delete_file` | Delete a file and its vectors | Write |
| `bulk_delete` | Delete up to 100 files | Write |
| `search_knowledge` | Semantic, keyword, or hybrid search | Read |

---

## container_create

Create a new container for organizing documents. Use when setting up a new knowledge domain or project.

### Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `name` | string | Yes | Container name (lowercase alphanumeric and hyphens, 2-128 chars) |
| `description` | string | No | Optional description for the container |

### Return Format

```
Container '<name>' created.

ID: <uuid>
```

### Error Cases

- `Error: Container name must be 2-128 chars, lowercase alphanumeric and hyphens.`
- `Error: Container '<name>' already exists.`

### Usage Example

> "Create a container called 'project-docs' for storing our project documentation"

---

## container_list

Lists every searchable knowledge scope. Use to discover what exists when the target is unknown; if the user already named one, call `search_knowledge` on it directly.

### Parameters

None.

### Return Format

```
Found 3 knowledge scope(s):

- my-docs [managed] (15 files) — Project documentation
  ID: <uuid>
- research [managed] (8 files) — Research papers
  ID: <uuid>
- company-docs [source] (128 files) — Shared reference material
  ID: <uuid>
```

### The `kind` discriminator

Every entry is one of two kinds:

| Kind | What it is | `list_files` |
|------|-----------|--------------|
| `managed` | Storage Connapse owns | Works |
| `source` | An external system Connapse mirrors read-only | **Does not apply** |

Both are searchable, and `search_knowledge` accepts either — search is scoped by owner id, which is the same column whichever kind owns the document.

`list_files` is container-only by design, not by omission. A source has no file listing because enumerating one would list every synced object out of somebody else's system to any Connapse reader, regardless of what they can see in the source system itself. Reading `kind` before calling `list_files` saves a wasted call.

Returns `No containers found.` when empty.

### Error Cases

None.

### Usage Example

> "What knowledge bases are available?"

---

## container_delete

Delete a container. It must be emptied first, because deleting one deletes its stored files. External sources are not containers and cannot be deleted here — use `DELETE /api/sources/{id}`.

### Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `containerId` | string | Yes | Container ID (UUID) or name |

### Return Format

```
Container '<name>' deleted.
```

### Error Cases

- `Error: Container '<id>' not found.`
- `Error: Container '<id>' is not empty. Delete all files first.`

### Usage Example

> "Delete the 'old-project' container"

---

## container_stats

Get container statistics: document counts, chunk count, storage size, and embedding model info.

### Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `containerId` | string | Yes | Container ID (UUID) or name |

### Return Format

```
Container: my-docs
Documents: 15
Chunks: 342
Storage: 2.4 MB
Embedding model: text-embedding-3-small (1536 dims, 342 vectors)
Last indexed: 2026-03-15 10:30:00Z
Created: 2026-03-10 08:00:00Z
```

When any documents are processing or failed:
```
Documents: 15 (12 ready, 2 processing, 1 failed)
```

### Error Cases

- `Error: Container '<id>' not found.`

### Usage Example

> "How many documents and chunks are in the 'research' container?"

---

## upload_file

Upload a file to be parsed, chunked, embedded, and made searchable. Provide either `content` (base64) or `textContent` (raw text), not both.

### Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `containerId` | string | Yes | Container ID (UUID) or name |
| `content` | string | No | Base64-encoded file content. For binary files (PDF, DOCX, images). Mutually exclusive with `textContent`. |
| `textContent` | string | No | Raw text content for text-based files (Markdown, TXT, CSV, JSON, etc.). Mutually exclusive with `content`. |
| `fileName` | string | Yes | Original file name with extension |
| `path` | string | No | Destination folder path (e.g., `/docs/2026/`). Default: `/` |
| `strategy` | string | No | Chunking strategy: `Semantic`, `FixedSize`, or `Recursive`. Default: `Semantic` |

### Return Format

```
File 'readme.md' uploaded to /docs/readme.md and queued for ingestion.

Document ID: <uuid>
Job ID: <uuid>

The file will be parsed, chunked, and embedded in the background.
```

### Error Cases

- `Error: 'fileName' is required.`
- `Error: invalid filename '<name>' — must not contain path separators or '..' segments.`
- `Error: file type '<ext>' is not supported. Supported types: ...`
- `Error: Provide either 'content' or 'textContent', not both.`
- `Error: Provide either 'content' (base64) or 'textContent' (raw text).`
- `Error: Container '<id>' not found.`
- `Error: 'content' must be valid base64-encoded data.`
- `Error: Container '<id>' not found.` for a source id — write tools resolve containers only

### Usage Example

> "Upload this markdown content as 'meeting-notes.md' to the 'project-docs' container"

---

## bulk_upload

Upload up to 100 files in one call. Each file is parsed, chunked, and embedded. Returns per-file results.

### Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `containerId` | string | Yes | Container ID (UUID) or name |
| `files` | string | Yes | JSON array of file objects. Each: `{"filename":"name.txt", "content":"...", "encoding":"text\|base64", "folderPath":"/optional/"}`. Max 100. |

### Return Format

```
Uploaded 3 of 3 file(s) to container 'my-docs'.

All files queued for ingestion (parsing, chunking, embedding).
```

With failures:
```
Uploaded 2 of 3 file(s) to container 'my-docs'.

Failures:
- item[2]: missing 'filename'
```

### Error Cases

- `Error: 'files' must be a valid JSON array of file objects.`
- `Error: 'files' array must not be empty.`
- `Error: Maximum 100 files per bulk_upload call.`
- `Error: Container '<id>' not found.`
- Per-file: `missing 'filename'`, `missing 'content'`, `invalid base64 content`

### Usage Example

> "Upload these three research papers to the 'research' container"

---

## list_files

List files and folders at a path within a container. Use to browse container contents before retrieving documents.

### Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `containerId` | string | Yes | Container ID (UUID) or name |
| `path` | string | No | Folder path to list. Default: root `/` |

### Return Format

```
Contents of /:

[DIR]  docs/
[DIR]  images/
[FILE] readme.md (1,234 bytes) ID: <uuid>
[FILE] notes.txt (567 bytes) ID: <uuid>
```

Empty folders show `(empty)`.

### Error Cases

- `Error: Container '<id>' not found.`
- `Error: Folder '<path>' not found in this container.`

### Usage Example

> "What files are in the 'project-docs' container under /docs/?"

---

## get_document

Retrieve a document's full text by ID or path. Returns extracted text for binary formats (PDF, DOCX, PPTX).

### Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `containerId` | string | Yes | Container ID (UUID) or name |
| `fileId` | string | Yes | Document ID (UUID) or virtual path (e.g., `/docs/readme.md`) |

### Return Format

```
Document: readme.md
Path: /docs/readme.md
ID: <uuid>
Size: 1,234 bytes
Created: 2026-03-15 10:30:00Z
---
<full document text content>
```

### Error Cases

- `Error: Container '<id>' not found.`
- `Error: Container '<id>' could not be loaded.` (rare — container deleted between checks)
- `Error: Document '<id>' not found in this container.`
- `Error: Document '<name>' is still being ingested (status: Processing). Try again later.`
- `Error: Document '<name>' failed ingestion: <message>`
- `Error: No parser available for '<ext>' files.`
- `Error: The backing file for '<name>' could not be read from storage.`
- `Document '<name>' exists but contains no readable text content.`

### Usage Example

> "Show me the contents of /docs/architecture.md in the 'project-docs' container"

---

## delete_file

Delete a file and all its chunks and vectors. To update a file, delete it first then re-upload with upload_file.

### Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `containerId` | string | Yes | Container ID (UUID) or name |
| `fileId` | string | Yes | File (document) ID to delete |

### Return Format

```
File 'readme.md' (ID: <uuid>) deleted.
```

If backing storage cleanup fails:
```
File 'readme.md' (ID: <uuid>) deleted from database, but the backing storage file could not be removed and may need manual cleanup.
```

### Error Cases

- `Error: Container '<id>' not found.`
- `Error: File '<id>' not found in this container.`
- `Error: Container '<id>' not found.` for a source id — write tools resolve containers only

### Usage Example

> "Delete the file with ID abc-123 from the 'project-docs' container"

---

## bulk_delete

Delete up to 100 files in one call. Returns per-file success/failure results.

### Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `containerId` | string | Yes | Container ID (UUID) or name |
| `fileIds` | string | Yes | JSON array of file (document) IDs to delete, e.g. `["id1","id2"]`. Max 100. |

### Return Format

```
Deleted 3 of 3 file(s).
```

With warnings (backing storage cleanup failed):
```
Deleted 3 of 3 file(s).

Warnings (1):
- <id>: storage cleanup failed
```

With failures:
```
Deleted 2 of 3 file(s).

Failures:
- <id>: File '<id>' not found in this container.
```

### Error Cases

- `Error: 'fileIds' must be a valid JSON array of strings.`
- `Error: 'fileIds' array must not be empty.`
- `Error: Maximum 100 files per bulk_delete call.`
- `Error: Container '<id>' not found.`

### Usage Example

> "Delete these three files from the 'old-data' container: id1, id2, id3"

---

## search_knowledge

Search a container using semantic, keyword, or hybrid mode. Returns ranked document chunks with scores. Use when answering questions from stored knowledge.

### Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `query` | string | Yes | The search query text |
| `containerId` | string | Yes | Container ID (UUID) or name to search within |
| `mode` | string | No | Search mode: `Semantic` (vector), `Keyword` (full-text), or `Hybrid` (both). Default: `Hybrid` |
| `topK` | integer | No | Number of results to return. Default: `10` |
| `path` | string | No | Filter results to a folder subtree (e.g., `/docs/`) |
| `minScore` | number | No | Minimum similarity score threshold (0.0-1.0). Defaults to server setting. |

### Return Format

```
Found 3 result(s) in 45ms (mode: Hybrid):

--- Result 1 ---
```

When results are truncated by `topK`:
```
Showing 10 of 25 matching chunk(s) in 62ms (mode: Hybrid):

--- Result 1 ---
Score: 0.847
File: architecture.md
Path: /docs/architecture.md
Chunk: 2
DocumentId: <uuid>
Content:
<chunk text>

--- Result 2 ---
Score: 0.723
File: readme.md
Path: /readme.md
Chunk: 0
DocumentId: <uuid>
Content:
<chunk text>
```

Returns `No results found.` when no matches.

### Error Cases

- `Error: Container '<id>' not found.`
- `Query must not exceed <N> characters.` (thrown as exception)
- `topK must be between <min> and <max>.` (thrown as exception)
- `minScore must be between 0.0 and 1.0.` (thrown as exception)

### Usage Example

> "Search the 'research' container for information about vector indexing strategies"

---

## What is writable

Write operations — `container_create`, `container_delete`, `upload_file`, `bulk_upload`, `delete_file`, `bulk_delete` — apply to **containers only**.

There is no permission table any more, because there is no runtime decision left to make. Only the managed-storage connector implements `IWritableConnector`, so a source is *incapable* of being written to rather than told not to at runtime. The per-connector permission flags and `ContainerWriteGuard` that used to be documented here were removed with the connector columns they read.

The practical consequence for an agent: **every write tool resolves containers only.** Passing a source id returns `Error: Container '<id>' not found.` — the same answer as an id that does not exist, because as far as these tools are concerned it is not a container.

Read tools split differently: `search_knowledge` and `container_describe` accept either kind, `list_files` and `get_document` accept containers only. `container_list` tells you which is which via `kind`.

---

## Client Configuration

For MCP client setup (Claude Desktop, Claude Code, Cursor, etc.), see [Using with Claude (MCP)](../README.md#using-with-claude-mcp) in the main README.
