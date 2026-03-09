# MCP (Model Context Protocol) Server

This directory contains the MCP server implementation that exposes the Connapse Platform as tools for AI agents. Built on the [official C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk) (`ModelContextProtocol` NuGet package).

## Transport

The server supports **Streamable HTTP** (current MCP standard) and **SSE** (legacy, for backward compatibility). Both are available at the `/mcp` endpoint.

## Authentication

All MCP endpoints require authentication. Use one of:

- **Agent API Key** (recommended): `X-Api-Key: cnp_<token>`
- **JWT Bearer Token**: `Authorization: Bearer <jwt>`

Agent API keys are created in the admin panel under **Agents**.

## Available Tools (11)

Tools are defined in `McpTools.cs` using `[McpServerTool]` attributes and are auto-discovered by the SDK.

### 1. container_create

Create a new container for organizing files. Containers provide isolated vector indexes.

**Parameters:**
- `name` (string, required): Container name (lowercase alphanumeric and hyphens, 2-128 chars)
- `description` (string, optional): Optional description for the container

### 2. container_list

List all containers with their document counts.

**Parameters:** None

### 3. container_delete

Delete a container. MinIO containers must be empty first. Filesystem, S3, and AzureBlob containers just stop being indexed — underlying data is not deleted.

**Parameters:**
- `containerId` (string, required): Container ID or name

### 4. container_stats

Get statistics for a container: document counts by status, chunk count, storage size, embedding model, and last indexed time.

**Parameters:**
- `containerId` (string, required): Container ID or name

### 5. upload_file

Upload a file to a container. The file will be parsed, chunked, embedded, and made searchable. Provide either `content` (base64) or `textContent` (raw text), not both.

**Parameters:**
- `containerId` (string, required): Container ID or name
- `content` (string, optional): Base64-encoded file content. For binary files (PDF, DOCX, images). Mutually exclusive with `textContent`.
- `textContent` (string, optional): Raw text content for text-based files (Markdown, TXT, CSV, JSON, etc.). Mutually exclusive with `content`.
- `fileName` (string, required): Original file name with extension
- `path` (string, optional): Destination folder path (e.g., `/docs/2026/`). Default: `/`
- `strategy` (string, optional): Chunking strategy: `Semantic`, `FixedSize`, or `Recursive`. Default: `Semantic`

### 6. bulk_upload

Upload multiple files to a container in one operation. Each file is parsed, chunked, embedded, and made searchable. Returns per-file results.

**Parameters:**
- `containerId` (string, required): Container ID or name
- `files` (string, required): JSON array of file objects. Each object: `{"filename":"name.txt", "content":"...", "encoding":"text|base64", "folderPath":"/optional/"}`. Max 100 files.

### 7. list_files

List files and folders in a container at a given path.

**Parameters:**
- `containerId` (string, required): Container ID or name
- `path` (string, optional): Folder path to list. Default: root `/`

### 8. get_document

Retrieve the full text content of a document by ID or path. For text files the original content is returned; for binary formats (PDF, DOCX, PPTX) the extracted text is returned.

**Parameters:**
- `containerId` (string, required): Container ID or name
- `fileId` (string, required): Document ID (UUID) or virtual path (e.g., `/docs/readme.md`)

### 9. delete_file

Delete a file from a container. This also deletes all associated chunks and vectors.

**Parameters:**
- `containerId` (string, required): Container ID or name
- `fileId` (string, required): File (document) ID to delete

### 10. bulk_delete

Delete multiple files from a container in one operation. Returns per-file results.

**Parameters:**
- `containerId` (string, required): Container ID or name
- `fileIds` (string, required): JSON array of file (document) IDs to delete, e.g. `["id1","id2"]`. Max 100.

### 11. search_knowledge

Search within a container using semantic, keyword, or hybrid search. Returns relevant document chunks with scores.

**Parameters:**
- `query` (string, required): The search query text
- `containerId` (string, required): Container ID or name to search within
- `mode` (string, optional): Search mode: `Semantic` (vector), `Keyword` (full-text), or `Hybrid` (both). Default: `Hybrid`
- `topK` (integer, optional): Number of results to return. Default: `10`
- `path` (string, optional): Filter results to a folder subtree (e.g., `/docs/`)
- `minScore` (number, optional): Minimum similarity score threshold (0.0-1.0). Defaults to server setting.

## Write Guards

Write operations (`upload_file`, `bulk_upload`, `delete_file`, `bulk_delete`) are subject to container write guards:

- **MinIO / InMemory**: Full read/write access.
- **Filesystem**: Per-container `allowUpload` / `allowDelete` permission flags (default: allowed). Configurable in the connector config.
- **S3 / AzureBlob**: Read-only. These containers are synced from the source and do not allow uploads or deletes through MCP. The tools return an error explaining why.

## Using with Claude Desktop

Add the following to your Claude Desktop MCP configuration (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "connapse": {
      "type": "streamableHttp",
      "url": "https://localhost:5001/mcp",
      "headers": {
        "X-Api-Key": "cnp_your_agent_key_here"
      }
    }
  }
}
```

Replace `https://localhost:5001` with your server address and `cnp_your_agent_key_here` with a valid Agent API key.

## Testing with MCP Inspector

You can test the server using [MCP Inspector](https://github.com/modelcontextprotocol/inspector):

```bash
npx @modelcontextprotocol/inspector --url https://localhost:5001/mcp --header "X-Api-Key: cnp_your_agent_key_here"
```

The inspector provides an interactive UI to list and call tools, inspect schemas, and debug responses.
