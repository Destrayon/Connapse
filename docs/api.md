# API Reference

AIKnowledgePlatform exposes multiple API surfaces for programmatic access:

1. **REST API** — HTTP endpoints for documents, search, and settings
2. **MCP (Model Context Protocol)** — Tool interface for AI agents
3. **CLI** — (Planned) Command-line interface

## REST API

Base URL (development): `https://localhost:5001/api`

All endpoints return JSON. Errors follow RFC 7807 Problem Details format.

### Authentication

**Phase 1**: No authentication (single-user, local deployment)

**Planned**: JWT bearer tokens via `Authorization: Bearer <token>` header

---

## Documents API

### Upload Documents

Upload one or more files for ingestion.

**Endpoint**: `POST /api/documents`

**Content-Type**: `multipart/form-data`

**Request**:
```http
POST /api/documents HTTP/1.1
Content-Type: multipart/form-data; boundary=----WebKitFormBoundary

------WebKitFormBoundary
Content-Disposition: form-data; name="files"; filename="report.pdf"
Content-Type: application/pdf

<binary data>
------WebKitFormBoundary
Content-Disposition: form-data; name="files"; filename="notes.txt"
Content-Type: text/plain

<text content>
------WebKitFormBoundary--
```

**Query Parameters**:
- `collectionId` (optional): Group documents into a collection (GUID)
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
    },
    {
      "documentId": "8e92c0f2-6531-4f1e-9f4d-7a8b3c5e2d1f",
      "fileName": "notes.txt",
      "fileSize": 1234,
      "contentType": "text/plain",
      "status": "Pending",
      "error": null
    }
  ],
  "successCount": 2,
  "failureCount": 0
}
```

**Response** (400 Bad Request):
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "files": ["File type '.exe' is not allowed"]
  }
}
```

**Response** (429 Too Many Requests):
```json
{
  "type": "https://httpstatuses.com/429",
  "title": "Ingestion queue is full",
  "status": 429,
  "detail": "The system is currently processing too many files. Please try again later."
}
```

**Notes**:
- Files are streamed directly to MinIO (not buffered in memory)
- Ingestion happens asynchronously in the background
- Use SignalR (see below) or polling (GET `/api/documents/{id}`) to track progress

---

### List Documents

Retrieve all documents, optionally filtered by collection.

**Endpoint**: `GET /api/documents`

**Query Parameters**:
- `collectionId` (optional): Filter by collection GUID

**Response** (200 OK):
```json
{
  "documents": [
    {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "fileName": "report.pdf",
      "contentType": "application/pdf",
      "fileSize": 245678,
      "contentHash": "a3f5b8c9e1d4...",
      "storagePath": "documents/3fa85f64.../original.pdf",
      "collectionId": null,
      "createdAt": "2026-02-05T10:30:00Z",
      "updatedAt": "2026-02-05T10:30:15Z"
    }
  ],
  "totalCount": 1
}
```

---

### Get Document

Retrieve a single document by ID.

**Endpoint**: `GET /api/documents/{documentId}`

**Path Parameters**:
- `documentId`: Document GUID

**Response** (200 OK):
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "fileName": "report.pdf",
  "contentType": "application/pdf",
  "fileSize": 245678,
  "contentHash": "a3f5b8c9e1d4...",
  "storagePath": "documents/3fa85f64.../original.pdf",
  "collectionId": null,
  "createdAt": "2026-02-05T10:30:00Z",
  "updatedAt": "2026-02-05T10:30:15Z"
}
```

**Response** (404 Not Found):
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.4",
  "title": "Document not found",
  "status": 404,
  "detail": "No document found with ID 3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

---

### Delete Document

Delete a document and all associated chunks/vectors.

**Endpoint**: `DELETE /api/documents/{documentId}`

**Path Parameters**:
- `documentId`: Document GUID

**Response** (204 No Content):
```
(empty body)
```

**Response** (404 Not Found):
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.4",
  "title": "Document not found",
  "status": 404
}
```

**Notes**:
- Cascade deletes chunks, vectors, and removes file from MinIO
- Irreversible operation

---

### Reindex Documents

Trigger reindexing of documents (e.g., after chunking/embedding settings change).

**Endpoint**: `POST /api/documents/reindex`

**Request Body** (optional):
```json
{
  "documentIds": [
    "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "8e92c0f2-6531-4f1e-9f4d-7a8b3c5e2d1f"
  ],
  "force": false
}
```

