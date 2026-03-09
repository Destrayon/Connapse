# API Reference

Connapse exposes multiple API surfaces for programmatic access:

1. **REST API** — HTTP endpoints for containers, files, search, auth, and settings
2. **MCP (Model Context Protocol)** — Tool interface for AI agents
3. **CLI** — Command-line interface (`connapse`)

## REST API

Base URL (development): `https://localhost:5001`

All endpoints return JSON. Errors follow RFC 7807 Problem Details format.

### Authentication

Connapse uses a **three-tier authentication** system:

| Tier | Header / Mechanism | Used By | Lifetime |
|------|--------------------|---------|----------|
| **Cookie** | ASP.NET Core Identity cookie | Blazor UI (browser sessions) | 14-day sliding |
| **PAT** | `X-Api-Key: cnp_<token>` | CLI, REST API clients, automation | Until revoked |
| **JWT Bearer** | `Authorization: Bearer <token>` | SDK clients, external integrations | 60-90 min + refresh |

**Role-based access control (RBAC)**:

| Role | Permissions |
|------|-------------|
| **Admin** | Full access: all endpoints, user management, settings, agents |
| **Editor** | Upload, delete, search, manage containers and folders |
| **Viewer** | Search and browse only (no writes) |
| **Agent** | MCP tool access only (search + ingest via API keys) |

**First-time setup**: On a fresh install with no users, visit the login page — it automatically shows an admin account creation form. The first user becomes the system admin.

**Subsequent users** are invite-only. Admins create invitations at `/admin/users`. Invited users visit `/register?token=<token>` to set their password.

---

## Auth API

Base path: `/api/v1/auth`

### Get JWT Token

Exchange email + password for a JWT access/refresh token pair.

**Endpoint**: `POST /api/v1/auth/token`

**Request Body**:
```json
{
  "email": "admin@example.com",
  "password": "YourSecurePassword"
}
```

**Response** (200 OK):
```json
{
  "accessToken": "eyJhbGci...",
  "refreshToken": "dGhpcyBpcyBhIHJlZnJlc2g...",
  "expiresIn": 5400,
  "tokenType": "Bearer"
}
```

**Response** (401 Unauthorized): Invalid credentials.

**Notes**:
- Use the `accessToken` as `Authorization: Bearer <token>` on subsequent requests
- `refreshToken` can be exchanged for a new token pair before expiry
- Updates `LastLoginAt` on the user record + writes audit log entry

---

### Refresh JWT Token

Exchange a valid refresh token for a new access/refresh token pair.

**Endpoint**: `POST /api/v1/auth/token/refresh`

**Request Body**:
```json
{
  "refreshToken": "dGhpcyBpcyBhIHJlZnJlc2g..."
}
```

**Response** (200 OK): Same as `POST /api/v1/auth/token`.

**Response** (401 Unauthorized): Token invalid, expired, or already revoked.

**Notes**: Old refresh token is revoked on use (rotation). Each call issues a new pair.

---

### List Personal Access Tokens

**Endpoint**: `GET /api/v1/auth/pats`

**Auth**: Required (any authenticated user)

**Response** (200 OK):
```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "name": "CLI (LAPTOP-01)",
    "prefix": "cnp_abc123",
    "lastUsedAt": "2026-02-26T10:00:00Z",
    "expiresAt": null,
    "isRevoked": false,
    "createdAt": "2026-02-20T09:00:00Z"
  }
]
```

---

### Create Personal Access Token

**Endpoint**: `POST /api/v1/auth/pats`

**Auth**: Required (any authenticated user)

**Request Body**:
```json
{
  "name": "My Script Token",
  "expiresAt": "2027-01-01T00:00:00Z"
}
```

**Fields**:
- `name` (required): Display label
- `expiresAt` (optional): ISO 8601 datetime; if omitted, token never expires

