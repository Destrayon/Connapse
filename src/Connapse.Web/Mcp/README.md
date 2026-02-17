# MCP (Model Context Protocol) Server

This directory contains the MCP server implementation that exposes the Connapse Platform as tools for AI agents like Claude.

## Endpoints

### JSON-RPC 2.0 Endpoint
`POST /mcp`

Standard MCP endpoint following JSON-RPC 2.0 specification.

### Convenience Endpoint
`GET /mcp/tools`

Returns a list of all available tools in JSON format.

## Available Tools

### 1. search_knowledge

Search the knowledge base using semantic, keyword, or hybrid search.

**Parameters:**
- `query` (required): Search query text
- `mode` (optional): "Semantic", "Keyword", or "Hybrid" (default: "Hybrid")
- `topK` (optional): Number of results to return (default: 10)
- `collectionId` (optional): Filter by collection ID

**Example:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "search_knowledge",
    "arguments": {
      "query": "machine learning best practices",
      "mode": "Hybrid",
      "topK": 5
    }
  },
  "id": "1"
}
```

### 2. list_documents

List all documents in the knowledge base.

**Parameters:**
- `collectionId` (optional): Filter by collection ID

**Example:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "list_documents",
    "arguments": {
      "collectionId": "research"
    }
  },
  "id": "2"
}
```

### 3. ingest_document

Add a document to the knowledge base.

**Parameters:**
- `path` (required): Virtual path for the document (e.g., "/documents/report.pdf")
- `content` (required): Base64-encoded document content
- `fileName` (required): Original file name with extension
- `collectionId` (optional): Collection ID to organize documents
- `strategy` (optional): "Semantic", "FixedSize", or "Recursive" (default: "Semantic")

**Example:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "ingest_document",
    "arguments": {
      "path": "/documents/report.pdf",
      "content": "SGVsbG8gV29ybGQh",
      "fileName": "report.pdf",
      "collectionId": "research",
      "strategy": "Semantic"
    }
  },
  "id": "3"
}
```

## Testing

You can test the MCP server using curl:

```bash
# List tools
curl https://localhost:5001/mcp/tools

# Search knowledge base
curl -X POST https://localhost:5001/mcp \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "tools/call",
    "params": {
      "name": "search_knowledge",
      "arguments": {
        "query": "test query",
        "mode": "Hybrid",
        "topK": 5
      }
    },
    "id": "1"
  }'
```

## Using with Claude

The MCP server allows Claude and other AI assistants to interact with your knowledge base. Configure your AI assistant to connect to your deployed instance:

```
https://your-server.com/mcp
```

Replace `your-server.com` with your actual server address. For local development, use `https://localhost:5001/mcp`.

The AI assistant can then use the exposed tools to search documents, list available content, and add new documents to your knowledge base.