**Fields**:
- `documentIds` (optional): Reindex specific documents (if omitted, reindexes all)
- `force` (default: `false`): If `true`, re-process even if content hash unchanged

**Response** (202 Accepted):
```json
{
  "batchId": "9d3e7c1a-4b2f-4e8d-9c5a-6f8e1b3d7c2a",
  "totalDocuments": 42,
  "status": "Pending"
}
```

**Notes**:
- Reindexing happens asynchronously
- Documents with unchanged `content_hash` are skipped (unless `force: true`)
- If embedding/chunking settings changed, automatic force reindex

---

## Search API

### Search Documents

Search for documents using semantic, keyword, or hybrid search.

**Endpoint**: `GET /api/search`

**Query Parameters**:
- `q` (required): Search query string
- `mode` (default: `Hybrid`): Search mode (`Semantic` | `Keyword` | `Hybrid`)
- `topK` (default: `10`): Number of results to return (max: 100)
- `minScore` (default: `0.7`): Minimum similarity score (0.0-1.0)
- `collectionId` (optional): Filter by collection GUID

**Response** (200 OK):
```json
{
  "hits": [
    {
      "chunkId": "7f8e9d1c-2b3a-4c5d-8e9f-1a2b3c4d5e6f",
      "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "content": "Machine learning is a subset of artificial intelligence that enables systems to learn and improve from experience without being explicitly programmed.",
      "score": 0.92,
      "metadata": {
        "fileName": "report.pdf",
        "chunkIndex": "5",
        "pageNumber": "12"
      }
    },
    {
      "chunkId": "4a5b6c7d-8e9f-1a2b-3c4d-5e6f7a8b9c0d",
      "documentId": "8e92c0f2-6531-4f1e-9f4d-7a8b3c5e2d1f",
      "content": "Neural networks are computing systems inspired by biological neural networks. They consist of interconnected nodes (neurons) organized in layers.",
      "score": 0.87,
      "metadata": {
        "fileName": "notes.txt",
        "chunkIndex": "2"
      }
    }
  ],
  "totalMatches": 2,
  "durationMs": 87
}
```

**Response** (400 Bad Request):
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "Invalid search parameters",
  "status": 400,
  "errors": {
    "q": ["Query string is required"],
    "topK": ["TopK must be between 1 and 100"]
  }
}
```

---

### Advanced Search (POST)

Search with complex filters and metadata constraints.

**Endpoint**: `POST /api/search`

**Request Body**:
```json
{
  "query": "machine learning algorithms",
  "mode": "Hybrid",
  "topK": 20,
  "minScore": 0.75,
  "collectionId": "9a8b7c6d-5e4f-3a2b-1c0d-9e8f7a6b5c4d",
  "filters": {
    "documentType": "pdf",
    "author": "John Doe"
  }
}
```

**Response**: Same as GET `/api/search`

---

## Settings API

### Get Settings

Retrieve current settings for a specific category.

**Endpoint**: `GET /api/settings/{category}`

**Path Parameters**:
- `category`: One of `embedding`, `chunking`, `search`, `llm`, `storage`, `upload`, `websearch`

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

**Response** (404 Not Found):
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.4",
  "title": "Category not found",
  "status": 404,
  "detail": "Unknown settings category: invalid_category"
}
```

---

### Update Settings

Update settings for a specific category.

**Endpoint**: `PUT /api/settings/{category}`

**Path Parameters**:
- `category`: Settings category (see GET endpoint)

**Request Body** — Example for `chunking`:
```json
{
  "strategy": "Semantic",
  "maxTokens": 512,
  "overlap": 50,
  "similarityThreshold": 0.8
}
```

**Response** (200 OK):
```json
{
  "message": "Settings updated successfully",
  "category": "chunking",
  "updatedAt": "2026-02-05T11:45:00Z"
}
```