**Response** (200 OK):
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "My Script Token",
  "token": "cnp_<random-64-chars>",
  "expiresAt": "2027-01-01T00:00:00Z"
}
```

> **Important**: The `token` value is shown **once only**. Store it securely immediately.

---

### Revoke Personal Access Token

**Endpoint**: `DELETE /api/v1/auth/pats/{id}`

**Auth**: Required (own tokens only; admins can revoke any)

**Response** (204 No Content)

---

### List Users (Admin)

**Endpoint**: `GET /api/v1/auth/users`

**Auth**: Admin role required

**Response** (200 OK):
```json
[
  {
    "id": "user-guid",
    "email": "viewer@example.com",
    "roles": ["Viewer"],
    "isSystemAdmin": false,
    "lastLoginAt": "2026-02-25T14:30:00Z",
    "createdAt": "2026-02-20T09:00:00Z"
  }
]
```

---

### Assign User Roles (Admin)

**Endpoint**: `PUT /api/v1/auth/users/{id}/roles`

**Auth**: Admin role required

**Request Body**:
```json
{
  "roles": ["Editor"]
}
```

**Notes**:
- `Owner` role cannot be assigned or removed via this endpoint
- `Agent` role cannot be assigned to user accounts (use Agent API instead)

**Response** (200 OK)

---

## Agents API

Base path: `/api/v1/agents`

All agent endpoints require **Admin** role.

Agents are non-human identities (CI/CD pipelines, automation scripts) that authenticate via API keys and access the MCP server.

### List Agents

**Endpoint**: `GET /api/v1/agents`

**Response** (200 OK):
```json
[
  {
    "id": "agent-guid",
    "name": "CI Pipeline",
    "description": "Indexes docs on every merge",
    "isEnabled": true,
    "activeKeyCount": 2,
    "createdAt": "2026-02-20T09:00:00Z"
  }
]
```

---

### Create Agent

**Endpoint**: `POST /api/v1/agents`

**Request Body**:
```json
{
  "name": "CI Pipeline",
  "description": "Indexes docs on every merge"
}
```

**Response** (200 OK): Returns the created `Agent` object.

---

### Get Agent

**Endpoint**: `GET /api/v1/agents/{id}`

**Response** (200 OK | 404 Not Found)

---

### Enable/Disable Agent

**Endpoint**: `PUT /api/v1/agents/{id}/status`

**Request Body**:
```json
{ "isEnabled": false }
```

**Response** (200 OK)

---

### Delete Agent

**Endpoint**: `DELETE /api/v1/agents/{id}`

Deletes the agent and all its API keys.

**Response** (204 No Content)

---

### List Agent API Keys

**Endpoint**: `GET /api/v1/agents/{id}/keys`

**Response** (200 OK):
```json
[
  {
    "id": "key-guid",
    "name": "Production Key",
    "prefix": "cnp_abc123",
    "lastUsedAt": "2026-02-26T08:00:00Z",
    "isRevoked": false,
    "createdAt": "2026-02-20T09:00:00Z"
  }
]
```

---

### Create Agent API Key

**Endpoint**: `POST /api/v1/agents/{id}/keys`

**Request Body**:
```json
{ "name": "Production Key" }
```

**Response** (200 OK):
```json
{
  "id": "key-guid",
  "name": "Production Key",
  "token": "cnp_<random-64-chars>"
}
```

> **Important**: The `token` is shown **once only**. Store it securely immediately.

---

### Revoke Agent API Key

**Endpoint**: `DELETE /api/v1/agents/{agentId}/keys/{keyId}`

**Response** (204 No Content)

---

## Containers API

All container endpoints require authentication. RBAC rules:
- `GET` endpoints require **Viewer** role minimum
- `POST`, `DELETE`, reindex require **Editor** role minimum
- Settings endpoints require **Admin** role

### Create Container

**Endpoint**: `POST /api/containers`

**Request Body**:
```json
{
  "name": "my-project",
  "description": "Project knowledge base",
  "connectorType": "MinIO",
  "connectorConfig": null
}
```

**Fields**:
- `name` (required): lowercase alphanumeric + hyphens, 2-128 chars, globally unique
- `description` (optional): Container description
- `connectorType` (optional, default: `MinIO`): `MinIO` | `Filesystem` | `S3` | `AzureBlob`
- `connectorConfig` (conditional): JSON string with connector-specific config

**Connector Config Requirements**:
| Connector | Required Fields | Example |
|-----------|----------------|---------|
| MinIO | None (global config) | — |
| Filesystem | `rootPath` | `{"rootPath":"C:\\docs"}` |
| S3 | `bucketName`, `region` | `{"bucketName":"docs","region":"us-east-1"}` |
| AzureBlob | `storageAccountName`, `containerName` | `{"storageAccountName":"acct","containerName":"docs"}` |

**Response** (201 Created):
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "my-project",
  "description": "Project knowledge base",
  "connectorType": "MinIO",
  "documentCount": 0,
  "createdAt": "2026-02-26T10:00:00Z",
  "updatedAt": "2026-02-26T10:00:00Z"
}
```

