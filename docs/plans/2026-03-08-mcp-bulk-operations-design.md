# MCP Bulk Operations Design

## Summary

Add two new MCP tools — `bulk_delete` and `bulk_upload` — for batch file operations within a single container. Both use thin wrappers over existing service calls (no new interfaces or store methods).

## Tools

### `bulk_delete`

**Parameters:**
- `containerId` (string) — Container ID or name
- `fileIds` (string) — JSON array of document IDs (max 100)

**Behavior:**
1. Resolve container (fail if not found)
2. Parse + validate JSON array (reject if empty, >100, or malformed)
3. For each file ID:
   - Look up document, verify container membership
   - `documentStore.DeleteAsync()` (cascades chunks + vectors via FK)
   - Best-effort `fileSystem.DeleteAsync()` for physical file (log warning on failure)
   - Record per-item result
4. Return summary with per-item success/failure

### `bulk_upload`

**Parameters:**
- `containerId` (string) — Container ID or name
- `files` (string) — JSON array of file objects (max 100)

**File object schema:**
```json
{
  "filename": "report.pdf",       // required
  "content": "...",               // required, base64 or raw text
  "encoding": "base64",          // optional, default "text"
  "folderPath": "docs/2026"      // optional, default root
}
```

**Behavior:**
1. Resolve container (fail if not found)
2. Parse + validate JSON array (reject if empty, >100, or malformed)
3. Generate single `batchId` for all jobs
4. For each file object:
   - Validate required fields
   - Build path from folderPath + filename
   - Decode content (base64 or text)
   - Write via connector/filesystem
   - Create document record + enqueue ingestion job with shared batchId
   - Record per-item result
5. Return summary with per-item success/failure

## Design Decisions

- **Single container per call** — simplifies validation, consistent between both tools
- **100 item cap** — prevents resource exhaustion
- **Continue on failure** — partial failures reported per-item, matches existing best-effort pattern
- **No new interfaces** — reuses `IDocumentStore.DeleteAsync`, `IKnowledgeFileSystem`, `IIngestionQueue`
- **JSON string parameters** — MCP SDK tool attributes use scalar params; arrays deserialized internally

## Files Modified

- `src/Connapse.Web/Mcp/McpTools.cs` — two new tool methods
- `tests/Connapse.Core.Tests/Mcp/McpToolsBulkDeleteTests.cs` — new test file
- `tests/Connapse.Core.Tests/Mcp/McpToolsBulkUploadTests.cs` — new test file

## Test Coverage

### bulk_delete
- Happy path (multiple files)
- Partial failure (file not found)
- Storage cleanup warning
- Container not found
- Exceeds 100 / empty array / invalid JSON

### bulk_upload
- Happy path (mixed text + base64)
- Partial failure (invalid base64)
- Batch ID shared across jobs
- Container not found
- Exceeds 100 / empty array / missing fields
- Folder auto-creation
