# MCP (Model Context Protocol) Server

This directory contains the MCP server implementation that exposes the Connapse Platform as tools for AI agents like Claude.

## Authentication

All MCP endpoints require authentication. Use one of:

- **Agent API Key** (recommended): `X-Api-Key: cnp_<token>`
- **JWT Bearer Token**: `Authorization: Bearer <jwt>`

Agent API keys are created in the admin panel under **Agents**. The key must belong to an agent with `Agent`, `Admin`, or `Owner` role.

## Endpoints

### JSON-RPC 2.0 Endpoint
`POST /mcp`

Standard MCP endpoint following JSON-RPC 2.0 specification. Supports `tools/list`, `tools/call`, and `ping` methods.

### Convenience Endpoint
`GET /mcp/tools`

Returns a list of all available tools in JSON format.

## Available Tools (7)

### 1. container_create

Create a new container for organizing files. Containers provide isolated vector indexes.

**Parameters:**
- `name` (string, required): Container name (lowercase alphanumeric and hyphens, 2-128 chars)
- `description` (string, optional): Optional description for the container

### 2. container_list

List all containers with their document counts.

**Parameters:** None

### 3. container_delete

Delete a container. MinIO and InMemory containers must be empty first. Filesystem, S3, and AzureBlob containers stop being indexed — underlying data is not deleted.

**Parameters:**
- `name` (string, required): Container name to delete

### 4. upload_file

Upload a file to a container. The file will be parsed, chunked, embedded, and made searchable.

**Parameters:**
- `containerId` (string, required): Container ID or name
- `content` (string, required): Base64-encoded file content
- `fileName` (string, required): Original file name with extension
- `path` (string, optional): Destination folder path (e.g., `/docs/2026/`). Default: `/`
- `strategy` (string, optional): Chunking strategy: `Semantic`, `FixedSize`, or `Recursive`. Default: `Semantic`

### 5. list_files

List files and folders in a container at a given path.

**Parameters:**
- `containerId` (string, required): Container ID or name
- `path` (string, optional): Folder path to list. Default: root `/`

### 6. delete_file

Delete a file from a container. This also deletes all associated chunks and vectors.

**Parameters:**
- `containerId` (string, required): Container ID or name
- `fileId` (string, required): File (document) ID to delete

### 7. search_knowledge

Search within a container using semantic, keyword, or hybrid search. Returns relevant document chunks with scores.

**Parameters:**
- `query` (string, required): The search query text
- `containerId` (string, required): Container ID or name to search within
- `mode` (string, optional): Search mode: `Semantic` (vector), `Keyword` (full-text), or `Hybrid` (both). Default: `Hybrid`
- `topK` (number, optional): Number of results to return. Default: `10`
- `path` (string, optional): Filter results to a folder subtree (e.g., `/docs/`)
- `minScore` (number, optional): Minimum similarity score threshold (0.0-1.0). Defaults to server setting (typically 0.5)

## Testing

You can test the MCP server using curl. All requests require authentication:

```bash
# List tools
curl https://localhost:5001/mcp/tools \
  -H "X-Api-Key: cnp_your_agent_key_here"

# List containers
curl -X POST https://localhost:5001/mcp \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: cnp_your_agent_key_here" \
  -d '{
    "jsonrpc": "2.0",
    "method": "tools/call",
    "params": {
      "name": "container_list",
      "arguments": {}
    },
    "id": "1"
  }'

# Search knowledge base
curl -X POST https://localhost:5001/mcp \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: cnp_your_agent_key_here" \
  -d '{
    "jsonrpc": "2.0",
    "method": "tools/call",
    "params": {
      "name": "search_knowledge",
      "arguments": {
        "query": "machine learning best practices",
        "containerId": "my-container",
        "mode": "Hybrid",
        "topK": 5
      }
    },
    "id": "2"
  }'

# Upload a file
curl -X POST https://localhost:5001/mcp \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: cnp_your_agent_key_here" \
  -d '{
    "jsonrpc": "2.0",
    "method": "tools/call",
    "params": {
      "name": "upload_file",
      "arguments": {
        "containerId": "my-container",
        "fileName": "notes.txt",
        "content": "SGVsbG8gV29ybGQh",
        "path": "/documents/"
      }
    },
    "id": "3"
  }'
```

## Using with Claude Desktop

Add the following to your Claude Desktop MCP configuration:

```json
{
  "mcpServers": {
    "connapse": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://localhost:5001/mcp"],
      "env": {
        "API_KEY": "cnp_your_agent_key_here"
      }
    }
  }
}
```

Replace `https://localhost:5001` with your server address and `cnp_your_agent_key_here` with a valid Agent API key.

The AI assistant can then use the exposed tools to create containers, search documents, upload files, and manage your knowledge base.
