# Fix: Cancel in-flight ingestion jobs when deleting files via MCP

**Issue:** [#146](https://github.com/Destrayon/Connapse/issues/146)
**Date:** 2026-03-12
**Size:** XS (~10 lines changed)

## Problem

The REST API's `DELETE /api/containers/{id}/documents/{fileId}` calls `CancelJobForDocumentAsync(fileId)` before deleting a file, cancelling any in-flight ingestion job. The MCP `delete_file` tool does not, leaving orphaned ingestion jobs that process a document that no longer exists in the database.

Additionally, `bulk_delete` duplicates the per-file deletion logic from `delete_file` but is missing the write guard, ingestion cancellation, and empty-ancestor folder cleanup.

## Changes

### 1. `DeleteFile` — add ingestion cancellation

In `src/Connapse.Web/Mcp/McpTools.cs`, add a call to `CancelJobForDocumentAsync(fileId)` before `documentStore.DeleteAsync()`, matching the REST API pattern in `DocumentsEndpoints.cs:271`.

### 2. `BulkDelete` — delegate to `DeleteFile`

Replace the inline per-file deletion loop with calls to `DeleteFile(services, containerId, fileId, ct)`. Classify results by string prefix:

- Starts with `"Error:"` → failure
- Contains `"backing storage file"` → success with storage warning
- Otherwise → clean success

This eliminates duplicated logic and ensures `bulk_delete` inherits the write guard, ingestion cancellation, and folder cleanup from `delete_file`.

## Files modified

- `src/Connapse.Web/Mcp/McpTools.cs` — both `DeleteFile` and `BulkDelete` methods

## Acceptance criteria

- [x] `delete_file` MCP tool calls `CancelJobForDocumentAsync` before deletion
- [x] `bulk_delete` reuses `delete_file` logic (gets write guard + cancellation + folder cleanup)
- [x] Read-only connector status checked before deleting (already present via `ContainerWriteGuard.CheckWrite`)