**Response** (400 Bad Request):
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "Invalid settings",
  "status": 400,
  "errors": {
    "maxTokens": ["MaxTokens must be between 1 and 8192"]
  }
}
```

**Notes**:
- Changes take effect immediately (no restart required)
- Services using `IOptionsMonitor<T>` will receive updated settings
- Changing `embedding.model` or `chunking` settings triggers reindex warning

---

### Test Connection

Test connectivity to external services (Ollama, MinIO).

**Endpoint**: `POST /api/settings/test-connection`

**Request Body**:
```json
{
  "service": "Ollama",
  "baseUrl": "http://localhost:11434",
  "model": "nomic-embed-text"
}
```

**Response** (200 OK):
```json
{
  "service": "Ollama",
  "success": true,
  "message": "Successfully connected to Ollama. Model 'nomic-embed-text' is available.",
  "latencyMs": 45,
  "details": {
    "version": "0.1.25",
    "models": ["nomic-embed-text", "llama2", "codellama"]
  }
}
```

**Response** (200 OK — Failure):
```json
{
  "service": "MinIO",
  "success": false,
  "message": "Failed to connect to MinIO",
  "error": "Connection refused",
  "latencyMs": 5000
}
```

---

## SignalR Hub (Real-Time)

### Ingestion Progress

Subscribe to real-time ingestion progress updates.

**Hub URL**: `wss://localhost:5001/hubs/ingestion`

**Hub Name**: `IngestionHub`

**Events**:

#### `IngestionProgress`

Sent whenever ingestion progress updates (parsing, chunking, embedding, storing).

**Message**:
```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "state": "Processing",
  "currentPhase": "Embedding",
  "percentComplete": 65.5,
  "errorMessage": null,
  "startedAt": "2026-02-05T10:30:00Z",
  "completedAt": null
}
```

**States**: `Pending`, `Processing`, `Completed`, `Failed`

**Phases**: `Parsing`, `Chunking`, `Embedding`, `Storing`, `Complete`

#### `IngestionCompleted`

Sent when ingestion finishes successfully.

**Message**:
```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "chunkCount": 42,
  "durationMs": 12345,
  "completedAt": "2026-02-05T10:30:15Z"
}
```

#### `IngestionFailed`

Sent when ingestion fails with an error.

**Message**:
```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "error": "Unsupported file format: .exe",
  "failedAt": "2026-02-05T10:30:05Z"
}
```

**Usage Example** (JavaScript):
```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/ingestion")
  .build();

connection.on("IngestionProgress", (update) => {
  console.log(`${update.currentPhase}: ${update.percentComplete}%`);
});

connection.on("IngestionCompleted", (result) => {
  console.log(`Completed! ${result.chunkCount} chunks in ${result.durationMs}ms`);
});

connection.on("IngestionFailed", (error) => {
  console.error(`Failed: ${error.error}`);
});

await connection.start();
```

---

## Model Context Protocol (MCP)

AIKnowledgePlatform exposes an MCP server for AI agent integration.

**Server Name**: `aikp-mcp-server`

**Configuration** (Claude Desktop):
```json
{
  "mcpServers": {
    "aikp": {
      "command": "dotnet",
      "args": ["run", "--project", "src/AIKnowledge.Web", "--mcp"],
      "env": {
        "ASPNETCORE_URLS": "http://localhost:5002"
      }
    }
  }
}
```

### MCP Tools

#### `aikp_search`

Search the knowledge base.

**Input Schema**:
```json
{
  "type": "object",
  "properties": {
    "query": {
      "type": "string",
      "description": "Search query"
    },
    "mode": {
      "type": "string",
      "enum": ["Semantic", "Keyword", "Hybrid"],
      "default": "Hybrid"
    },
    "topK": {
      "type": "number",
      "default": 10,
      "minimum": 1,
      "maximum": 100
    }
  },
  "required": ["query"]
}
```

**Output**:
```json
{
  "hits": [
    {
      "content": "...",
      "score": 0.92,
      "source": "report.pdf (page 12)"
    }
  ],
  "totalMatches": 2
}
```

---

#### `aikp_upload`

Upload a document to the knowledge base.

**Input Schema**:
```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "Local file path"
    },
    "strategy": {
      "type": "string",
      "enum": ["Semantic", "FixedSize", "Recursive"],
      "default": "Semantic"
    }
  },
  "required": ["path"]
}
```

**Output**:
```json
{
  "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "fileName": "report.pdf",
  "status": "Pending",
  "message": "Document uploaded successfully. Ingestion in progress."
}
```

---

#### `aikp_list_documents`

List all documents in the knowledge base.

**Input Schema**:
```json
{
  "type": "object",
  "properties": {
    "collectionId": {
      "type": "string",
      "description": "Filter by collection (optional)"
    }
  }
}
```

