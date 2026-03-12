# API Surface Reference

> **Baseline from v0.3.2.** Compare this against the actual documentation for the release you're testing. New endpoints, CLI commands, MCP tools, or UI pages may have been added. Use this as a starting point for your feature inventory, not as the definitive list.

Quick reference for testable endpoints, CLI commands, and MCP tools.

## Auth Model

Connapse uses **three auth mechanisms** — understanding this is critical for writing correct tests:

1. **Cookie auth (Blazor Server)** — Used by the web UI. Login happens via the `/login` Blazor page, NOT a REST endpoint. The browser gets an auth cookie. Not scriptable via curl.
2. **PAT auth (`X-Api-Key` header)** — Used for API scripting and CLI. The primary scriptable auth for testing. PATs are created via the API (requires JWT first) or via the UI.
3. **Agent key auth (`X-Api-Key` header)** — Used by MCP clients. Agent keys have limited scope.

**The `/api/v1/auth/token` endpoint may not exist** in all versions. Connapse's primary auth is cookie-based Blazor. If this endpoint returns 404, all JWT-based tests must be adapted to use PAT auth instead. Always probe before assuming.

## Response Shapes (Critical for Test Scripts)

These shapes were verified in live testing and differ from what you might assume:

- **List endpoints** return paginated wrappers, NOT bare arrays:
  ```json
  {"items": [...], "totalCount": 5, "hasMore": false}
  ```
- **Pagination is REQUIRED** on all list endpoints: `?skip=0&take=50`. Omitting these may return 400 or incomplete results.
- **File upload** returns HTTP **200** (not 201). The response body contains `{batchId, documents: [{documentId, jobId}]}`.
- **Container stats** fields: `containerId`, `containerName`, `connectorType`, `documents`, `totalChunks`, `totalSizeBytes`, `embeddingModels`, `lastIndexedAt`, `createdAt`. (NOT `documentCount`/`fileCount`)
- **Search hits** have `fileName` inside `metadata` object: `hit.metadata.fileName` (not `hit.fileName`).
- **Container create** returns HTTP **201** with `{id, name, description, ...}`.
- **Container delete** returns HTTP **204** (empty). Delete of non-empty container returns **400** with error message.
- **Nonexistent settings category** returns **404** with descriptive error message.

## REST API Endpoints

### Auth (`/api/v1/auth`)
| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| POST | `/api/v1/auth/token` | None | Get JWT — **may return 404; probe first** |
| POST | `/api/v1/auth/token/refresh` | None | Refresh JWT — **may return 404** |
| GET | `/api/v1/auth/pats` | PAT/JWT | List PATs |
| POST | `/api/v1/auth/pats` | PAT/JWT | Create PAT |
| DELETE | `/api/v1/auth/pats/{id}` | Own/Admin | Revoke PAT |
| GET | `/api/v1/auth/users` | Admin | List users |
| PUT | `/api/v1/auth/users/{id}/roles` | Admin | Assign roles |

### Agents (`/api/v1/agents`)
| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| GET | `/api/v1/agents` | Admin | List agents |
| POST | `/api/v1/agents` | Admin | Create agent |
| GET | `/api/v1/agents/{id}` | Admin | Get agent |
| PUT | `/api/v1/agents/{id}/active` | Admin | Enable/disable — **docs say /status but actual is /active; verify** |
| DELETE | `/api/v1/agents/{id}` | Admin | Delete agent |
| GET | `/api/v1/agents/{id}/keys` | Admin | List agent keys |
| POST | `/api/v1/agents/{id}/keys` | Admin | Create agent key |
| DELETE | `/api/v1/agents/{agentId}/keys/{keyId}` | Admin | Revoke key |

### Containers (`/api/containers`)
| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| GET | `/api/containers?skip=0&take=50` | Viewer+ | List containers (pagination REQUIRED) |
| POST | `/api/containers` | Editor+ | Create container |
| GET | `/api/containers/{id}` | Viewer+ | Get container |
| DELETE | `/api/containers/{id}` | Editor+ | Delete (must be empty) |
| GET | `/api/containers/{id}/stats` | Viewer+ | Container statistics |
| GET | `/api/containers/{id}/settings` | Admin | Get container settings |
| PUT | `/api/containers/{id}/settings` | Admin | Update container settings |

### Files (`/api/containers/{id}/files`)
| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| POST | `/api/containers/{id}/files` | Editor+ | Upload files (multipart) |
| GET | `/api/containers/{id}/files` | Viewer+ | List files (optional ?path=) |
| GET | `/api/containers/{id}/files/{fileId}` | Viewer+ | Get file metadata |
| GET | `/api/containers/{id}/files/{fileId}/content` | Viewer+ | Get parsed text content |
| DELETE | `/api/containers/{id}/files/{fileId}` | Editor+ | Delete file |
| GET | `/api/containers/{id}/files/{fileId}/reindex-check` | Viewer+ | Check if reindex needed |

### Folders
| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| POST | `/api/containers/{id}/folders` | Editor+ | Create folder |
| DELETE | `/api/containers/{id}/folders?path=` | Editor+ | Delete folder (cascade) |

### Search
| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| GET | `/api/containers/{id}/search?q=&mode=&topK=&minScore=&path=` | Viewer+ | Search (GET) |
| POST | `/api/containers/{id}/search` | Viewer+ | Search (POST, with filters) |

### Reindex
| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| POST | `/api/containers/{id}/reindex` | Editor+ | Trigger container reindex |
| POST | `/api/settings/reindex` | Admin | Trigger global reindex |
| GET | `/api/settings/reindex/status` | Admin | Get reindex queue status |

