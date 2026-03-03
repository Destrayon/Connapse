# Connectors

Connapse uses **Connectors** to interface with different storage backends. A **Container** is a logical knowledge base; a **Connector** is the technology that backs it.

## Connector Types

| Type | Config Required | Live Watch | Sync | Use Case |
|------|----------------|------------|------|----------|
| **MinIO** | No (global) | Polling (5 min) | Yes | Default — self-hosted S3-compatible storage |
| **Filesystem** | `rootPath` | FileSystemWatcher | No (auto) | Local directories, shared drives |
| **InMemory** | No | No | No | Ephemeral agent working memory |
| **S3** | `bucketName`, `region` | Polling (5 min) | Yes | AWS S3 buckets (IAM-only) |
| **AzureBlob** | `storageAccountName`, `containerName` | Polling (5 min) | Yes | Azure Blob Storage (managed identity) |

---

## MinIO (Default)

MinIO is the default connector. It uses globally configured storage settings — no per-container configuration needed.

**How it works:**
- Single `IAmazonS3` client shared across all MinIO containers
- Files stored in a global MinIO instance with per-container key prefixes
- Background polling detects remote changes every 5 minutes

**Setup:**
1. MinIO runs automatically via Docker (development) or is configured in `appsettings.json`
2. Global settings: Settings > Storage > MinIO endpoint, access key, secret key
3. Create a container with no connector type specified (defaults to MinIO)

**API:**
```http
POST /api/containers
{ "name": "my-knowledge-base" }
```

---

## Filesystem

The Filesystem connector watches a local directory for changes in real time using `FileSystemWatcher`.

**Configuration:**
```json
{
  "rootPath": "C:\\Documents\\Knowledge",
  "includePatterns": ["*.pdf", "*.docx", "*.md"],
  "excludePatterns": ["*.tmp", "~*"],
  "allowDelete": true,
  "allowUpload": true,
  "allowCreateFolder": true
}
```

| Field | Required | Default | Description |
|-------|----------|---------|-------------|
| `rootPath` | Yes | — | Absolute path to the watched directory |
| `includePatterns` | No | `[]` (all files) | Glob patterns for files to include |
| `excludePatterns` | No | `[]` | Glob patterns for files to exclude |
| `allowDelete` | No | `true` | Allow deleting files through the UI |
| `allowUpload` | No | `true` | Allow uploading files through the UI |
| `allowCreateFolder` | No | `true` | Allow creating folders through the UI |

**How live watch works:**
1. `FileSystemWatcher` monitors the root path for Created, Changed, Deleted, Renamed events
2. Events are debounced with a **750ms** quiet period (prevents duplicate processing)
3. New external files are automatically ingested; UI uploads are skipped (the upload endpoint owns them)
4. Changed files are re-ingested unless already Pending/Queued/Processing
5. Deleted files have their documents and chunks removed from the database
6. Renamed files reuse the original document ID (avoids unique constraint issues)
7. A full rescan runs every **5 minutes** as a safety net

**Glob matching:** Patterns match against filename only (not full path). Supports `*` (any characters) and `?` (single character). Case-insensitive.

**API:**
```http
POST /api/containers
{
  "name": "local-docs",
  "connectorType": "Filesystem",
  "connectorConfig": "{\"rootPath\":\"C:\\\\Documents\\\\Knowledge\"}"
}
```

> **Note:** Filesystem containers do not support manual sync — the live watcher handles everything automatically.

---

## InMemory

The InMemory connector stores files in process memory. All data is lost when the application restarts.

**How it works:**
- Files stored in a `ConcurrentDictionary` keyed by path
- Same connector instance reused for the lifetime of the container (singleton per container ID)
- Ephemeral containers are swept on startup (all documents/folders deleted)

**Use cases:**
- Agent working memory — temporary RAG context for an active session
- Testing and development
- Short-lived data that doesn't need persistence

**API:**
```http
POST /api/containers
{
  "name": "agent-scratch",
  "connectorType": "InMemory"
}
```

> **Note:** InMemory containers do not support sync (no remote source to sync from).

---

## S3

The S3 connector reads from AWS S3 buckets using IAM credentials only — no access keys are stored.

**Configuration:**
```json
{
  "bucketName": "company-knowledge",
  "region": "us-east-1",
  "prefix": "docs/",
  "roleArn": "arn:aws:iam::123456789012:role/ConnaspseReader"
}
```

| Field | Required | Default | Description |
|-------|----------|---------|-------------|
| `bucketName` | Yes | — | S3 bucket name |
| `region` | Yes | `us-east-1` | AWS region |
| `prefix` | No | — | Key prefix to scope access within the bucket |
| `roleArn` | No | — | IAM role ARN for cross-account access via STS AssumeRole |

**Authentication:** Uses `DefaultAWSCredentials` — IAM roles, environment variables, or instance profiles. No stored access keys.

**How sync works:**
- Background polling every **5 minutes** compares remote files against the database
- First poll detects creates and deletes
- Subsequent polls also detect changes (LastModified/SizeBytes differences)
- New files are pre-registered as "Pending" in the database (visible in UI immediately)
- On-demand sync available via `POST /api/containers/{id}/sync`