**Output**:
```json
{
  "documents": [
    {
      "id": "3fa85f64...",
      "fileName": "report.pdf",
      "fileSize": 245678,
      "createdAt": "2026-02-05T10:30:00Z"
    }
  ],
  "totalCount": 1
}
```

---

#### `aikp_delete_document`

Delete a document and its chunks/vectors.

**Input Schema**:
```json
{
  "type": "object",
  "properties": {
    "documentId": {
      "type": "string",
      "description": "Document GUID to delete"
    }
  },
  "required": ["documentId"]
}
```

**Output**:
```json
{
  "success": true,
  "message": "Document deleted successfully"
}
```

---

## CLI (Planned)

Command-line interface for automation and scripting.

### Install

```bash
dotnet tool install -g aikp-cli
```

### Commands

#### `aikp ingest <path>`

Ingest a file or directory into the knowledge base.

**Options**:
- `--strategy` — Chunking strategy (Semantic | FixedSize | Recursive)
- `--collection` — Collection ID to group documents
- `--wait` — Wait for ingestion to complete (default: true)

**Example**:
```bash
aikp ingest ./docs --strategy Semantic --collection my-docs
```

---

#### `aikp search "<query>"`

Search the knowledge base.

**Options**:
- `--mode` — Search mode (Semantic | Keyword | Hybrid)
- `--topK` — Number of results (default: 10)
- `--format` — Output format (json | table | markdown)

**Example**:
```bash
aikp search "machine learning" --mode Hybrid --topK 5 --format markdown
```

---

#### `aikp list`

List all documents.

**Options**:
- `--collection` — Filter by collection
- `--format` — Output format (json | table)

**Example**:
```bash
aikp list --collection my-docs --format table
```

---

#### `aikp delete <documentId>`

Delete a document.

**Example**:
```bash
aikp delete 3fa85f64-5717-4562-b3fc-2c963f66afa6
```

---

#### `aikp reindex`

Trigger reindexing.

**Options**:
- `--force` — Force reindex even if content unchanged
- `--documents` — Comma-separated document IDs

**Example**:
```bash
aikp reindex --force
```

---

#### `aikp config set <key> <value>`

Update a configuration setting.

**Example**:
```bash
aikp config set embedding.model nomic-embed-text
aikp config set chunking.maxTokens 512
```

---

#### `aikp config get <key>`

Get a configuration value.

**Example**:
```bash
aikp config get embedding.model
```

---

#### `aikp serve`

Start the web server.

**Options**:
- `--port` — HTTP port (default: 5001)
- `--urls` — ASP.NET Core URLs

**Example**:
```bash
aikp serve --port 8080
```

---

## Error Codes

All API errors follow RFC 7807 Problem Details format.

| Status | Type | Description |
|--------|------|-------------|
| 400 | `ValidationError` | Invalid request parameters or body |
| 404 | `NotFound` | Resource not found |
| 409 | `Conflict` | Resource already exists or state conflict |
| 413 | `PayloadTooLarge` | File exceeds `UploadSettings.MaxFileSizeBytes` |
| 415 | `UnsupportedMediaType` | File type not in `UploadSettings.AllowedExtensions` |
| 429 | `TooManyRequests` | Ingestion queue full (backpressure) |
| 500 | `InternalServerError` | Unexpected server error |
| 503 | `ServiceUnavailable` | External service (Ollama, MinIO) unreachable |

**Example Error Response**:
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "Validation Error",
  "status": 400,
  "detail": "One or more validation errors occurred.",
  "errors": {
    "fileName": ["File name cannot be empty"],
    "fileSize": ["File size must be less than 100MB"]
  },
  "traceId": "00-4a8c9d7e6f5b4c3d2a1e9f8d7c6b5a4d-1234567890abcdef-00"
}
```

---

## Rate Limiting

**Phase 1**: No rate limiting (single-user, local deployment)

**Planned**:
- Upload: 100 requests/hour per IP
- Search: 1000 requests/hour per IP
- Settings: 10 updates/hour per user

---

## Versioning

**Current Version**: `v1` (implicit, no version prefix required)

**Future Versioning**: URL-based (`/api/v2/...`) for breaking changes

**Breaking Change Policy**: Major version bump, 6-month deprecation period for previous version

---

## References

- [architecture.md](architecture.md) — System architecture and design
- [deployment.md](deployment.md) — Deployment and configuration
- [.claude/state/api-surface.md](../.claude/state/api-surface.md) — Internal API surface documentation
