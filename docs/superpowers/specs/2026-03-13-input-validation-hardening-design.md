# Input Validation Hardening Pass — Design Spec

**Issue**: [#228](https://github.com/Destrayon/Connapse/issues/228)
**Date**: 2026-03-13
**Milestone**: v0.3.2
**Size**: S (~100-150 lines)

## Problem

Release validation (v0.3.2-alpha.2) found cases where invalid input reaches business logic and causes unhandled 500 errors. PRs #221 (filename length) and #222 (negative topK) fixed individual cases. This spec covers a systematic pass across all remaining endpoints.

## Approach

Per-endpoint inline validation following the existing pattern established in `ContainersEndpoints.cs`, `DocumentsEndpoints.cs`, and recent PRs #234/#236. No new abstractions — early-return `Results.BadRequest(...)` with descriptive error messages.

## Validation Constants

A new static class `ValidationConstants` in `Connapse.Core/Utilities/` centralizes all limits:

| Constant | Value | Rationale |
|---|---|---|
| `MaxQueryLength` | 10,000 | Generous for semantic/passage search via MCP; most platforms cap at 512-2,048 |
| `MinTopK` | 1 | Must request at least 1 result |
| `MaxTopK` | 100 | Industry standard (Google caps at 100) |
| `MinScore` | 0.0 | Floor for normalized cosine similarity |
| `MaxScore` | 1.0 | Ceiling for normalized cosine similarity |
| `MinAgentNameLength` | 2 | Consistent with container name minimum; avoids meaningless single-char names |
| `MaxAgentNameLength` | 64 | Matches AWS IAM (most restrictive major platform) |
| `AgentNamePattern` | `[a-zA-Z0-9_-]+` | Intersection of what major platforms accept |
| `MaxAgentDescriptionLength` | 500 | Between AWS IAM (1,000) and Azure AD (256) |
| `MaxAgentKeyNameLength` | 64 | Same limit as agent name for consistency |
| `MaxPathDepth` | 50 | Prevents excessive nesting; no practical use case exceeds this |
| `MaxFileNameLength` | 255 | Replaces hardcoded `255` in UploadService and DocumentsEndpoints |

## Changes by File

### `src/Connapse.Core/Utilities/ValidationConstants.cs` (new)

Static class with all constants listed above. No methods — just `public const` fields.

### `src/Connapse.Core/Utilities/PathUtilities.cs` (modify)

Extend `IsValidFileName` to reject control characters (U+0000–U+001F, U+007F) in filenames. These are invalid in all major filesystems and indicate malformed input.

### `src/Connapse.Web/Endpoints/SearchEndpoints.cs` (modify)

Both GET `/api/containers/{id}/search` and POST `/api/search`:

1. Query length: reject if > 10,000 chars → `400 { "error": "query_too_long", "message": "Query must not exceed 10,000 characters" }`
2. topK: reject if < 1 or > 100 → `400 { "error": "topk_out_of_range", "message": "topK must be between 1 and 100" }`
3. minScore: reject if < 0.0 or > 1.0 → `400 { "error": "minscore_out_of_range", "message": "minScore must be between 0.0 and 1.0" }`

Extract a static `ValidateSearchParams(string query, int? topK, float? minScore)` helper within the file returning `IResult?` (null = valid, non-null = BadRequest), matching the `PaginationValidator.Validate` pattern. Shared between GET and POST handlers.

### `src/Connapse.Web/Mcp/McpTools.cs` (modify)

The `SearchKnowledge` MCP tool accepts the same `query`, `topK`, and `minScore` parameters but has zero validation. Add the same checks before calling `searchService.SearchAsync`:

1. Query length: reject if > 10,000 chars
2. topK: reject if < 1 or > 100
3. minScore: reject if < 0.0 or > 1.0

MCP tools throw exceptions to surface errors, so validation failures should throw `ArgumentException` with descriptive messages (MCP framework converts these to error responses).

### `src/Connapse.Web/Endpoints/AgentEndpoints.cs` (modify)

POST create handler only (no PUT update endpoint exists for name/description):

1. Name: required, trimmed, 2–64 chars, matches `^[a-zA-Z0-9_-]+$` → `400 { "error": "agent_name_invalid", "message": "Agent name must be 2-64 characters, alphanumeric with hyphens and underscores" }`
2. Description: if provided, trimmed, ≤ 500 chars → `400 { "error": "agent_description_too_long", "message": "Agent description must not exceed 500 characters" }`

Also validate agent key name on `POST /api/v1/agents/{id}/keys`:
1. Key name: required, trimmed, 2–64 chars → `400 { "error": "agent_key_name_invalid", "message": "Agent key name must be 2-64 characters" }`

### `src/Connapse.Web/Services/UploadService.cs` (modify)

- Add path depth validation in `ValidateInput`: count `/` segments in the destination folder path (`request.Path`), reject if > 50 → `"path_too_deep"` error
- Replace hardcoded `255` with `ValidationConstants.MaxFileNameLength`

### `src/Connapse.Web/Endpoints/FoldersEndpoints.cs` (modify)

Add path depth validation on `POST /api/containers/{id}/folders` before folder creation. The folder path does not go through `UploadService`, so it needs its own check:
- Count `/` segments, reject if > 50 → `400 { "error": "path_too_deep", "message": "Path exceeds maximum depth of 50 levels" }`

### `src/Connapse.Web/Endpoints/DocumentsEndpoints.cs` (modify)

Replace hardcoded `255` filename length constant with `ValidationConstants.MaxFileNameLength`. Also update the `error.Contains("255 characters")` string comparison (line ~498) to reference the constant, preventing breakage if the limit changes.

## Error Format

Use machine-readable error codes with human-readable messages consistently (matching the `DocumentsEndpoints` pattern which is the most API-consumer-friendly):

```json
{ "error": "topk_out_of_range", "message": "topK must be between 1 and 100" }
{ "error": "minscore_out_of_range", "message": "minScore must be between 0.0 and 1.0" }
{ "error": "query_too_long", "message": "Query must not exceed 10,000 characters" }
{ "error": "agent_name_invalid", "message": "Agent name must be 2-64 characters, alphanumeric with hyphens and underscores" }
{ "error": "path_too_deep", "message": "Path exceeds maximum depth of 50 levels" }
```

## What's Already Validated (No Changes Needed)

| Area | Status |
|---|---|
| Container names | 2-128 chars, lowercase alphanumeric + hyphens, rejects uppercase (PR #225) |
| Filename length | Max 255 chars (PR #234) |
| Path traversal | `..` segments rejected in paths and filenames |
| File type | Unsupported extensions rejected; batch fails if any file invalid |
| Pagination | skip >= 0, 1 <= take <= 200 via `PaginationValidator` |
| File size | Zero-byte files rejected |

## Test Plan

### Unit Tests (`Connapse.Core.Tests`)

- `PathUtilities.IsValidFileName` rejects filenames with control characters (null byte, tab, newline, 0x1F, 0x7F)
- `PathUtilities.IsValidFileName` still accepts valid filenames with dots, spaces, unicode

### Integration Tests (`Connapse.Integration.Tests`)

**Search validation** (new test class or extend `SearchEndpointTests`):
- topK = 0 → 400
- topK = -1 → 400
- topK = 101 → 400
- topK = 1 → 200 (boundary)
- topK = 100 → 200 (boundary)
- minScore = -0.1 → 400
- minScore = 1.1 → 400
- minScore = 0.0 → 200 (boundary)
- minScore = 1.0 → 200 (boundary)
- Query of 10,001 chars → 400
- Query of 10,000 chars → 200 (boundary)

**Agent validation** (new test class or extend existing):
- Empty name → 400
- Single char name → 400
- Name with 65 chars → 400
- Name with 64 chars → 200 (boundary)
- Name with 2 chars → 200 (boundary)
- Name with spaces → 400
- Name with special chars (e.g., `!@#`) → 400
- Valid name (`my-agent_01`) → 200
- Description with 501 chars → 400
- Description with 500 chars → 200 (boundary)
- Agent key: empty name → 400
- Agent key: name with 65 chars → 400
- Agent key: valid name → 200

**Upload validation** (extend `FileUploadSanitizationTests`):
- Filename with null byte → 400
- Filename with control char → 400
- Path with 51 levels → 400
- Path with 50 levels → 200 (boundary)

**Folder validation** (extend folder tests):
- Folder path with 51 levels → 400
- Folder path with 50 levels → 200 (boundary)

## Files Summary

| File | Action |
|---|---|
| `src/Connapse.Core/Utilities/ValidationConstants.cs` | Create |
| `src/Connapse.Core/Utilities/PathUtilities.cs` | Modify |
| `src/Connapse.Web/Endpoints/SearchEndpoints.cs` | Modify |
| `src/Connapse.Web/Endpoints/AgentEndpoints.cs` | Modify |
| `src/Connapse.Web/Endpoints/FoldersEndpoints.cs` | Modify |
| `src/Connapse.Web/Endpoints/DocumentsEndpoints.cs` | Modify |
| `src/Connapse.Web/Mcp/McpTools.cs` | Modify |
| `src/Connapse.Web/Services/UploadService.cs` | Modify |
| `tests/Connapse.Core.Tests/Utilities/PathUtilitiesTests.cs` | Modify |
| `tests/Connapse.Integration.Tests/` | Add/modify test classes |