---

### List Containers

**Endpoint**: `GET /api/containers`

**Response** (200 OK): Array of `Container` objects (see above).

---

### Get Container

**Endpoint**: `GET /api/containers/{id}`

**Response** (200 OK | 404 Not Found)

---

### Delete Container

**Endpoint**: `DELETE /api/containers/{id}`

**Notes**: Fails with 400 if the container still has files or folders. Must be empty first.

**Response** (204 No Content | 400 Bad Request)

---

### Get Container Stats

**Endpoint**: `GET /api/containers/{id}/stats`

**Auth**: Viewer minimum

**Response** (200 OK):
```json
{
  "containerId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "containerName": "my-project",
  "connectorType": "MinIO",
  "documents": {
    "total": 42,
    "ready": 40,
    "processing": 1,
    "failed": 1
  },
  "totalChunks": 1200,
  "totalSizeBytes": 52428800,
  "embeddingModels": [
    { "modelId": "nomic-embed-text", "dimensions": 768, "vectorCount": 1200 }
  ],
  "lastIndexedAt": "2026-03-09T10:00:00Z",
  "createdAt": "2026-02-26T10:00:00Z"
}
```

---

## Container Files API

### Upload Files

Upload one or more files into a container.

**Endpoint**: `POST /api/containers/{id}/files`

**Content-Type**: `multipart/form-data`

**Form Fields**:
- `files` (required): One or more file parts
- `path` (optional): Destination folder path (e.g., `/docs/2026/`)
- `strategy` (optional): Chunking strategy (`Semantic` | `FixedSize` | `Recursive`, default: `Semantic`)

**Response** (200 OK):
```json
{
  "documents": [
    {
      "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "fileName": "report.pdf",
      "fileSize": 245678,
      "contentType": "application/pdf",
      "status": "Pending",
      "error": null
    }
  ],
  "successCount": 1,
  "failureCount": 0
}
```

