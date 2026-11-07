# Changelog

All notable changes to Connapse are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Upload a whole folder from the browser: choose or drop a folder and everything inside it uploads with its subfolder paths, and the files that fail are listed. `POST /api/containers/{id}/files` takes `paths`, one folder per file (CON-257)
- Link-aware search: with `EnableLinkSearch` on, search also returns documents its top results link to, one step out. Links come from GitHub (pull request closes issue, sub-issue parent), Jira (issue links, epic, parent, fix version, linked Confluence pages), Confluence (parent page, labels, page links, mentioned Jira issues) and code files. Linked documents pass the same permission check as ordinary results (CON-33, CON-34)
- Code files: `.cs`, `.ts`/`.mts`/`.cts`/`.tsx` and `.py`/`.pyi` are parsed with tree-sitter, cut into one passage per definition, and linked by definitions, imports, calls and base types (CON-36, CON-108, CON-109)
- Code MCP tools: `code_find_symbol`, `code_references`, `code_impact` and `code_repo_map` (CON-37)
- Agent memory MCP tools: `memory_remember`, `memory_correct`, `memory_forget`, `memory_recall` and `memory_timeline`, storing dated facts with sources and keeping their history (CON-39)
- Embedding size setting works for any OpenAI-compatible model: "Ask the model for this size" sends `dimensions` to models such as Qwen3-Embedding, and vectors of the wrong size are refused with an error naming the setting to change (#666, reported by @marcusyoung)

### Changed
- **Breaking:** Container memory is removed, because agents answered no better with it than without it (0.68 with a check on what they saved and 0.61 without, against 0.70 with memory off). Memory now lives only in personal and team spaces. A migration deletes every fact and star on documents containers, including facts shared with the team there, **which can't be undone**, so back up the database before upgrading if you need them. Facts already in personal and team spaces are untouched. Naming a documents container in `memory_remember`, `memory_recall`, `memory_timeline` or the memory REST routes is refused, `GET /api/memory/spaces` drops `kind=container` and `counts.containers`, and the Memory tab on documents containers is gone (CON-255)
- **Breaking:** `GET` and `PUT /api/containers/{id}/settings` return only the search settings the container chose, and `PUT` stores only the search settings the body names; the rest follow the deployment (CON-157)
- **Breaking:** `LinkMaxHops` defaults to 1 instead of 2 (CON-33)
- Results whose scores tie are ordered by document path, so re-indexing gives the same ranking (CON-152, CON-158)

### Fixed
- Audit entries for actions taken by agents were dropped; they are now recorded under the user the agent acts for
- Changing one link-search setting on a container no longer resets its other search settings (CON-157)
- A newly built vector index is used right away instead of being skipped on stale table statistics (CON-34)
- Deleted documents no longer come back: a file whose storage delete fails is retried instead of only logged, and a sync running alongside a folder delete can no longer restore its files (#378, reported and traced by @marcusyoung)
- The Files tab lists every file in a container's root: it used to fetch only the 50 newest documents in the whole container, so after many uploads into a subfolder the older root files disappeared from view (#812, reported by @marcusyoung)
- Uploading a file that is already in the container replaces it instead of failing to index with `23505 duplicate key ... idx_documents_container_path` (#812, reported by @marcusyoung)

## [v0.3.2] - 2026-03-15

### Added
- CLI `files list`, `files delete`, `files get` commands (#166)
- CLI `container stats` command (#165)
- CLI `--pre` flag for update command to install prerelease builds (#231)
- CLI `--help` and `-h` flags for root and all subcommands (#229)
- API `GET /api/containers/{id}/files/{fileId}/content` endpoint (#163)
- API `GET /api/containers/{id}/stats` endpoint (#162)
- API security headers middleware (X-Content-Type-Options, X-Frame-Options, etc.) (#181)
- Bulk MCP tools: `bulk_upload` and `bulk_delete` for batch operations (#128)
- `get_document` MCP tool for full document retrieval (#99)
- `container_stats` MCP tool (#104)
- Rate limiting middleware with per-user and per-IP policies (#103)
- Pagination for listing endpoints (#111)
- MCP search results now include scores and metadata (#93)
- Document IDs in `list_files` MCP output (#100)
- Truncated vs total match count in MCP search (#101)
- Raw text upload support in MCP `upload_file` tool (#118)
- File type validation: reject uploads with unsupported extensions (#194)
- Input validation hardening: centralized `ValidationConstants`, search param bounds, agent field validation, path depth limits (#228)
- `IUploadService` — unified upload pipeline shared by API and MCP endpoints (#214)
- Manual re-embed button in embedding settings (#215)
- Cloud connector integration tests (LocalStack + Azurite) (#114)
- Docker release package and ghcr.io publish (#113)
- Unit tests for ingestion pipeline, identity services, SemanticChunker (#108, #109, #110)
- CloudIdentity and Search integration tests (#112)
- Dynamic tests badge from CI results (#105)
- Connapse branding logos and favicon (#120)
- Convex Combination fusion for hybrid search with configurable alpha (#92)
- DBSF (Distribution-Based Score Fusion) as alternative outlier-robust fusion method
- AutoCut: automatic result trimming via score gap detection

### Fixed
- **Security**: empty `X-Api-Key` header no longer falls through to cookie auth (#224)
- **Security**: path traversal in upload filenames rejected (#183)
- **Security**: control characters rejected in filenames
- Uppercase container names now return 400 instead of 409 (#225)
- Filenames exceeding 255 characters rejected with 400 (#221)
- Zero-byte file uploads rejected with 400 (#193)
- Stale error message cleared when file ingestion succeeds (#242)
- CLI home directory resolution uses `USERPROFILE` env var on Windows (#241)
- CLI container name resolution uses direct lookup instead of broken paginated list (#232)
- CLI `EnsureAuthenticated` missing on container/search/upload/reindex commands (#164)
- CLI source-generated JSON context for trim-safe deserialization (#186)
- CLI pagination parameters added to container list (#185)
- MCP `list_files` works at paths without explicit folder records (#191)
- MCP `delete_file` cancels in-flight ingestion jobs (#146)
- MCP `container_delete` stops watchers and writes audit log (#147)
- API pagination `take` parameter enforces min/max bounds (#188)
- API returns 404 for nonexistent settings category (#192)
- Cookie `Secure` flag adapts to HTTP/HTTPS scheme (#187)
- SSL certificate bypass scoped to localhost only (#160)
- Bootstrap Icons self-hosted instead of CDN (#240)
- LICENSE copyright updated to Connapse Contributors (#161)
- Stale project names and URLs replaced (#168)
- MinIO containers isolated by auto-scoping to container ID prefix (#121)
- Empty parent folders cleaned up after file deletion (#123)
- Folder entries created when uploading files via MCP (#86)
- Keyword search improved for exact term matches (#85)
- Keyword search for technical terms with dual-config tsvector (#91)
- Fire-and-forget admin operations now surface errors (#68)
- N+1 query in user listing replaced with batch JOIN (#102)
- MCP Server file deletion failures now logged instead of swallowed (#61)

### Changed
- Upload pipeline unified via `IUploadService` — API and MCP share same validation and ingestion logic (#214)
- `bulk_delete` delegates to `delete_file` for consistent behavior (#147)
- CLI release assets renamed to `connapse-cli-*` for clarity (#220)
- Version centralized via MinVer git tags (#119)
- `container_delete` parameter standardized to `containerId` (#98)
- InMemory (ephemeral) connector removed (#94)
- Legacy `IngestionWorker` fallback bypassing connector system removed (#84)
- Dead `IngestFromPathAsync` removed from `IKnowledgeIngester` (#69)
- Fake `SearchStreamAsync` that buffered before yielding removed (#67)
- Reflection-based MinIO settings extraction removed (#62)
- MCP server migrated to official C# MCP SDK (#66)

### Documentation
- Agent API key scope model and access boundaries clarified (#226)
- First-time registration flow documented in deployment guide
- `.env.example` expanded with all configuration variables
- Pagination query parameters documented for all list endpoints (#184)
- Bulk API tools documented (#169)
- Container write guard behavior documented (#167)
- MCP documentation updated for all 11 tools (#159)
- README rewritten with benefit-first positioning and GIF hero section

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

[Unreleased]: https://github.com/Destrayon/Connapse/compare/v0.3.2...HEAD
[v0.3.2]: https://github.com/Destrayon/Connapse/compare/v0.3.1...v0.3.2
[v0.3.1]: https://github.com/Destrayon/Connapse/compare/v0.2.2...v0.3.1
[v0.2.2]: https://github.com/Destrayon/Connapse/compare/v0.2.1...v0.2.2
[v0.2.1]: https://github.com/Destrayon/Connapse/compare/v0.2.0...v0.2.1
