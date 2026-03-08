# Connapse

**Open-source AI-powered knowledge management platform. Transform documents into searchable knowledge for AI agents.**

Connapse provides container-based document organization, hybrid search (vector + keyword), multi-format ingestion, and four access surfaces: Web UI, REST API, CLI, and MCP server.

## Key Features

- **Container-Based Organization** -- Isolated projects with S3-like folder hierarchies
- **5 Connector Types** -- MinIO (default), Filesystem (live watch), S3, Azure Blob, InMemory (ephemeral)
- **Hybrid Search** -- Vector similarity + keyword full-text with Reciprocal Rank Fusion and cross-model support
- **Multi-Format Ingestion** -- PDF, Office documents (DOCX, PPTX), Markdown, plain text, CSV, JSON, XML, YAML
- **Real-Time Processing** -- Background ingestion with live progress updates via SignalR
- **Multi-Provider AI** -- Embeddings (Ollama, OpenAI, Azure OpenAI) + LLM (Ollama, OpenAI, Azure OpenAI, Anthropic)
- **Three-Tier Auth** -- Cookie sessions, Personal Access Tokens, and JWT with role-based access control
- **Cloud Identity** -- AWS IAM Identity Center (device auth) + Azure AD (OAuth2+PKCE)
- **MCP Server** -- Claude Desktop integration with 8 tools for container and document management
- **Fully Dockerized** -- PostgreSQL + pgvector, MinIO, optional Ollama

## Wiki Pages

- [[Getting Started]] -- Prerequisites, Docker Compose quickstart, first walkthrough
- [[Deployment Guide]] -- Services, environment variables, volumes, production hardening
- [[CLI Reference]] -- Full command reference with all flags and options
- [[MCP Integration]] -- MCP tools, parameters, and Claude Desktop configuration

## Links

- [Source Code](https://github.com/Destrayon/Connapse)
- [API Reference](https://github.com/Destrayon/Connapse/blob/main/docs/api.md)
- [Architecture Guide](https://github.com/Destrayon/Connapse/blob/main/docs/architecture.md)
- [Connectors Guide](https://github.com/Destrayon/Connapse/blob/main/docs/connectors.md)
- [Security Policy](https://github.com/Destrayon/Connapse/blob/main/SECURITY.md)
- [Contributing](https://github.com/Destrayon/Connapse/blob/main/CONTRIBUTING.md)