**Notes**:
- Files stream directly to MinIO (not buffered in memory)
- Ingestion is asynchronous — track progress via SignalR or poll the file endpoint
- Duplicate filenames in the same folder auto-increment: `file (1).pdf`, `file (2).pdf`
- **Write guard**: S3 and AzureBlob containers block uploads (read-only). Filesystem containers respect the `allowUpload` flag. Returns `400` with `{ "error": "write_denied" }` when blocked. See [connectors.md — Write Guards](connectors.md#write-guards).

---

### List Files

**Endpoint**: `GET /api/containers/{id}/files`

**Query Parameters**:
- `path` (optional): Browse a specific folder (e.g., `?path=/docs/`)

**Response** (200 OK): Array of file and folder entries.

---

### Get File

**Endpoint**: `GET /api/containers/{id}/files/{fileId}`

Returns file metadata including current indexing status (`Pending`, `Processing`, `Ready`, `Failed`).

---

### Get File Content

**Endpoint**: `GET /api/containers/{id}/files/{fileId}/content`

**Auth**: Viewer minimum

Returns the full text content of a file. For text files, the original content is returned. For binary formats (PDF, DOCX, PPTX), the extracted text is returned.

**Content Negotiation**:
- `Accept: text/plain` — returns raw text content only
- `Accept: application/json` (default) — returns structured response with metadata

**Response** (200 OK, JSON):
```json
{
  "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "fileName": "report.pdf",
  "path": "/docs/report.pdf",
  "contentType": "application/pdf",
  "sizeBytes": 245678,
  "createdAt": "2026-02-26T10:00:00Z",
  "content": "Machine learning is a subset of artificial intelligence..."
}
```

**Errors**:
- 400 `document_not_ready` — file is still being ingested
- 400 `document_failed` — file failed ingestion
- 400 `no_parser` — no parser available for the file type
- 404 — file or container not found

---

### Check Reindex Status

**Endpoint**: `GET /api/containers/{id}/files/{fileId}/reindex-check`

Returns whether the file needs reindexing and the reason.

---

### Delete File

**Endpoint**: `DELETE /api/containers/{id}/files/{fileId}`

Cascade deletes chunks, vectors, and removes the file from MinIO.

**Write guard**: S3 and AzureBlob containers block deletes. Filesystem containers respect the `allowDelete` flag. Returns `400` with `{ "error": "write_denied" }` when blocked. See [connectors.md — Write Guards](connectors.md#write-guards).

**Response** (204 No Content)

---

## Container Folders API

### Create Folder

**Endpoint**: `POST /api/containers/{id}/folders`

**Request Body**:
```json
{ "path": "/docs/2026/" }
```

**Write guard**: S3 and AzureBlob containers block folder creation. Filesystem containers respect the `allowCreateFolder` flag. Returns `400` with `{ "error": "write_denied" }` when blocked.

**Response** (200 OK): Returns `Folder` object.

---

### Delete Folder

**Endpoint**: `DELETE /api/containers/{id}/folders?path=/docs/2026/`

Cascade deletes all nested files, subfolders, chunks, vectors, and MinIO objects.

**Write guard**: S3 and AzureBlob containers block folder deletion. Filesystem containers respect the `allowDelete` flag. Returns `400` with `{ "error": "write_denied" }` when blocked.

**Response** (204 No Content)

---

## Container Search API

### Search (GET)

**Endpoint**: `GET /api/containers/{id}/search`

**Query Parameters**:
- `q` (required): Search query
- `mode` (default: `Hybrid`): `Semantic` | `Keyword` | `Hybrid`
- `topK` (default: `10`): Number of results (max: 100)
- `minScore` (optional): Minimum similarity score 0.0-1.0 (default: from settings, typically 0.5)
- `path` (optional): Restrict search to a folder subtree

**Response** (200 OK):
```json
{
  "hits": [
    {
      "chunkId": "7f8e9d1c-2b3a-4c5d-8e9f-1a2b3c4d5e6f",
      "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "content": "Machine learning is a subset of artificial intelligence...",
      "score": 0.92,
      "metadata": {
        "fileName": "report.pdf",
        "chunkIndex": "5",
        "pageNumber": "12"
      }
    }
  ],
  "totalMatches": 1,
  "durationMs": 87
}
```

---

### Search (POST)

**Endpoint**: `POST /api/containers/{id}/search`

**Request Body**:
```json
{
  "query": "machine learning algorithms",
  "mode": "Hybrid",
  "topK": 20,
  "minScore": 0.6,
  "path": "/docs/",
  "filters": {
    "documentType": "pdf"
  }
}
```

**Response**: Same as GET search.

---

## Container Reindex API

### Reindex Container

**Endpoint**: `POST /api/containers/{id}/reindex`

**Request Body** (optional):
```json
{
  "force": false,
  "detectSettingsChanges": true,
  "documentIds": null
}
```

**Fields**:
- `force` (default: `false`): Re-process all files even if content hash unchanged
- `detectSettingsChanges` (default: `true`): Force reindex if embedding/chunking settings changed
- `documentIds` (optional): Reindex specific files only

**Response** (202 Accepted):
```json
{
  "batchId": "9d3e7c1a-4b2f-4e8d-9c5a-6f8e1b3d7c2a",
  "totalDocuments": 42,
  "enqueuedCount": 38,
  "skippedCount": 4,
  "status": "Pending"
}
```

---

## Settings API

All settings endpoints require **Admin** role.

### Get Settings

**Endpoint**: `GET /api/settings/{category}`

**Categories**: `embedding` | `chunking` | `search` | `llm` | `upload` | `awssso` | `azuread`

**Response** (200 OK) — Example for `embedding`:
```json
{
  "provider": "Ollama",
  "baseUrl": "http://ollama:11434",
  "model": "nomic-embed-text",
  "dimensions": 768,
  "timeout": 30,
  "batchSize": 4
}
```

---

### Update Settings

**Endpoint**: `PUT /api/settings/{category}`

**Notes**: Changes take effect immediately (live reload via `IOptionsMonitor<T>`). No restart required.

**Response** (200 OK):
```json
{
  "message": "Settings updated successfully",
  "category": "chunking",
  "updatedAt": "2026-02-26T11:45:00Z"
}
```

---

### Test Connection

**Endpoint**: `POST /api/settings/test-connection`

**Categories**: `Embedding` | `Llm` | `AwsSso` | `AzureAd` | `CrossEncoder`

**Request Body**:
```json
{
  "category": "Embedding",
  "settings": {
    "provider": "Ollama",
    "baseUrl": "http://localhost:11434",
    "model": "nomic-embed-text"
  },
  "timeoutSeconds": 10
}
```

**Response** (200 OK):
```json
{
  "success": true,
  "message": "Connected to Ollama (3 models available)",
  "details": { "modelCount": 3 },
  "duration": "00:00:00.4530000"
}
```

---

## Batches API

### Get Batch Status

**Endpoint**: `GET /api/batches/{id}/status`

**Auth**: Viewer minimum

**Response** (200 OK): Ingestion progress for all jobs in the batch.

---

## SignalR Hub (Real-Time)

**Hub URL**: `/hubs/ingestion`

**Auth**: Required — pass JWT via `?access_token=<token>` query string, or use cookie session.

### Subscribe to Job Progress

```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/ingestion?access_token=eyJ...")
  .build();

// Subscribe
await connection.invoke("SubscribeToJob", "job-id");

// Listen for updates
connection.on("IngestionProgress", (update) => {
  // update: { jobId, state, currentPhase, percentComplete, errorMessage, startedAt, completedAt }
  console.log(`${update.currentPhase}: ${update.percentComplete}%`);
});

await connection.start();
```

**States**: `Pending` | `Processing` | `Completed` | `Failed`

**Phases**: `Parsing` | `Chunking` | `Embedding` | `Storing` | `Complete`

---

## Model Context Protocol (MCP)

Connapse exposes an MCP server for AI agent integration.

**Endpoint**: `POST /mcp`

**Auth**: Requires a valid **Agent** API key via `X-Api-Key: cnp_<token>` header.

**Tool list**: `GET /mcp/tools`

### Available Tools (11)

| Tool | Required Parameters | Optional Parameters | Description |
|------|---------------------|---------------------|-------------|
| `container_create` | `name` | `description` | Create a new container for organizing files |
| `container_list` | — | — | List all containers with document counts |
| `container_delete` | `containerId` | — | Delete a container (must be empty for MinIO) |
| `container_stats` | `containerId` | — | Get statistics: document counts by status, chunk count, storage size, embedding model, last indexed time |
| `upload_file` | `containerId`, `fileName`, + `content` or `textContent` | `path`, `strategy` | Upload a single file (base64 or raw text) |
| `bulk_upload` | `containerId`, `files` (JSON array) | — | Upload up to 100 files in one operation |
| `list_files` | `containerId` | `path` | List files and folders at a given path |
| `get_document` | `containerId`, `fileId` | — | Retrieve full parsed text content by document ID or path |
| `delete_file` | `containerId`, `fileId` | — | Delete a file and its associated chunks and vectors |
| `bulk_delete` | `containerId`, `fileIds` (JSON array) | — | Delete up to 100 files in one operation |
| `search_knowledge` | `query`, `containerId` | `mode`, `topK`, `path`, `minScore` | Semantic, keyword, or hybrid search within a container |

**Notes**:
- `containerId` accepts either a container GUID or container name (resolved automatically).
- `upload_file` accepts either `content` (base64) for binary files or `textContent` (raw text) for text files — provide one, not both.
- `bulk_upload` `files` is a JSON array of objects: `{"filename":"name.txt", "content":"...", "encoding":"text|base64", "folderPath":"/optional/"}`.
- `bulk_delete` `fileIds` is a JSON array of document ID strings: `["id1","id2"]`.
- `get_document` `fileId` accepts either a document UUID or a virtual path (e.g., `/docs/readme.md`).

### Write Guards

Write operations (`upload_file`, `bulk_upload`, `delete_file`, `bulk_delete`) are subject to container write guards:

| Connector Type | Upload | Delete | Notes |
|---------------|--------|--------|-------|
| **MinIO** | Allowed | Allowed | Default connector; full read/write |
| **InMemory** | Allowed | Allowed | Ephemeral storage |
| **Filesystem** | Configurable | Configurable | Per-container `allowUpload`/`allowDelete` flags (default: allowed) |
| **S3** | Blocked | Blocked | Read-only; files are synced from the source bucket |
| **AzureBlob** | Blocked | Blocked | Read-only; files are synced from the source container |

When a write is blocked, the tool returns an error message explaining why (e.g., "S3 containers are read-only. Files are synced from the source.").

---

## CLI

The `connapse` CLI is a self-contained tool for managing Connapse from the command line.

### Installation

**Option A: .NET Global Tool** (requires .NET 10):
```bash
dotnet tool install -g Connapse.CLI
```

**Option B: Native Binary** (no .NET required):
Download the self-contained binary for your platform from [GitHub Releases](https://github.com/Destrayon/Connapse/releases):
- `connapse-win-x64.exe`
- `connapse-linux-x64`
- `connapse-osx-x64`
- `connapse-osx-arm64`

Binaries are published automatically on version tags via GitHub Actions.

---

### Authentication Commands

Before using other CLI commands, authenticate to store credentials locally (`~/.connapse/credentials.json`).

```bash
# Log in — prompts for email + password, creates a PAT, saves credentials
connapse auth login [--url https://your-server.com]

# Log out — removes stored credentials
connapse auth logout

# Show current identity
connapse auth whoami

# Create a named PAT (displayed once)
connapse auth pat create "CI Token" [--expires 2027-01-01]

# List all your PATs
connapse auth pat list

# Revoke a PAT by ID
connapse auth pat revoke <pat-guid>
```

**Login flow**: `connapse auth login` prompts for email + password, calls `POST /api/v1/auth/token`, then creates a PAT named `CLI ({MachineName})` via `POST /api/v1/auth/pats`. The PAT token is stored locally and auto-injected into all subsequent API requests as `X-Api-Key`.

---

### Container Commands

```bash
# Create a container
connapse container create <name> [--description "..."]

# List all containers
connapse container list

# Delete an empty container
connapse container delete <name>
```

---

### File Commands

```bash
# Upload a file or folder into a container
connapse upload <path> --container <name> [--destination /folder/] [--strategy Semantic]

# Search within a container
connapse search "<query>" --container <name> [--mode Hybrid] [--top 10] [--path /folder/] [--min-score 0.5]

# Reindex a container
connapse reindex --container <name> [--force] [--no-detect-changes]
```

---

## Error Codes

All API errors follow RFC 7807 Problem Details format.

| Status | Description |
|--------|-------------|
| 400 | Invalid request parameters or body |
| 401 | Missing or invalid authentication |
| 403 | Insufficient role/permissions |
| 404 | Resource not found |
| 409 | Conflict (duplicate name, already revoked, etc.) |
| 413 | File exceeds `UploadSettings.MaxFileSizeMb` |
| 415 | File type not in `UploadSettings.AllowedExtensions` |
| 429 | Ingestion queue full |
| 500 | Unexpected server error |
| 503 | External service (Ollama, MinIO) unreachable |

---

## Container Connector Endpoints (v0.3.0)

### Test Connector Connection

Test a cloud connector configuration before creating a container.

**Endpoint**: `POST /api/containers/test-connection`

**Request Body**:
```json
{
  "connectorType": "S3",
  "connectorConfig": "{\"bucketName\":\"docs\",\"region\":\"us-east-1\"}",
  "timeoutSeconds": 15
}
```

**Response** (200 OK):
```json
{
  "success": true,
  "message": "Connected successfully",
  "details": { "bucketName": "docs", "region": "us-east-1", "objectsFound": 3 },
  "elapsed": "00:00:01.234"
}
```

**Supported connectors**: S3, AzureBlob, MinIO.

---

### Sync Container

Trigger an on-demand sync for cloud containers.

**Endpoint**: `POST /api/containers/{id}/sync`

**Notes**:
- Returns 400 for Filesystem ("live watch is enough")
- Returns 404 for non-existent containers
- Cloud scope enforcement: user must have a linked identity for the container's cloud provider

**Response** (200 OK):
```json
{
  "batchId": "abc123",
  "totalFiles": 42,
  "enqueuedCount": 5,
  "skippedCount": 37
}
```

---

## Container Settings Endpoints (v0.3.0)

### Get Container Settings

**Endpoint**: `GET /api/containers/{id}/settings`

Returns per-container settings overrides. Null values mean "use global setting".

**Response** (200 OK):
```json
{
  "chunking": null,
  "embedding": { "provider": "OpenAI", "model": "text-embedding-3-small", "dimensions": 1536 },
  "search": null,
  "upload": null
}
```

---

### Save Container Settings

**Endpoint**: `PUT /api/containers/{id}/settings`

**Request Body**:
```json
{
  "embedding": { "provider": "OpenAI", "model": "text-embedding-3-small", "dimensions": 1536 }
}
```

Null or omitted categories are cleared (reset to global).

**Response** (200 OK)

---

## Embedding Model Endpoints (v0.3.0)

### Get Embedding Models

Discover which embedding models have vectors in the database.

**Endpoint**: `GET /api/settings/embedding-models`

**Response** (200 OK):
```json
{
  "currentModel": "nomic-embed-text",
  "models": [
    { "modelId": "nomic-embed-text", "dimensions": 768, "vectorCount": 1200 },
    { "modelId": "text-embedding-3-small", "dimensions": 1536, "vectorCount": 50 }
  ]
}
```

---

### Get Container Embedding Models

**Endpoint**: `GET /api/containers/{id}/search/models`

Same response as above, but scoped to a specific container.

---

### Trigger Reindex

**Endpoint**: `POST /api/settings/reindex`

Fire-and-forget reindex with settings change detection.

**Response** (202 Accepted)

---

### Get Reindex Status

**Endpoint**: `GET /api/settings/reindex/status`

**Response** (200 OK):
```json
{
  "queueDepth": 12,
  "isActive": true
}
```

---

## Cloud Identity Endpoints (v0.3.0)

Base path: `/api/v1/auth/cloud`

All endpoints require authentication.

### Start AWS Device Authorization

**Endpoint**: `POST /api/v1/auth/cloud/aws/device-auth`

Returns a user code and verification URL for the IAM Identity Center device auth flow.

**Response** (200 OK):
```json
{
  "userCode": "ABCD-EFGH",
  "verificationUri": "https://device.sso.us-east-1.amazonaws.com/",
  "verificationUriComplete": "https://device.sso.us-east-1.amazonaws.com/?user_code=ABCD-EFGH",
  "deviceCode": "...",
  "expiresInSeconds": 600,
  "intervalSeconds": 5
}
```

**Error** (400): `aws_sso_not_configured` if admin hasn't set Issuer URL + Region.

---

### Poll AWS Device Authorization

**Endpoint**: `POST /api/v1/auth/cloud/aws/device-auth/poll`

**Request Body**:
```json
{ "deviceCode": "..." }
```

**Response** (200 OK — pending):
```json
{ "status": "pending" }
```

**Response** (200 OK — complete):
```json
{
  "status": "complete",
  "identity": {
    "id": "...",
    "provider": "AWS",
    "data": { "principalArn": "123456789012", "accountId": "123456789012", "displayName": "My Account" },
    "createdAt": "...",
    "lastUsedAt": null
  }
}
```

---

### Get Azure Connect URL

**Endpoint**: `GET /api/v1/auth/cloud/azure/connect`

Redirects to Azure AD authorize endpoint with PKCE challenge. Sets `__connapse_az_state` and `__connapse_az_pkce` cookies (10-min TTL).

**Response**: 302 Redirect to Azure AD.

---

### Azure Callback

**Endpoint**: `GET /api/v1/auth/cloud/azure/callback`

Handles the OAuth2 callback from Azure AD. Validates state, exchanges code for ID token, stores identity.

**Response**: 302 Redirect to `/profile`.

---

### List Cloud Identities

**Endpoint**: `GET /api/v1/auth/cloud`

**Response** (200 OK): Array of linked cloud identities.

---

### Disconnect Cloud Identity

**Endpoint**: `DELETE /api/v1/auth/cloud/{provider}`

**Path Parameters**: `provider` = `AWS` | `Azure`

Removes the linked identity and evicts cached scope entries.

**Response** (204 No Content | 404 Not Found)

---

## Versioning

**Current Version**: `v0.3.0`

Auth endpoints are under `/api/v1/auth/` and `/api/v1/agents/`. Container endpoints remain under `/api/containers/`. Cloud identity endpoints are under `/api/v1/auth/cloud/`.

---

## References

- [architecture.md](architecture.md) — System architecture and design
- [deployment.md](deployment.md) — Deployment and configuration
- [.claude/state/api-surface.md](../.claude/state/api-surface.md) — Internal interface documentation
