# API Reference

> Part of [Connapse](https://github.com/Destrayon/Connapse) — open-source AI knowledge management platform.

Connapse exposes multiple API surfaces for programmatic access:

1. **REST API** — HTTP endpoints for containers, files, search, auth, and settings
2. **MCP (Model Context Protocol)** — Tool interface for AI agents
3. **CLI** — Command-line interface (`connapse`)

## REST API

Base URL (development): `https://localhost:5001`

All endpoints return JSON. Errors follow RFC 7807 Problem Details format.

### Authentication

Connapse uses a **multi-tier authentication** system:

| Tier | Header / Mechanism | Used By | Lifetime |
|------|--------------------|---------|----------|
| **Cookie** | ASP.NET Core Identity cookie | Blazor UI (browser sessions) | 14-day sliding |
| **OAuth 2.1** | `Authorization: Bearer <token>` (via PKCE) | CLI (browser-based login) | 60-90 min + refresh token |
| **PAT** | `X-Api-Key: cnp_<token>` | REST API clients, scripts, automation | Until revoked |
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

### Pagination

All list endpoints use paginated responses. Pagination parameters are **required** — omitting them returns 400.

**Query Parameters**:

| Parameter | Type | Default | Constraints |
|-----------|------|---------|-------------|
| `skip` | int | 0 | min: 0 |
| `take` | int | 50 | min: 1, max: 200 |

**Response Envelope** (`PagedResponse<T>`):

```json
{
  "items": [ ... ],
  "totalCount": 142,
  "hasMore": true
}
```

| Field | Description |
|-------|-------------|
| `items` | Array of results for the current page |
| `totalCount` | Total number of matching records |
| `hasMore` | `true` if more records exist beyond `skip + take` |

**Paginated endpoints**: `GET /api/containers`, `GET /api/containers/{id}/files`, `GET /api/v1/auth/users`.

**Example**: `GET /api/containers?skip=0&take=20`

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
- `scopes` (optional): what the token may do, such as `knowledge:read`. A token passes a scope check only
  with that scope: one issued with none passes none, whatever its owner's role allows. It still reaches the
  REST routes that check only the owner's role.

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

### OAuth 2.1 Endpoints

Connapse implements OAuth 2.1 with PKCE for CLI and third-party client authentication. These endpoints follow [RFC 6749](https://datatracker.ietf.org/doc/html/rfc6749) and [RFC 7636](https://datatracker.ietf.org/doc/html/rfc7636).

#### Discovery

| Endpoint | Purpose |
|----------|---------|
| `GET /.well-known/oauth-protected-resource` | Protected resource metadata (resource URL, supported scopes) |
| `GET /.well-known/oauth-authorization-server` | Authorization server metadata (endpoints, supported grants, PKCE methods) |

#### CLI Client Metadata

`GET /oauth/clients/cli.json` — Static metadata document for the built-in CLI client. Returns the client ID, allowed redirect URIs, and grant types.

#### Authorization

`GET /oauth/authorize` — Renders the OAuth consent page. The user authenticates (or is already authenticated via cookie) and approves the requested scopes.

**Query Parameters**:

| Parameter | Required | Description |
|-----------|----------|-------------|
| `response_type` | Yes | Must be `code` |
| `client_id` | Yes | Client ID (URL to metadata document, or registered ID) |
| `redirect_uri` | Yes | Must match a registered redirect URI |
| `code_challenge` | Yes | PKCE code challenge (S256) |
| `code_challenge_method` | Yes | Must be `S256` |
| `state` | Recommended | CSRF protection value |
| `scope` | Optional | Space-separated scopes (e.g., `knowledge:read knowledge:write`) |
| `resource` | Optional | Resource indicator (RFC 8707) |

#### Token Exchange

`POST /oauth/token` — Exchange an authorization code or refresh token for an access token.

**Content-Type**: `application/x-www-form-urlencoded` (required)

**Grant type `authorization_code`**:

| Parameter | Required | Description |
|-----------|----------|-------------|
| `grant_type` | Yes | `authorization_code` |
| `code` | Yes | Authorization code from the consent page redirect |
| `redirect_uri` | Yes | Must match the original authorize request |
| `client_id` | Yes | Client ID |
| `code_verifier` | Yes | PKCE code verifier |

**Grant type `refresh_token`**:

| Parameter | Required | Description |
|-----------|----------|-------------|
| `grant_type` | Yes | `refresh_token` |
| `refresh_token` | Yes | The refresh token to exchange |
| `client_id` | Optional | If provided, must match the original grant's client |

**Response** (200 OK):
```json
{
  "access_token": "eyJhbGci...",
  "token_type": "bearer",
  "expires_in": 5400,
  "refresh_token": "new-refresh-token...",
  "scope": "knowledge:read knowledge:write"
}
```

**Notes**:
- Refresh tokens are rotated on each use (old token is revoked)
- Replay detection: reusing a consumed refresh token revokes the entire token family
- The `resource` parameter in the authorize request is validated against the server's base URL

#### Dynamic Client Registration

`POST /oauth/register` — Register a new OAuth client dynamically.

**Request Body**:
```json
{
  "client_name": "My App",
  "redirect_uris": ["http://127.0.0.1:9999/callback"],
  "application_type": "native"
}
```

**Response** (201 Created):
```json
{
  "client_id": "generated-client-id",
  "client_name": "My App",
  "redirect_uris": ["http://127.0.0.1:9999/callback"],
  "grant_types": ["authorization_code", "refresh_token"],
  "application_type": "native",
  "token_endpoint_auth_method": "none"
}
```

**Supported scopes**: `knowledge:read`, `knowledge:write`

---

### List Users (Admin)

**Endpoint**: `GET /api/v1/auth/users?skip=0&take=50`

**Auth**: Admin role required

**Query Parameters**: `skip`, `take` (see [Pagination](#pagination))

**Response** (200 OK):
```json
{
  "items": [
    {
      "id": "user-guid",
      "email": "viewer@example.com",
      "roles": ["Viewer"],
      "isSystemAdmin": false,
      "lastLoginAt": "2026-02-25T14:30:00Z",
      "createdAt": "2026-02-20T09:00:00Z"
    }
  ],
  "totalCount": 1,
  "hasMore": false
}
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

Agents are non-human identities (CI/CD pipelines, automation scripts) that authenticate via API keys and access the MCP server. For details on what agent API keys can access, see [Agent Permissions and Scope](#agent-permissions-and-scope).

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

**Endpoint**: `PUT /api/v1/agents/{id}/active`

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

### Agent Permissions and Scope

Agents are **MCP-only identities**. They authenticate with `cnp_`-prefixed API keys and interact with Connapse exclusively through the MCP endpoint. Agent keys **cannot** access REST API endpoints.

#### Access surface

| Interface | Access | Details |
|-----------|--------|---------|
| **MCP** (`POST /mcp`) | Allowed | All 11 MCP tools available |
| **REST API** (`/api/v1/*`) | **403 Forbidden** | Agent role is not included in REST API authorization policies (Viewer/Editor/Admin) |
| **Web UI** | Not accessible | Agent keys are API-only |

This is by design. Agents interact exclusively through the MCP protocol, which provides a structured tool interface suited to non-human consumers. REST API endpoints use role-based policies (Viewer, Editor, Admin) that do not include the Agent role.

#### Scopes

An agent key holds the scopes it was issued with, and nothing else: one issued with none can call no MCP
tool. These scopes govern **MCP tool behavior only** — they do not grant REST API access.

| Scope | Grants |
|-------|--------|
| `knowledge:read` | Search and read documents across all containers via MCP |
| `agent:ingest` | Upload and delete files in containers via MCP |

Agents have **unrestricted access to all containers** in the system. There is no per-container access control for agent API keys.

#### MCP tool access

Agents interact with Connapse through the MCP endpoint (`POST /mcp`). All 11 MCP tools documented in the [Model Context Protocol](#model-context-protocol-mcp) section are available to agents.

#### What agents can do

- Create, list, and delete containers (via MCP)
- Upload and delete files in containers (via MCP)
- Search across any container (via MCP)
- List files and retrieve document content (via MCP)

#### What agents cannot do

- Access any REST API endpoint (returns 403)
- Manage users or roles
- Change system settings (embedding, search, LLM configuration)
- Create or manage other agents
- Access the web UI

> **Security note**: Since agents have system-wide container access via MCP, treat agent API keys with the same care as admin credentials. Revoke unused keys promptly.

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
  "description": "Project knowledge base"
}
```

**Fields**:
- `name` (required): lowercase alphanumeric + hyphens, 2-128 chars, globally unique
- `description` (optional): Container description

A container is always managed storage — Connapse's own backend, configured globally under Settings > Storage. There is no connector to choose. To index an external system, create a connection and a source instead: see [Sources](#sources) and [connectors.md](connectors.md).

> **Changed in v0.4.0.** `connectorType` and `connectorConfig` were removed. A request that still sends them is accepted, the fields are ignored, and a managed container is created — the response carries no connector field back.

**Response** (201 Created):
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "my-project",
  "description": "Project knowledge base",
  "documentCount": 0,
  "createdAt": "2026-02-26T10:00:00Z",
  "updatedAt": "2026-02-26T10:00:00Z"
}
```

---

### List Containers

**Endpoint**: `GET /api/containers?skip=0&take=50`

**Query Parameters**: `skip`, `take` (see [Pagination](#pagination))

**Response** (200 OK):
```json
{
  "items": [
    {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "name": "my-project",
      "description": "Project knowledge base",
      "documentCount": 42,
      "createdAt": "2026-02-26T10:00:00Z",
      "updatedAt": "2026-02-26T10:00:00Z"
    }
  ],
  "totalCount": 1,
  "hasMore": false
}
```

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
- **Containers only**: a source id returns `404`. Sources mirror somebody else's system and are never written to through Connapse. See [connectors.md](connectors.md).

---

### List Files

**Endpoint**: `GET /api/containers/{id}/files?skip=0&take=50`

**Query Parameters**:
- `skip`, `take` (required): See [Pagination](#pagination)
- `path` (optional): Browse a specific folder (e.g., `?path=/docs/`)

**Response** (200 OK):
```json
{
  "items": [
    {
      "type": "file",
      "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "fileName": "report.pdf",
      "path": "/docs/report.pdf",
      "contentType": "application/pdf",
      "sizeBytes": 245678,
      "status": "Ready",
      "createdAt": "2026-02-26T10:00:00Z"
    }
  ],
  "totalCount": 1,
  "hasMore": false
}
```

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

**Containers only**: a source id returns `404`.

**Response** (204 No Content)

---

## Container Folders API

### Create Folder

**Endpoint**: `POST /api/containers/{id}/folders`

**Request Body**:
```json
{ "path": "/docs/2026/" }
```

**Containers only**: a source id returns `404`. A source has no folder tree to add to.

**Response** (200 OK): Returns `Folder` object.

---

### Delete Folder

**Endpoint**: `DELETE /api/containers/{id}/folders?path=/docs/2026/`

Cascade deletes all nested files, subfolders, chunks, vectors, and MinIO objects.

**Containers only**: a source id returns `404`.

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

**Errors** (both routes): `400` [`invalid-search`](problems.md#invalid-search) with `field` naming the input
at fault; `403` when the caller lacks `knowledge:read`; `404` [`search-scope-not-found`](problems.md#search-scope-not-found)
for an unknown container, or a source id (search a source at its own route).

---

### Search a source

**Endpoint**: `POST /api/sources/{sourceId}/search`

Searches a source as a container is searched, with the same request body and response as
`POST /api/containers/{id}/search`. Every hit is checked for the caller. A source the caller may not
know exists returns `404`, except one whose every hit is checked against its provider for them (a
Confluence space, a Jira project, a permission-checked custom source): that one is searched, and
returns `404` only when the caller can read nothing in it. A container id returns `404`.

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

### Retry a Container's Failed Documents

**Endpoint**: `POST /api/containers/{id}/retry-failed`

**Auth**: Editor minimum

Queues every document in the container whose ingestion failed again, with a fresh attempt budget.

**Response** (200 OK): `{ "enqueuedCount": 3 }`

---

## Settings API

All settings endpoints require **Admin** role.

### Get Settings

**Endpoint**: `GET /api/settings/{category}`

**Categories**: `embedding` | `chunking` | `search` | `llm` | `upload` | `summary`, in any case

API keys are never returned: those fields read as `null`. `samlsignin`, `identitycenter`, `azure` and `azuread` can be read here, but changing them is a `409`: use `PUT`/`DELETE /api/v1/providers/aws/permissions`, `/api/v1/providers/aws/identity-center`, `/api/v1/providers/azure/access` and `/api/v1/providers/azure/permissions`. Saving Azure sign-in turns on per-user enforcement, so it is a `503` when enforcement can't be turned on, and its answer carries an `adminConsentUrl` to open in a browser while admin consent is pending. Entra sends the administrator back to the providers page, which records the consent; until then the Azure sign-in requirement in `GET /api/v1/providers` is a `Warning` saying consent is pending, so poll that. A client that recorded consent some other way sends the sign-in settings again with `adminConsentPending: false`.

The GitHub App isn't a settings category. `PUT /api/v1/providers/github/app` saves an existing App's ID, private key and client secret after GitHub accepts them, and replacing a different stored App needs `replaceExisting: true` because it disconnects every GitHub connection; `DELETE` removes it. Creating a new App happens in a browser: `POST /api/v1/providers/github/manifest` (optionally with an `organisation`) returns the `manifest` to post to GitHub's `formAction`, and a `setupPageUrl` for a person to open instead. GitHub sends that person back to Connapse, which stores the App; poll `GET /api/v1/providers` until the GitHub App requirement is met.

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
The body replaces the whole category; a property the category doesn't have is refused (`400`
[`invalid-settings`](problems.md#invalid-settings)). An API key left out or
`null` keeps its value, so a body read from `GET` can be sent back as it is; send `""` to clear one.

**Response** (200 OK):
```json
{
  "message": "Settings updated successfully",
  "category": "chunking",
  "updatedAt": "2026-02-26T11:45:00Z"
}
```

---

### Reset Settings

**Endpoint**: `DELETE /api/v1/settings/{category}`

Removes what is stored for the category, so it reads as configuration (appsettings, environment
variables) gives it. Resetting `embedding` also rebuilds the configured model's vector index.

**Response** (204 No Content); `404` [`settings-category-not-found`](problems.md#settings-category-not-found)
for an unknown category.

---

### Test Connection

**Endpoint**: `POST /api/settings/test-connection`

**Categories**: `embedding` | `llm` | `crossencoder` | `minio`, in any case. Another category answers
`200` with `success: false`.

The settings are tested as sent and never stored. An API key left out is not filled in from the stored
one, since the settings can name any address: send the key to test with. `timeoutSeconds`, when given,
is 1 to 300; a missing category or settings, settings that don't fit the category, or a timeout out of
range is `400` [`invalid-settings`](problems.md#invalid-settings).

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

### Embedding Models

**Endpoint**: `GET /api/settings/embedding-models`

The embedding models with vectors in any container. `currentModel` is the one the saved embedding
settings name (configuration's when none are saved); `hasLegacyVectors` says whether another model has any.

---

### Re-Index

**Endpoint**: `POST /api/settings/reindex`

**Request Body** (optional): `{ "containerId": "<guid>", "force": false }`

Starts re-embedding in the background and answers at once. Without `force`, only documents whose
embedding or chunking settings changed are re-embedded; with `force`, every document is. Either way
cross-model search is turned on, so documents still on a previous model stay findable. `containerId`
limits it to one container.

**Endpoint**: `GET /api/settings/reindex/status` — the latest run (`Idle`, `Running`, `Completed` or
`Failed`, with its error) and the ingestion queue's depth.

---

### Clear All Summaries

**Endpoint**: `POST /api/v1/settings/summaries/clear`

Clears every container's summary and every document summary; the next sweep writes them again.
Embeddings, chunks and documents are kept.

**Response** (200 OK): `{ "containers": 3, "documents": 41 }`

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

### Subscribe to Document Status

```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/ingestion?access_token=eyJ...")
  .build();

await connection.start();

// Subscribe to one document (the documentId returned by an upload)
await connection.invoke("SubscribeToDocument", "document-id");

// Sent after every status transition
connection.on("DocumentStatusChanged", (update) => {
  // update: { documentId, status, failureKind, summaryStatus }
  console.log(`${update.documentId}: ${update.status}`);
});
```

**Status**: `Pending` | `Processing` | `Ready` | `Failed` — the same strings as the document's `status` field.

**Failure kind** (only when `Failed`): `Retryable` (a transient fault; sync retries it later) | `Permanent` (the file cannot be ingested as it is).

**Summary status**: `NotNeeded` | `Pending` | `Done` | `Failed`

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

### What is writable

Write tools (`container_create`, `container_delete`, `upload_file`, `bulk_upload`, `delete_file`, `bulk_delete`) resolve **containers only**.

There is no permission table any more: only the managed-storage connector implements `IWritableConnector`, so a source is incapable of being written to rather than blocked at runtime. Passing a source id returns `Error: Container '<id>' not found.` — the same answer as an id that does not exist.

### bulk_upload

Upload up to 100 files in a single operation. Each file is written to storage and queued for ingestion (parsing, chunking, embedding).

**Parameters**:
- `containerId` (required): Container ID (UUID) or name
- `files` (required): JSON array of file objects (max 100)

**File object schema**:
```json
{
  "filename": "report.pdf",
  "content": "base64-encoded-content-here",
  "encoding": "base64",
  "folderPath": "/docs/"
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `filename` | Yes | Target filename |
| `content` | Yes | File content (raw text or base64-encoded) |
| `encoding` | No | `"text"` (default) or `"base64"` for binary files |
| `folderPath` | No | Destination folder path (default: `"/"`) |

**Success response**:
```
Uploaded 3 of 3 file(s) to container 'my-project'.

All files queued for ingestion (parsing, chunking, embedding).
```

**Partial failure response**:
```
Uploaded 2 of 3 file(s) to container 'my-project'.

Failures:
- report.pdf: invalid base64 content
```

**Errors**:
| Error | Cause |
|-------|-------|
| `'files' must be a valid JSON array` | Malformed JSON |
| `'files' array must not be empty` | Empty array |
| `Maximum 100 files per bulk_upload call` | Too many files |
| `Container '{id}' not found` | Invalid container ID/name |
| `missing 'filename'` | File object missing filename |
| `missing 'content'` | File object missing content |
| `invalid base64 content` | Bad base64 when encoding is `"base64"` |

**Notes**:
- All files share a single batch ID for tracking
- Uses `Semantic` chunking strategy
- Intermediate folders are created automatically
- Containers only — see [What is writable](#what-is-writable)

---

### bulk_delete

Delete up to 100 files from a container in a single operation. Removes database records and backing storage files.

**Parameters**:
- `containerId` (required): Container ID (UUID) or name
- `fileIds` (required): JSON array of document ID strings (max 100)

**Example input**:
```json
["3fa85f64-5717-4562-b3fc-2c963f66afa6", "7f8e9d1c-2b3a-4c5d-8e9f-1a2b3c4d5e6f"]
```

**Success response**:
```
Deleted 2 of 2 file(s).
```

**Partial failure response**:
```
Deleted 1 of 2 file(s).

Warnings (1):
- 7f8e9d1c (...): storage cleanup failed

Failures:
- abc123: not found
```

**Errors**:
| Error | Cause |
|-------|-------|
| `'fileIds' must be a valid JSON array of strings` | Malformed JSON |
| `'fileIds' array must not be empty` | Empty array |
| `Maximum 100 files per bulk_delete call` | Too many files |
| `Container '{id}' not found` | Invalid container ID/name |

**Notes**:
- Files not found or belonging to a different container are reported as failures
- Storage deletion failures are non-fatal warnings (DB record is still removed)
- Does not clean up empty parent folders
- Containers only — see [What is writable](#what-is-writable)

---

## CLI

The `connapse` command-line client lives in its own repository: [Destrayon/connapse-cli](https://github.com/Destrayon/connapse-cli). Install via:

```bash
dotnet tool install -g Connapse.CLI
# or: download a binary from https://github.com/Destrayon/connapse-cli/releases/latest
```

See the [connapse-cli README](https://github.com/Destrayon/connapse-cli#readme) for the full command reference and authentication flow.

```bash
connapse auth login --server https://your-server
connapse --help
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
| 429 | Rate limit exceeded (see rate limiting) |
| 500 | Unexpected server error |
| 503 | External service (Ollama, MinIO) unreachable |

---

## Sources

Base path: `/api/sources`

A **source** is an external system Connapse mirrors read-only — an S3 bucket, an SFTP directory, or a filesystem directory. It is searchable but never browsable and never writable. See [connectors.md](connectors.md) for the connection/source model.

**Connections have no REST surface**, apart from [Atlassian sites](#atlassian-sites), which are saved only after every check passes, and the [Atlassian linking app](#atlassian-linking-app), which is saved only after Atlassian accepts it. They hold the credential boundary and are managed on the Connections page by administrators only. Sources are scoped inside a connection an administrator already approved, which is why they do have routes.

| Method | Route | Role |
|--------|-------|------|
| GET | `/api/sources` | Viewer |
| GET | `/api/sources/{id}` | Viewer |
| POST | `/api/sources` | **Admin** |
| PATCH | `/api/sources/{id}` | **Admin** |
| DELETE | `/api/sources/{id}?confirmName=<name>` | **Admin** |
| POST | `/api/sources/{id}/sync` | **Admin** |
| POST | `/api/sources/{id}/retry-failed` | **Admin** |

### Create Source

**Endpoint**: `POST /api/sources`

**Request Body**:
```json
{
  "name": "company-docs",
  "description": "Shared reference material",
  "connectionId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "scopeJson": "{\"bucketName\":\"company-knowledge\",\"prefix\":\"docs/\"}"
}
```

Scope keys by provider:

| Provider | Keys |
|----------|------|
| S3 | `bucketName`, `prefix` |
| SFTP | `subPath`, `includePatterns`, `excludePatterns` |
| Filesystem | `subPath`, `includePatterns`, `excludePatterns` |
| GitHub | `owner`, `repo`, `kind`, `includePatterns`, `includeComments` |
| Atlassian (Confluence) | `spaceId`, `includeAttachments`, `maxAttachmentMb` |
| Atlassian (Jira) | `kind` (`"jira-project"`), `projectId`, `includeAttachments`, `maxAttachmentMb` |

The scope must fall inside the `allowedLocations` or `allowedRoot` its connection declares, or creation fails.

For an **Atlassian** connection, the request runs the checks the New source dialog runs: `spaceId` must be a space the site's service account can view now, the space must not already have a source on the connection, and `maxAttachmentMb` must be 1 to 100 while attachments are on. `kind`, `spaceKey` and `spaceName` are written from Atlassian's answer, over anything sent. `name` may be left out; it then defaults to `<site host> / <space name>`, as in the dialog.

```json
{
  "connectionId": "8a1b...",
  "scopeJson": "{\"spaceId\":\"98306\",\"includeAttachments\":true,\"maxAttachmentMb\":25}"
}
```

| Field | Type | Default | Rule |
|-------|------|---------|------|
| `spaceId` | string of digits (a number is accepted) | required | A space the site's service account can view now, with no source on this connection yet |
| `includeAttachments` | boolean | `true` if the site's last connection test confirmed the `read:attachment:confluence` scope, otherwise `false` | Attachments need that scope on the site's credential |
| `maxAttachmentMb` | whole number | `25` | 1 to 100 while `includeAttachments` is `true`. With attachments off it is not checked, and an out-of-range value is stored as `25` |

A failed check is a `400` naming the field, for example `{ "error": "...", "field": "spaceId" }`. A space that already has a source on the connection is a `409`, and if Atlassian can't be reached the response is `502`.

If a source includes attachments but the site's credential lacks `read:attachment:confluence`, syncing still succeeds for pages and comments. The source's `lastSyncStatus` stays `"Succeeded"` and `lastSyncError` carries the notice *Attachments were skipped: the site's credential lacks the read:attachment:confluence permission. Add it to the credential, or turn off Include attachments.* The site is not marked as needing new credentials.

On a **Jira** connection the scope names a project instead:

```json
{
  "connectionId": "8a1b...",
  "scopeJson": "{\"kind\":\"jira-project\",\"projectId\":\"10000\",\"includeAttachments\":false}"
}
```

`projectId` must be a project the site's service account can browse now (list them with `GET /api/v1/atlassian/sites/{connectionId}/projects`), with no source on this connection yet. `includeAttachments` defaults to `true` only if the site's last connection test confirmed `read:attachment:jira`, and `maxAttachmentMb` follows the same rule as for Confluence. `name` defaults to `<site host> / <project name> (Jira)`.

For either product, a `400` with `field: "connectionId"` means the scope is for the other product than the connection reads, or the site hasn't passed a connection test yet (run `POST /api/v1/atlassian/sites/{connectionId}/test`, or choose **Test connection**).

### Update a Confluence Source

**Endpoint**: `PATCH /api/sources/{id}`

`name`, `description`, `syncIntervalSeconds` and `enabled` work as for any source; `enabled: false` is what **Pause syncing** on the Sources page sends. In `scopeJson`, send only the attachment fields to change; fields left out keep their stored values. A changed scope makes the next sync re-read the space.

```json
{
  "scopeJson": "{\"includeAttachments\":false}"
}
```

| Field | Rule |
|-------|------|
| `includeAttachments` | `true` or `false` |
| `maxAttachmentMb` | A whole number from 1 to 100 while attachments are on. With attachments off it is not checked, and an out-of-range value keeps the stored size |
| `spaceId` | May be sent only if it is the source's current space. A source covers one space; add another space as a new source |

**Errors** (`400`, naming the field; a `scopeJson` that is not valid JSON at all is a `400` with no `field`):

| `field` | When |
|---------|------|
| `scopeJson` | The scope is JSON but not an object |
| `spaceId` | It names a different space, or is not a string of digits |
| `includeAttachments` | It is not `true` or `false` |
| `maxAttachmentMb` | It is not a whole number, or is outside 1 to 100 while attachments are on |

### Update a Jira Source

**Endpoint**: `PATCH /api/sources/{id}`

The same as for a Confluence source, with `projectId` in place of `spaceId`: only `includeAttachments` and `maxAttachmentMb` can change, and a `projectId` other than the source's own is a `400` naming `projectId`. Add another project as a new source.

The Sources page's **Edit** view changes the same two settings under the same rules.

### Delete Source

**Endpoint**: `DELETE /api/sources/{id}?confirmName=<name>`

Deletes the source and every document Connapse indexed from it; the data in the external system is untouched. `confirmName` must equal the source's name, the value the Sources page asks you to type, or the answer is `400` with `field: "confirmName"` and an `error` saying how many indexed documents the delete would remove. `204` when deleted, `404` if there is no such source.

> **Changed:** `confirmName` is required. A client that sends `DELETE /api/sources/{id}` without it now gets `400` and nothing is deleted.

### Source Response

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "company-docs",
  "description": "Shared reference material",
  "connectionId": "8a1b...",
  "enabled": true,
  "lastSyncStatus": "Succeeded",
  "lastSyncedAt": "2026-02-26T10:05:00Z",
  "syncIntervalSeconds": 300,
  "documentCount": 128,
  "failedDocumentCount": 2,
  "processingDocumentCount": 0,
  "summary": null,
  "lastSyncError": null,
  "lastSyncTroubleshootingUrl": null,
  "confluence": null,
  "createdAt": "2026-02-26T10:00:00Z",
  "updatedAt": "2026-02-26T10:05:00Z",
  "kind": "source"
}
```

For an administrator reading a Confluence source, `confluence` holds the settings the Sources page and its **Edit** view show, under the same names `scopeJson` takes on create and `PATCH`:

```json
"confluence": {
  "spaceId": "98306",
  "spaceKey": "ENG",
  "spaceName": "Engineering",
  "includeAttachments": true,
  "maxAttachmentMb": 25
}
```

`spaceKey` and `spaceName` are what the space was called when the source was added. A source created before a key was stored reads the connector's default for it (`includeAttachments: true`, `maxAttachmentMb: 25`). `confluence` is `null` for every other source, and for readers who aren't administrators.

`lastSyncTroubleshootingUrl` is, for administrators, the setup guide entry the Sources page links beside `lastSyncError` (Confluence and GitHub sources), or `null`.

**Two fields are withheld deliberately.** `scopeJson` names buckets, prefixes, and filesystem subpaths, and `syncCursor` is an opaque provider continuation token — returning either would turn a read route into reconnaissance. The one exception is `confluence` above, a Confluence source's settings, returned to administrators only. `lastSyncError` is populated **for administrators only**, because a provider's failure text routinely echoes what failed (`Access Denied for bucket payroll-data`). With `lastSyncStatus: "Succeeded"` it is a notice, not a failure: the cycle was cut short on purpose, for example because GitHub's hourly request limit was used up, and the next cycle continues. The Sources page shows this as **Waiting**.

`kind` is always `"source"`, so a client consuming both these and the container routes can tell them apart on one field. It matches the MCP contract.

### Sync a Source

**Endpoint**: `POST /api/sources/{id}/sync`

Runs one reconciliation cycle immediately rather than waiting for the next scheduled poll. A source already syncing is reported as such rather than queued.

### Retry a Source's Failed Documents

**Endpoint**: `POST /api/sources/{id}/retry-failed`

Queues every document of the source whose ingestion failed again, with a fresh attempt budget. Without it, a document that failed for a temporary reason (`failureKind: "Retryable"`) is retried by the sync engine 24 hours after it failed, and one that cannot be read as it is (`"Permanent"`) only when the file changes. Use this once whatever made them fail has been fixed.

**Response** (200 OK): `{ "enqueuedCount": 2 }`

### Delete a Source

**Endpoint**: `DELETE /api/sources/{id}`

Removes the source and its indexed documents. **The external data is untouched.**

### Atlassian Sites

Base path: `/api/v1/atlassian/sites`. Administrator only. A token needs the `admin:sources` scope to list, read, rename, pause, resume or delete a site. Everything the Connections page can do to a site is here, with the same field names, the same checks and the same audit events. Setup steps and every field are in the [Atlassian setup guide](atlassian-setup.md). The client secret is accepted on the way in and never returned.

| Method | Route | Purpose |
|--------|-------|---------|
| GET | `/` | Every saved site, as [site objects](#atlassian-site-object) without `scopes` |
| POST | `/resolve` | `{ "siteUrl" }` → `{ siteUrl, cloudId }`. `400` if the address is not `https://<name>.atlassian.net`, `404` if no site is there |
| POST | `/` | `{ "siteUrl", "clientId", "clientSecret", "product"?, "saveAnyway"? }`. `product` is `confluence` (the default) or `jira`: a site's Confluence and Jira are separate connections, so add it once as each to index both. Runs that product's site checks and saves the site as a connection only if all pass: `201` with `{ id, name, siteUrl, cloudId, clientId, product, test }`, where `test` is a [test report](#atlassian-test-report). The site is named after its host, with ` (Jira)` added for a Jira connection. `400` with `field: "product"` for any other `product`. A failed check is `422` with the test report. `saveAnyway: true` saves a site whose only problem is a warning (`warning: true`: nothing yet to check administrator access against, which is no page or no other user on a Confluence site, and no project, issue or other user on a Jira site); any other failure is never saved. `409` if the site already has a connection for that product |
| GET | `/{connectionId}` | One [site object](#atlassian-site-object), with `scopes`. `404` if it isn't an Atlassian site |
| PATCH | `/{connectionId}` | `{ "name" }` renames the site, as the Name field does. `200` with the site object. `400` if the name is blank or over 128 characters, `409` if another connection has it. Audited as `connection.updated` |
| POST | `/{connectionId}/test` | Runs every check again with the stored secret and records the result as the site's health: `200` with a [test report](#atlassian-test-report), passed or not. `404` if the site is gone or its stored secret can't be read |
| PUT | `/{connectionId}/credentials` | `{ "clientId", "clientSecret", "saveAnyway"? }`. Tests the new credential and replaces the stored one only if it passes, keeping the connection's id so its sources keep syncing: `200` with `{ id, name, clientId, test }`. `422` with the test report if it fails (nothing changes), `409` if the site now has a different Cloud ID (it is a different site; add it as a new connection). Audited as `connection.credentials_replaced` |
| POST | `/{connectionId}/pause` | Pause syncing: disables every source on the site. Nothing is deleted and documents stay searchable. `200` with `{ syncPaused: true, sources }`; `409` if the site has no sources. Audited as `connection.paused` |
| POST | `/{connectionId}/resume` | Resume syncing. Optional body `{ "sourceIds": [...] }` turns back on only those sources (as the page's Resume dialog lets you untick some); without it, every source. `200` with `{ syncPaused, sources }`. `400` if a `sourceIds` entry isn't on this site. Audited as `connection.resumed` |
| DELETE | `/{connectionId}?confirmName=<name>` | Deletes the site. `confirmName` must equal the site's name, the value the page asks you to type, or the answer is `400`. `409` while any source uses the site, with `{ error, sources: [{ id, name, url }] }`: `error` names each source, as the page's refusal does, and `url` is the source's page. Delete those sources first. `204` when deleted. Audited as `connection.deleted` |
| GET | `/{connectionId}/spaces?includePersonal=false` | The spaces the site's service account can view: `[{ id, key, name, type }]`. `400` with `field: "connectionId"` on a Jira connection, `502` if Atlassian can't be reached |
| GET | `/{connectionId}/projects` | The Jira projects the site's service account can browse: `[{ id, key, name, projectTypeKey }]`. `400` with `field: "connectionId"` on a Confluence connection, `502` if Atlassian can't be reached |

#### Atlassian site object

| Field | Meaning |
|-------|---------|
| `id`, `name` | The connection's id and name |
| `siteUrl`, `cloudId` | The site's address and Atlassian's own ID for it |
| `clientId` | The service account credential's client ID. The secret is never returned; `hasSecret` says one is stored |
| `product` | What the connection reads: `confluence` or `jira`. A connection saved without one is `confluence`. A `confluence-space` source needs a `confluence` connection and a `jira-project` source a `jira` one |
| `sourceCount` | How many sources use the site |
| `syncPaused` | `true` when the site has sources and every one is disabled |
| `health` | The last check, or `null` before one: `{ checkedAt, succeeded, warning, step, stepLabel, message, origin, credentialRejected, lastSucceededAt, confirmedScopes }`. `origin` is `test` or `sync`; `credentialRejected: true` is what the page shows as **Needs new credentials** |
| `scopes` | Single site only: `[{ name, reason, optional, product }]`, every scope the connection's credential needs and why: its own product's only. `product` is `confluence` or `jira` |

#### Atlassian test report

Returned by `/test`, inside `test` on a create or replace, and as the `422` body of a failed one.

| Field | Meaning |
|-------|---------|
| `success` | `true` when every check passed |
| `warning` | `true` when the only problem is that the site has nothing yet to check against: no page or no other user for Confluence, no project, issue or other user for Jira; `saveAnyway` can save past it |
| `message` | What happened and what to do, as the page shows it |
| `step` | The check that failed: `site`, `token`, `identity`, `spaces`, `content` (a scope a sync needs for comments, folders or changes is missing) or `admin`, or on a Jira connection `jira-identity`, `jira-projects`, `jira-content`, `jira-admin` or `jira-membership`; `null` on success |
| `stepLabel` | That check in the page's words, for example "Signing in as the service account" |
| `troubleshootingUrl` | The setup guide entry for the failure. A timeout, a rate limit or an answer Atlassian didn't finish links the guide's general Troubleshooting section, not a credential entry |
| `detail` | Atlassian's own answer when it gave one, such as `HTTP 401 (Unauthorized)`; never a secret or token |
| `scopes` | On success, the scopes the test confirmed the credential carries for the connection's product. For Confluence: every required one, plus `read:attachment:confluence` when its probe passed. For Jira: the scopes its checks call (`read:user:jira`, `read:project:jira`, `read:issue-details:jira`, `read:issue:jira`, `read:comment:jira`, `read:permission:jira`, `read:group:jira`, `read:project-role:jira`), `read:attachment:jira` when its probe passed, and any other Jira scope Atlassian lists as granted |

### Atlassian Linking App

Base path: `/api/v1/atlassian/linking-app`. Administrator only. The app people sign in through to link their Atlassian accounts, at parity with the Linking app card on the Providers page: the same check with Atlassian before saving, the same typed confirmation to remove it, and the same audit events. The client secret is accepted on the way in and never returned.

| Method | Route | Purpose |
|--------|-------|---------|
| GET | `/` | `{ clientId, permission, signInEnabled, lastCheckedAt, lastCheckAccepted, lastUsedForSignInAt, peopleLinked }`. `lastCheckAccepted` is `null` before a check; `peopleLinked` is `null` if it can't be counted. `404` when no app is stored |
| PUT | `/` | `{ "clientId", "clientSecret", "replaceExisting"? }`. Asks Atlassian whether it accepts the pair and stores it only if it does. Saving turns sign-in on. When an app is already stored, `replaceExisting: true` is required, as the page asks you to choose **Check and replace**: every new sign-in then goes through the new app, and people who linked keep their links. Without it the answer is `409` with `{ error, field: "replaceExisting" }`, before Atlassian is asked and with nothing changed. `200` with the app as `GET` returns it. `400` if a field is missing, `422` with `{ error, troubleshootingUrl, detail }` if Atlassian rejects the pair, `502` with `{ error, troubleshootingUrl, detail }` if Atlassian can't be reached or gives no clear answer. `detail` is Atlassian's own answer, as the card's **Details** shows it: the status and Atlassian's error code (such as `HTTP 401, access_denied`), or why there was none; never the secret. Nothing is stored on a failure. Audited as `provider.credential.check_started` and `provider.credential.checked` (sharing an attempt id, with the time the check took), then `provider.credential.saved` |
| POST | `/check` | Checks the stored pair with Atlassian again, as **Recheck** on the card does, and records the answer as the last check: `200` with `{ result, message, troubleshootingUrl, detail }`, where `result` is `accepted`, `rejected` or `unknown` (couldn't reach Atlassian; nothing recorded), `troubleshootingUrl` is the guide entry (`null` when accepted) and `detail` is Atlassian's own answer as on the card (`null` when accepted). `404` when no app is stored. Audited as `provider.credential.check_started` and `provider.credential.checked` |
| PUT | `/sign-in` | `{ "signInEnabled": false }` turns Atlassian sign-in off: nobody new can link, and people who linked keep their links. `true` turns it back on. `200` with the app. `400` without `signInEnabled`, `404` when no app is stored. Audited as `provider.sign_in.disabled` or `provider.sign_in.enabled` |
| DELETE | `/?confirmClientId=<client ID>` | Removes the app. `confirmClientId` must equal the stored client ID, the value the page asks you to type, or the answer is `400`. People who linked keep their links, but nobody can link until an app is added again. `204` when removed, `404` when none is stored. Audited as `provider.credential.deleted` |

---

## Container Connector Endpoints

### Sync Container

Reconcile a container's managed storage against the document table. Lists stored objects, compares to the database, and enqueues anything new or unfinished — useful when objects have landed in the bucket out of band.

**Endpoint**: `POST /api/containers/{id}/sync`

**Auth**: Editor minimum

**Response** (200 OK):
```json
{
  "batchId": "abc123",
  "totalFiles": 42,
  "enqueuedCount": 5,
  "skippedCount": 37
}
```

> **Changed in v0.4.0.** `POST /api/containers/test-connection` was removed — it validated connector config before creating a container, which no longer happens. Connection testing lives on the Connections page. This route also no longer talks to any external system: external syncing is `POST /api/sources/{id}/sync`.

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

## Cloud Identity Endpoints

Base path: `/api/v1/auth/cloud`

AWS-only per-user identity link, backed by an IAM Identity Center SAML flow (see
[architecture.md](architecture.md) — Identity Linking). Azure AD per-user identity linking was
removed in the Azure teardown (#476).

### List My Account Links

**Endpoint**: `GET /api/v1/auth/links`

**Authorization**: a signed-in person or their own token (`knowledge:read`); agent keys get 403.

The caller's own linked accounts, one entry per provider: AWS, Microsoft Entra ID, GitHub, Atlassian, then each custom provider. Search hides documents that only an unlinked account may read, so an agent can use this to explain a missing result. Linking itself happens in a browser, at `linkPage`.

**Response** (200 OK):
```json
{
  "links": [
    { "provider": "github", "providerId": null, "name": "GitHub", "linked": true, "account": "octocat", "linkedAt": "2026-10-01T12:00:00Z", "canLink": true },
    { "provider": "atlassian", "providerId": null, "name": "Atlassian", "linked": false, "account": null, "linkedAt": null, "canLink": false },
    { "provider": "custom", "providerId": "3f2c…", "name": "Acme Wiki", "linked": null, "account": null, "linkedAt": null, "canLink": true }
  ],
  "linkPage": "/profile/integrations"
}
```

`linked` is null when a custom provider's source server couldn't say within a few seconds. `canLink` says whether this server offers linking for that provider.

### Start AWS Connect

**Endpoint**: `GET /api/v1/auth/cloud/aws/connect`

Redirects the browser to IAM Identity Center to begin the SAML sign-in. Requires authentication.

**Response**: 302 Redirect to IAM Identity Center.

---

### AWS Assertion Consumer

**Endpoint**: `POST /api/v1/auth/cloud/aws/acs`

Anonymous endpoint where IAM Identity Center posts the signed SAML assertion. Validates the
assertion and RelayState, then redirects to the confirm step.

**Response**: 302 Redirect to `/api/v1/auth/cloud/aws/confirm`.

---

### Confirm AWS Link

**Endpoint**: `GET /api/v1/auth/cloud/aws/confirm`

Same-site redirect target that carries the session cookie. Verifies the signed-in user matches the
one who started the sign-in, then saves the link.

**Response**: 302 Redirect to `/profile/integrations`.

---

### Disconnect Cloud Identity

**Endpoint**: `DELETE /api/v1/auth/cloud/{provider}`

**Path Parameters**: `provider` = `AWS`

Removes the linked identity and evicts cached scope entries.

**Response** (204 No Content | 404 Not Found)

---

## Audit Log

### List Audit Entries

**Endpoint**: `GET /api/v1/audit/`

**Authorization**: Owner or Admin. A personal access token needs the `admin:audit` scope.

Every sensitive action writes an audit entry: who did it, to which resource, from which IP address, and when. Entries come newest first.

**Query Parameters**: `action`, `userId`, `resourceType`, `resourceId` (exact matches); `since` (inclusive) and `until` (exclusive), UTC, where a time given without a zone is read as UTC; `take` (default 50, up to 200); `before`, a page's `nextBefore`, for the next page. Paging by cursor means entries written between requests never repeat or shift a page.

**Response** (200 OK):
```json
{
  "items": [
    { "id": "…", "createdAt": "2026-10-24T21:40:00Z", "action": "source.updated", "userId": "…", "userEmail": "admin@example.com",
      "resourceType": "source", "resourceId": "…", "details": { "Name": "docs" }, "ipAddress": "10.0.0.4" }
  ],
  "hasMore": true,
  "nextBefore": "NjM4OTY…"
}
```

**Errors**: 400 `invalid-page` (`take` out of range, or a `before` this endpoint didn't return), 400 `invalid-audit-filter` (`since` not before `until`), 403 `forbidden`.

---

## OAuth Clients

OAuth clients are the MCP clients that sign people in to Connapse: ones that registered themselves (`/oauth/register`), and ones known by a client ID metadata URL, which never register. Both require Owner or Admin, and a personal access token needs the `admin:oauth-clients` scope.

### List OAuth Clients

**Endpoint**: `GET /api/v1/oauth-clients/`

Registered clients oldest first, then metadata-URL clients that hold grants.

**Response** (200 OK):
```json
[
  { "clientId": "5f0c…", "name": "Claude Desktop", "applicationType": "native", "redirectUris": ["http://127.0.0.1:8765/callback"],
    "registered": true, "registeredAt": "2026-10-01T12:00:00Z", "registeredFromIp": "10.0.0.4",
    "activeGrants": 2, "users": 1, "lastGrantAt": "2026-10-24T09:00:00Z" }
]
```

`activeGrants` counts refresh tokens neither revoked nor expired; `users` is how many people granted them.

### Revoke an OAuth Client

**Endpoint**: `DELETE /api/v1/oauth-clients/{clientId}` (escape a metadata-URL client ID)

Removes the client's registration, so it can't start a new sign-in; revokes every refresh token it holds; and deletes its pending authorization codes. Access tokens it already holds stay valid until they expire. A registered client's tokens and codes stop working once its registration is gone, even one minted while the revoke ran. A metadata-URL client can sign in again if a person approves it again. Writes an `oauth.client.revoked` audit entry.

**Response** (200 OK): `{ "clientId": "…", "clientRemoved": true, "refreshTokensRevoked": 2, "authCodesRemoved": 0 }`

**Errors**: 404 `oauth-client-not-found` (no registration, grants or pending codes), 403 `forbidden`.

---

## Versioning

**Current Version**: `v0.3.2`

Auth endpoints are under `/api/v1/auth/` and `/api/v1/agents/`. Container endpoints remain under `/api/containers/`. Cloud identity endpoints are under `/api/v1/auth/cloud/`.

---

## References

- [architecture.md](architecture.md) — System architecture and design
- [connectors.md](connectors.md) — Connections, sources, and containers
- [deployment.md](deployment.md) — Deployment and configuration
- [mcp-tools.md](mcp-tools.md) — Tool reference for agents