### Settings (`/api/settings`)
| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| GET | `/api/settings/{category}` | Admin | Get settings |
| PUT | `/api/settings/{category}` | Admin | Update settings |
| POST | `/api/settings/test-connection` | Admin | Test provider connection |
| GET | `/api/settings/embedding-models` | Admin | List embedding models |
| GET | `/api/containers/{id}/search/models` | Admin | Container embedding models |

Categories: `embedding`, `chunking`, `search`, `llm`, `upload`, `awssso`, `azuread`

Test connection categories: `Embedding`, `Llm`, `AwsSso`, `AzureAd`, `CrossEncoder`

### Connector Endpoints
| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| POST | `/api/containers/test-connection` | Admin | Test connector config before creating |
| POST | `/api/containers/{id}/sync` | Editor+ | Trigger on-demand sync (cloud containers) |

### Batches
| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| GET | `/api/batches/{id}/status` | Viewer+ | Ingestion progress for batch |

### SignalR Hub
| Endpoint | Auth | Purpose |
|----------|------|---------|
| `/hubs/ingestion?access_token=<jwt>` | JWT | Real-time ingestion progress |

Subscribe: `SubscribeToJob(jobId)` → Listen: `IngestionProgress` events

### Cloud Identity (`/api/v1/auth/cloud`)
| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| POST | `/api/v1/auth/cloud/aws/device-auth` | Any user | Start AWS device flow |
| POST | `/api/v1/auth/cloud/aws/device-auth/poll` | Any user | Poll AWS device flow |
| GET | `/api/v1/auth/cloud/azure/connect` | Any user | Start Azure OAuth flow |
| GET | `/api/v1/auth/cloud/azure/callback` | — | Azure OAuth callback |
| GET | `/api/v1/auth/cloud` | Any user | List cloud identities |
| DELETE | `/api/v1/auth/cloud/{provider}` | Any user | Disconnect identity |

---

## CLI Commands

Based on release notes and README:

```
# Auth
connapse auth login [--url <url>]                    # Log in (prompts email+password, creates PAT)
connapse auth logout                                  # Remove stored credentials
connapse auth whoami                                  # Show current identity
connapse auth pat create "<name>" [--expires <date>]  # Create named PAT
connapse auth pat list                                # List PATs
connapse auth pat revoke <pat-guid>                   # Revoke PAT

# Containers
connapse container create <name> [--description "..."]
connapse container list
connapse container delete <name>

# Files
connapse upload <path> --container <name> [--destination /folder/] [--strategy Semantic]
connapse search "<query>" --container <name> [--mode Hybrid] [--top 10] [--path /folder/] [--min-score 0.5]
connapse reindex --container <name> [--force] [--no-detect-changes]

# General
connapse --help
connapse --version
```

CLI stores credentials at `~/.connapse/credentials.json` (PAT auto-injected as X-Api-Key).

Test each command and verify output format, error messages, and exit codes.

### Known CLI Bugs (as of v0.3.2-alpha)
- `container list` returns 400 — doesn't send pagination params (skip/take)
- `auth pat list` — PatListItem deserialization error
- `auth login` — interactive only (`Console.ReadKey()` throws on redirected stdin)
- USERPROFILE override doesn't work on Windows native binary (reads from registry)

---

## MCP Tools (11 total)

These are the tools exposed via the MCP server. Test via the MCP endpoint or by using the Connapse MCP tools if connected.

| Tool | Description | Write Guard? |
|------|-------------|--------------|
| `container_create` | Create container | No |
| `container_list` | List containers with doc counts | No |
| `container_delete` | Delete container | No |
| `container_stats` | Get container statistics | No |
| `upload_file` | Upload single file | Yes — blocked on S3/AzureBlob |
| `bulk_upload` | Upload up to 100 files | Yes |
| `list_files` | List files at path | No |
| `get_document` | Get full parsed text | No |
| `delete_file` | Delete single file | Yes |
| `bulk_delete` | Delete up to 100 files | Yes |
| `search_knowledge` | Semantic/Keyword/Hybrid search | No |

---

## UI Pages to Test

Navigate to each of these and verify they render and function:

| Page | URL Path | Key Elements |
|------|----------|--------------|
| Login | `/login` or `/` (if not authed) | Email/password form, submit button |
| Dashboard | `/` (authed) | Container list, create button |
| Container Detail | `/containers/{id}` | File browser, upload form, search |
| Search | (may be within container view) | Query input, mode selector, results |
| Settings | `/admin/settings` or similar | Category tabs, form fields, save |
| User Management | `/admin/users` | User list, invite button, role editor |
| Agent Management | `/admin/agents` | Agent list, create form, key management |
| Profile | `/profile` | PAT management, cloud identity linking |
| Audit Log | `/admin/audit` or similar | Event list with timestamps |

Note: Exact URL paths may vary. Use Playwright `browser_snapshot` to discover navigation elements.

---

## Test Data Templates

### Markdown test file
```markdown
# Test Document: Distributed Systems

Circuit breakers prevent cascading failures in microservice architectures.
The bulkhead pattern isolates components to contain failures.
Retry policies with exponential backoff handle transient errors gracefully.
```

### Text test file
```
This is a plain text file for testing ingestion.
It contains simple content that should be searchable.
Keywords: testing, ingestion, plain text, searchable content.
```

Use these as baseline test documents. Create 3-5 files with distinct topics to validate search relevance.
