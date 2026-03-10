# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- CLI commands: `container stats`, `files list`, `files delete`, `files get` ([#165](https://github.com/Destrayon/Connapse/pull/165), [#166](https://github.com/Destrayon/Connapse/pull/166))
- API endpoints: `GET /api/containers/{id}/stats` and `GET /api/containers/{id}/files/{fileId}/content` ([#162](https://github.com/Destrayon/Connapse/pull/162), [#163](https://github.com/Destrayon/Connapse/pull/163))
- MCP bulk operations: `bulk_upload` and `bulk_delete` tools ([#128](https://github.com/Destrayon/Connapse/pull/128))
- MCP `container_stats` tool for container metrics ([#104](https://github.com/Destrayon/Connapse/pull/104))
- MCP `get_document` tool for full document retrieval ([#99](https://github.com/Destrayon/Connapse/pull/99))
- MCP `textContent` parameter for `upload_file` tool ([#118](https://github.com/Destrayon/Connapse/pull/118))
- Document IDs in MCP `list_files` output ([#100](https://github.com/Destrayon/Connapse/pull/100))
- Truncated vs total match count in MCP search output ([#101](https://github.com/Destrayon/Connapse/pull/101))
- Scores and metadata in MCP search results ([#93](https://github.com/Destrayon/Connapse/pull/93))
- RRF (Reciprocal Rank Fusion) for hybrid search ([#92](https://github.com/Destrayon/Connapse/pull/92))
- Dual-config tsvector keyword search with `websearch_to_tsquery` ([#91](https://github.com/Destrayon/Connapse/pull/91))
- Rate limiting middleware for API endpoints ([#103](https://github.com/Destrayon/Connapse/pull/103))
- Reusable pagination system across listing endpoints ([#111](https://github.com/Destrayon/Connapse/pull/111))
- Docker release package and `ghcr.io` publish workflow ([#113](https://github.com/Destrayon/Connapse/pull/113))
- Cloud connector integration tests using LocalStack and Azurite ([#114](https://github.com/Destrayon/Connapse/pull/114))
- Dynamic test badge from CI results ([#105](https://github.com/Destrayon/Connapse/pull/105))
- Branding logos and favicon ([#120](https://github.com/Destrayon/Connapse/pull/120))
- Centralized versioning via MinVer git tags ([#119](https://github.com/Destrayon/Connapse/pull/119))
- PR size check and auto-labeling workflows ([#63](https://github.com/Destrayon/Connapse/pull/63))
- Integration tests for endpoints, ingestion pipeline, identity security, and semantic chunker ([#109](https://github.com/Destrayon/Connapse/pull/109), [#110](https://github.com/Destrayon/Connapse/pull/110), [#112](https://github.com/Destrayon/Connapse/pull/112))

### Changed

- Migrated MCP server to official C# MCP SDK ([#66](https://github.com/Destrayon/Connapse/pull/66))
- Renamed `container_delete` MCP parameter from `name` to `containerId` ([#98](https://github.com/Destrayon/Connapse/pull/98))
- Replaced N+1 query in user listing with batch JOIN ([#102](https://github.com/Destrayon/Connapse/pull/102))

### Fixed

- Path traversal vulnerability in file upload ([#60](https://github.com/Destrayon/Connapse/pull/60))
- Write guards enforced on MCP tools and Filesystem permission flags ([#123](https://github.com/Destrayon/Connapse/pull/123))
- MinIO container isolation via auto-scoping to container ID prefix ([#121](https://github.com/Destrayon/Connapse/pull/121))
- Empty parent folder cleanup after file deletion ([#123](https://github.com/Destrayon/Connapse/pull/123))
- Folder creation during cloud sync and MCP uploads ([#86](https://github.com/Destrayon/Connapse/pull/86))
- Keyword search switched from AND to OR matching for multi-term queries ([#85](https://github.com/Destrayon/Connapse/pull/85))
- Silent exception in MCP file deletion replaced with proper logging ([#61](https://github.com/Destrayon/Connapse/pull/61))
- Error visibility for fire-and-forget admin operations ([#68](https://github.com/Destrayon/Connapse/pull/68))
- SSL certificate bypass scoped to localhost only ([#160](https://github.com/Destrayon/Connapse/pull/160))
- `EnsureAuthenticated` added to CLI container/search/upload/reindex commands ([#164](https://github.com/Destrayon/Connapse/pull/164))
- LICENSE copyright updated to Connapse Contributors ([#161](https://github.com/Destrayon/Connapse/pull/161))
- Stale project names and URLs replaced with Connapse/Destrayon ([#168](https://github.com/Destrayon/Connapse/pull/168))

### Removed

- InMemory (ephemeral) connector ([#94](https://github.com/Destrayon/Connapse/pull/94))
- Legacy `IngestFromPathAsync` from `IKnowledgeIngester` ([#69](https://github.com/Destrayon/Connapse/pull/69))
- Fake `SearchStreamAsync` that buffered before yielding ([#67](https://github.com/Destrayon/Connapse/pull/67))
- Reflection-based MinIO settings extraction fallback ([#62](https://github.com/Destrayon/Connapse/pull/62))
- Legacy ingestion worker fallback bypassing connector system ([#84](https://github.com/Destrayon/Connapse/pull/84))

## [0.3.1] - 2026-03-03

### Added

- S3 and Azure Blob storage connectors with sync-on-demand
- Filesystem connector with FileSystemWatcher and real-time file browser
- IConnector abstraction with ConnectorFactory for all 5 connector types
- Per-container settings overrides via IContainerSettingsResolver
- User cloud identities with Azure OAuth2 and AWS SSO device auth flow
- Cloud scope discovery and query-time RBAC enforcement
- OpenAI and Azure OpenAI embedding providers
- Multi-dimension vector support with unconstrained `vector` column and partial IVFFlat indexes
- ILlmProvider abstraction with Ollama, OpenAI, Azure OpenAI, and Anthropic providers
- Agentic search with iterative LLM-driven retrieval, HyDE enrichment, and corrective RAG
- Cross-embedding model search and reranking with model discovery
- Cloud container background polling (S3, Azure Blob, MinIO) on 5-minute interval
- Azure AD settings UI with connection testing
- Provider-specific API key fields and IOptionsSnapshot support
- Profile page with cloud identity management
- ConnectorWatcherService for filesystem and cloud container monitoring

### Changed

- Vector column changed from `vector(768)` to unconstrained `vector` for mixed embedding dimensions
- SearchMode expanded: Semantic, Keyword, Hybrid, Agentic
- CrossEncoderReranker refactored from raw Ollama HTTP to ILlmProvider

### Fixed

- CLI self-update now works for global tool installs
- Log forging vulnerability in ConnectorWatcherService (sanitized user-controlled values)
- AWS account IDs removed from logs (CodeQL compliance)

### Removed

- RS256/JWKS support (Connapse no longer acts as OIDC provider; HS256-only JWT)
- Unimplemented settings: WebSearchSettings, StorageSettings, and 11 dead properties

## [0.2.2] - 2026-02-27

### Added

- CLI self-update command (`connapse update`)
- CLI version display

## [0.2.1] - 2026-02-27

### Added

- Three-tier authentication system (cookie, JWT, PAT)
- Invite-only user registration
- Personal Access Token management UI
- Agent entities and agent management UI
- Audit logging
- CLI with PKCE authentication flow
- REST API, Search UI, CLI, and MCP server access surfaces
- Hybrid search (vector, keyword, reranking) with configurable cross-encoder
- Document ingestion pipeline with parsing, chunking, and background processing
- Ollama embeddings and pgvector-backed vector search
- Runtime-mutable settings system with database backing
- Reindex service with content-hash comparison and settings-change detection
- Container-based file browser with API, UI, CLI, and MCP support
- MinIO and Docker Compose infrastructure
- CI workflow with test categorization and Testcontainers support
- SECURITY.md and Dependabot configuration
- CodeQL security scanning

### Fixed

- Release workflow binary name and NuGet key guard
- CodeQL security alerts for log forging and workflow permissions
- pgvector parameter binding and configurable minScore threshold

[Unreleased]: https://github.com/Destrayon/Connapse/compare/v0.3.1...HEAD
[0.3.1]: https://github.com/Destrayon/Connapse/compare/v0.2.2...v0.3.1
[0.2.2]: https://github.com/Destrayon/Connapse/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/Destrayon/Connapse/releases/tag/v0.2.1
