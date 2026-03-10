# Changelog

All notable changes to Connapse are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- CLI `files list`, `files delete`, `files get` commands (#166)
- CLI `container stats` command (#165)
- API `GET /api/containers/{id}/files/{fileId}/content` endpoint (#163)
- API `GET /api/containers/{id}/stats` endpoint (#162)
- Bulk MCP tools: `bulk_upload` and `bulk_delete` for batch operations (#128)
- `get_document` MCP tool for full document retrieval (#99)
- `container_stats` MCP tool (#104)
- Rate limiting middleware with per-user and per-IP policies (#103)
- Pagination for listing endpoints (#111)
- MCP search results now include scores and metadata (#93)
- Document IDs in `list_files` MCP output (#100)
- Truncated vs total match count in MCP search (#101)
- Raw text upload support in MCP `upload_file` tool (#118)
- Cloud connector integration tests (LocalStack + Azurite) (#114)
- Docker release package and ghcr.io publish (#113)
- Unit tests for ingestion pipeline, identity services, SemanticChunker (#108, #109, #110)
- CloudIdentity and Search integration tests (#112)
- Dynamic tests badge from CI results (#105)
- Connapse branding logos and favicon (#120)
- GitHub Wiki pages (#115)
- RRF fusion as built-in hybrid search step (#92)
- Connector type support in MCP `container_create` (#145, planned)

### Fixed
- CLI `EnsureAuthenticated` missing on container/search/upload/reindex commands (#164)
- SSL certificate bypass now scoped to localhost only (#160)
- LICENSE copyright updated to Connapse Contributors (#161)
- Stale project names and URLs replaced (#168)
- MinIO containers isolated by auto-scoping to container ID prefix (#121)
- Empty parent folders cleaned up after file deletion (#123)
- Folder entries created when uploading files via MCP (#86)
- Keyword search improved for exact term matches (#85)
- Keyword search for technical terms with dual-config tsvector (#91)
- Path traversal vulnerability and MinIO connection tester wiring (#60)
- Fire-and-forget admin operations now surface errors (#68)
- N+1 query in user listing replaced with batch JOIN (#102)
- MCP Server file deletion failures now logged instead of swallowed (#61)
- Log injection sanitization in ConnectorWatcherService (#v0.3.1)

### Changed
- Version centralized via MinVer git tags (#119)
- `container_delete` parameter standardized to `containerId` (#98)
- InMemory (ephemeral) connector removed (#94)
- Legacy `IngestionWorker` fallback bypassing connector system removed (#84)
- Dead `IngestFromPathAsync` removed from `IKnowledgeIngester` (#69)
- Fake `SearchStreamAsync` that buffered before yielding removed (#67)
- Reflection-based MinIO settings extraction removed (#62)
- MCP server migrated to official C# MCP SDK (#66)

### Documentation
- Bulk API tools documented (#169)
- Container write guard behavior documented (#167)
- MCP documentation updated for all 11 tools (#159)

## [v0.3.1] - 2026-03-03

### Added
- 4 connector types: MinIO, Filesystem (FileSystemWatcher), S3 (IAM-only), Azure Blob (managed identity)
- Per-container settings overrides (chunking, embedding, search, upload)
- Cloud identity linking: AWS IAM Identity Center (device auth) + Azure AD (OAuth2+PKCE)
- IAM-derived scope enforcement — cloud permissions as source of truth
- Multi-provider embeddings: Ollama, OpenAI, Azure OpenAI
- Multi-provider LLM: Ollama, OpenAI, Azure OpenAI, Anthropic
- Multi-dimension vector support with partial IVFFlat indexes per model
- Cross-model search with automatic Semantic-to-Hybrid fallback
- Background sync: FileSystemWatcher for local, 5-min polling for cloud containers
- Connection testing for all providers (S3, Azure Blob, MinIO, LLM, embeddings, AWS SSO, Azure AD)
- Provider-specific API keys and IOptionsSnapshot for settings
- 457 passing tests (unit + integration)

### Fixed
- CLI self-update for global tool installs
- AWS account IDs removed from logs (CodeQL)
- User-controlled values sanitized in ConnectorWatcherService logs

## [v0.2.2] - 2026-02-28

### Added
- CLI `self-update` command
- CLI `--version` flag

## [v0.2.1] - 2026-02-27

### Fixed
- Release workflow: correct binary name and NuGet key guard
- CodeQL security alerts: log injection and PII exposure
- Input sanitization for user-provided values

### Security
- SECURITY.md updated to reflect v0.2.0 auth implementation

## v0.2.0 - 2026-02-27

### Added
- Three-tier authentication: Cookie sessions + Personal Access Tokens + JWT (HS256)
- Role-based access control (Admin / Editor / Viewer / Agent)
- Invite-only user registration (admin-controlled)
- First-class agent entities with API key lifecycle management
- Agent management UI and PAT management UI
- Audit logging for uploads, deletes, and container operations
- CLI auth commands (`auth login`, `auth whoami`, `auth pat`)
- CLI PKCE authentication flow
- GitHub Actions release pipeline (native binaries + NuGet global tool)
- 256 passing tests (unit + integration)
- `/health` endpoint

### Fixed
- PAT UX: auto-revoke old tokens on re-login

## v0.1.0 - 2026-02-17

### Added
- Document ingestion pipeline (PDF, Office documents, Markdown, plain text)
- Hybrid search: vector similarity + keyword full-text search
- Container-based file browser with folder hierarchies
- Web UI (Blazor Server)
- REST API
- Command-line interface
- MCP server for Claude Desktop integration
- PostgreSQL + pgvector for vector storage
- MinIO (S3-compatible) object storage
- Ollama integration for local embeddings
- Docker Compose deployment

[Unreleased]: https://github.com/Destrayon/Connapse/compare/v0.3.1...HEAD
[v0.3.1]: https://github.com/Destrayon/Connapse/compare/v0.2.2...v0.3.1
[v0.2.2]: https://github.com/Destrayon/Connapse/compare/v0.2.1...v0.2.2
[v0.2.1]: https://github.com/Destrayon/Connapse/compare/v0.2.0...v0.2.1
