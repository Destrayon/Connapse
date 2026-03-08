# MCP Integration

Connapse includes a built-in [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server for integration with Claude Desktop and other MCP-compatible clients.

## Setup

### 1. Create an Agent

1. Log in to the Connapse Web UI as an admin
2. Navigate to **Admin > Agents** (`/admin/agents`)
3. Create a new agent and generate an API key
4. Copy the API key -- it is shown only once

### 2. Configure Claude Desktop

Add the following to your Claude Desktop configuration file:

**macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
**Windows:** `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "connapse": {
      "url": "http://localhost:5001/mcp",
      "headers": {
        "X-Api-Key": "YOUR_AGENT_API_KEY"
      }
    }
  }
}
```

Replace `YOUR_AGENT_API_KEY` with the API key generated in step 1. Adjust the URL if your Connapse instance runs on a different host or port.

### 3. Restart Claude Desktop

After saving the configuration, restart Claude Desktop. The Connapse tools will appear in the tools menu.

## Available Tools

### `container_create`

Create a new container for organizing files.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Container name (lowercase alphanumeric and hyphens, 2-128 chars) |
| `description` | string | No | Optional description for the container |

### `container_list`

List all containers with their document counts. No parameters.

### `container_delete`

Delete a container. MinIO containers must be empty first. Filesystem, S3, and AzureBlob containers just stop being indexed -- underlying data is not deleted.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `containerId` | string | Yes | Container ID or name |

### `search_knowledge`

Search within a container using semantic, keyword, or hybrid search. Returns relevant document chunks with scores.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `query` | string | Yes | The search query text |
| `containerId` | string | Yes | Container ID or name to search within |
| `mode` | string | No | Search mode: `Semantic`, `Keyword`, or `Hybrid` (default: `Hybrid`) |
| `topK` | int | No | Number of results to return (default: 10) |
| `path` | string | No | Filter results to a folder subtree (e.g., `/docs/`) |
| `minScore` | float | No | Minimum similarity score floor, 0.0-1.0 (default: from server settings) |

### `list_files`

List files and folders in a container at a given path.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `containerId` | string | Yes | Container ID or name |
| `path` | string | No | Folder path to list (default: root `/`) |

### `upload_file`

Upload a file to a container. The file will be parsed, chunked, embedded, and made searchable.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `containerId` | string | Yes | Container ID or name |
| `content` | string | Yes | Base64-encoded file content |
| `fileName` | string | Yes | Original file name with extension |
| `path` | string | No | Destination folder path (e.g., `/docs/2026/`) |
| `strategy` | string | No | Chunking strategy: `Semantic` (default), `FixedSize`, or `Recursive` |

### `delete_file`

Delete a file from a container. Also deletes all associated chunks and vectors.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `containerId` | string | Yes | Container ID or name |
| `fileId` | string | Yes | File (document) ID to delete |

### `get_document`

Retrieve the full text content of a document by ID or path. For text files the original content is returned; for binary formats (PDF, DOCX, PPTX) the extracted text is returned.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `containerId` | string | Yes | Container ID or name |
| `fileId` | string | Yes | Document ID (UUID) or virtual path (e.g., `/docs/readme.md`) |

### `container_stats`

Get statistics for a container: document counts by status, chunk count, storage size, embedding model, and last indexed time.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `containerId` | string | Yes | Container ID or name |

## Authentication

The MCP server uses agent API key authentication via the `X-Api-Key` header. Agent identities are managed separately from user accounts -- create them in the admin UI under **Admin > Agents**.

Agents have the `Agent` role, which grants read and write access to containers, documents, and search. Admin-only operations (user management, settings) are not available to agents.

## Supported File Formats

Files uploaded via the `upload_file` tool support the same formats as the Web UI and REST API:

- **Text:** `.txt`, `.md`, `.markdown`, `.csv`, `.log`, `.json`, `.xml`, `.yaml`, `.yml`
- **Binary (parsed):** `.pdf`, `.docx`, `.pptx`

## Container Resolution

All tools that accept a `containerId` parameter accept either:

- A container **ID** (UUID format)
- A container **name** (lowercase string)

The tool resolves names to IDs automatically.