**Cross-account access:** Set `roleArn` to assume a role in another AWS account. The connector uses STS `AssumeRole` to get temporary credentials.

**API:**
```http
POST /api/containers
{
  "name": "s3-knowledge",
  "connectorType": "S3",
  "connectorConfig": "{\"bucketName\":\"company-knowledge\",\"region\":\"us-east-1\"}"
}
```

**Test before creating:**
```http
POST /api/containers/test-connection
{
  "connectorType": "S3",
  "connectorConfig": "{\"bucketName\":\"company-knowledge\",\"region\":\"us-east-1\"}",
  "timeoutSeconds": 15
}
```

---

## Azure Blob

The Azure Blob connector reads from Azure Blob Storage containers using managed identity — no connection strings stored.

**Configuration:**
```json
{
  "storageAccountName": "companydata",
  "containerName": "knowledge",
  "prefix": "docs/",
  "managedIdentityClientId": "12345678-1234-1234-1234-123456789012"
}
```

| Field | Required | Default | Description |
|-------|----------|---------|-------------|
| `storageAccountName` | Yes | — | Azure Storage account name |
| `containerName` | Yes | — | Blob container name |
| `prefix` | No | — | Blob prefix to scope access |
| `managedIdentityClientId` | No | — | Client ID for user-assigned managed identity |

**Authentication:** Uses `DefaultAzureCredential` — managed identity, Azure CLI, environment variables. For user-assigned managed identities, set `managedIdentityClientId`.

**How sync works:** Same as S3 — 5-minute background polling + on-demand sync endpoint.

**API:**
```http
POST /api/containers
{
  "name": "azure-knowledge",
  "connectorType": "AzureBlob",
  "connectorConfig": "{\"storageAccountName\":\"companydata\",\"containerName\":\"knowledge\"}"
}
```

**Test before creating:**
```http
POST /api/containers/test-connection
{
  "connectorType": "AzureBlob",
  "connectorConfig": "{\"storageAccountName\":\"companydata\",\"containerName\":\"knowledge\"}",
  "timeoutSeconds": 15
}
```

> **Note:** `DefaultAzureCredential` tries multiple authentication methods sequentially, so the first connection test may take longer than expected.

---

## Per-Container Settings

Each container can override global settings for chunking, embedding, search, and upload.

**Override hierarchy** (highest priority wins):
1. Container-specific overrides
2. Global database settings
3. Application defaults

**Endpoints:**
```http
GET  /api/containers/{id}/settings   # Current overrides (null = using global)
PUT  /api/containers/{id}/settings   # Save overrides
```

**Example — override embedding model for one container:**
```json
PUT /api/containers/{id}/settings
{
  "embedding": {
    "provider": "OpenAI",
    "model": "text-embedding-3-small",
    "dimensions": 1536
  }
}
```

**Override categories:**
- `chunking` — Strategy, chunk size, overlap
- `embedding` — Provider, model, dimensions
- `search` — Mode, TopK, cross-model settings
- `upload` — Max file size, allowed types

> **Warning:** Changing the embedding model for a container that already has indexed documents will cause search quality degradation until all documents are re-embedded. Use the reindex endpoint to trigger re-embedding.

---

## Connection Testing

Test cloud connector configurations before creating a container:

```http
POST /api/containers/test-connection
{
  "connectorType": "S3",
  "connectorConfig": "{ ... }",
  "timeoutSeconds": 15
}
```

**Response:**
```json
{
  "success": true,
  "message": "Connected successfully",
  "details": {
    "bucketName": "company-knowledge",
    "region": "us-east-1",
    "objectsFound": 3,
    "hasMore": true
  },
  "elapsed": "00:00:01.234"
}
```

**Supported connectors:** S3, AzureBlob, MinIO. Filesystem and InMemory don't need connection tests.

**What gets tested:**
- **S3:** `ListObjectsV2` with MaxKeys=5 — verifies bucket exists and credentials have read access
- **AzureBlob:** `GetBlobsAsync` with limit of 5 — verifies container exists and identity has access
- **MinIO:** `ListBuckets` + bucket existence check — verifies endpoint and credentials

---

## Background Sync

The `ConnectorWatcherService` manages file change detection for all containers.

**Filesystem containers:** Real-time `FileSystemWatcher` with 750ms debounce + 5-minute rescan safety net.

**Cloud containers (S3, AzureBlob, MinIO):** 5-minute polling loop that:
1. Lists all remote files via `ListFilesAsync`
2. Compares against database documents and an in-memory snapshot
3. Detects creates, deletes, and changes (after first poll)
4. Pre-registers new files as "Pending" in the database
5. Enqueues ingestion jobs for new/changed files
6. Removes deleted documents from the database

**On-demand sync:** `POST /api/containers/{id}/sync` triggers an immediate sync for cloud containers. Returns:
```json
{
  "batchId": "abc123",
  "totalFiles": 42,
  "enqueuedCount": 5,
  "skippedCount": 37
}
```

**Startup behavior:**
- Existing Filesystem containers start watching automatically
- Existing cloud containers start polling automatically
- InMemory ephemeral containers are swept (all documents deleted)
