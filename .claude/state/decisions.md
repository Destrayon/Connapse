# Architectural Decisions

Record significant decisions with context and rationale. Future sessions should check this before making architectural changes.

---

### 2026-02-27 — v0.3.0 Architecture: Connector System + Cloud Identity

**Context**: v0.2.0 delivered auth/RBAC. v0.3.0 needs to generalize storage into pluggable connectors and add cloud storage with proper IAM-derived access control.

**Decisions**:

1. **Naming**: Container = storage unit (unchanged term/API). Connector = technology type. A container *uses* a connector.

2. **Connector types**: MinIO (existing, global StorageSettings), Filesystem (FileSystemWatcher live watch, cross-platform), InMemory (ephemeral, dynamic short-term RAG), S3 (IAM-only, no stored keys), AzureBlob (managed identity).

3. **Per-container settings**: Each container overrides global chunking/embedding/search/upload settings. `IContainerSettingsResolver` merges: appsettings.json → env vars → global DB → container override (highest). Changing embedding model requires full container reindex.

4. **Cloud RBAC model**: Connapse indexes what the service credential (IAM role, managed identity) can see. Per-user scope is derived from the cloud's own IAM at access time — Connapse does NOT maintain a shadow permission table for cloud connectors. Scopes cached with 15-min TTL. Local connectors use role-level RBAC for now.

5. **Cloud identity per user**: Each Connapse user links one identity per cloud provider. AWS: IAM Identity Center SSO (device authorization flow). Azure: OAuth2 authorization code + PKCE with client secret (confidential client, Web platform). AWS admin config: 2 fields (IssuerUrl, Region). Azure admin config: 3 fields (ClientId, TenantId, ClientSecret). Identity facts stored encrypted (no access tokens, no keys). Connapse auto-registers as a public OAuth2 client with IAM Identity Center via the RegisterClient API. Azure uses Web platform (requires client_secret for server-side token exchange); PKCE is sent additionally for defense in depth.

6. ~~**RS256**~~: *Removed*. RS256 was required for the old per-user OIDC federation model where Connapse acted as an identity provider to AWS. With the switch to IAM Identity Center SSO (AWS is now the identity provider to Connapse), RS256/JWKS endpoints are no longer needed. JWTs are HS256-only.

7. **Filesystem connector**: FileSystemWatcher only — fully automatic, transparent to user. No manual sync. Debounced 750ms. Buffer overflow fallback: full rescan every 5 minutes.

8. **S3/AzureBlob sync**: Sync-on-demand via `POST /api/containers/{id}/sync` for v0.3.0. Live events (SQS, Event Grid) deferred to v0.4.0.

9. **InMemory connector**: Files in process memory. Chunks/vectors in PostgreSQL (is_ephemeral = true). Cleaned on startup. Full ingestion pipeline and search quality preserved within a session.

10. ~~**Agentic search**~~: *Removed*. Was implemented in Sessions I/I2 (SearchMode.Agentic, AgenticSearchService, HydeQueryEnricher) but intentionally removed before Session K. SearchMode only has { Semantic, Keyword, Hybrid }.

11. **Additional LLM/embedding providers**: OpenAI + Azure OpenAI for embeddings. Ollama + OpenAI + Anthropic for LLM. ILlmProvider formalized.

**Full plan**: [docs/v0.3.0-plan.md](../../docs/v0.3.0-plan.md)

---

### 2026-02-22 — Invite-Only Registration Model

**Context**: Open registration (`Identity:AllowRegistration`) allows anyone to create an account. For a knowledge management platform, the admin should control who has access.

**Decision**: Replace open registration with an invite-only system:
1. **First-user setup**: If no users exist, the login page shows a setup form. The first user becomes admin (`IsSystemAdmin = true`, Admin role).
2. **Admin invites**: Admins create invitations via `/admin/users` page. Each invite generates a unique token-based link (SHA-256 hashed, 7-day expiry).
3. **Invite acceptance**: Invited users visit `/register?token=...` to set their password and create their account with the admin-assigned role.
4. **No public registration**: The `/register` page requires a valid token. The MapIdentityApi `POST /register` endpoint returns 403.

**Alternatives**:
- Option A: Keep open registration with config flag — too permissive for multi-user knowledge platform
- Option B: Invite-only with admin-generated links (chosen) — admin controls access, simple UX
- Option C: Approval queue (user registers, admin approves) — more complex, user waits in limbo

**Consequences**:
- New `user_invitations` table (EF migration)
- `InviteService` handles token generation, validation, acceptance, revocation
- Admin can choose role (Viewer/Editor/Agent/Admin) at invite time
- `Identity:AllowRegistration` config removed
- Environment-variable admin seeding still works for initial deployment (before first-user setup)

---

### 2026-02-21 — Auth Strategy: Maximize Built-In ASP.NET Core Identity, Minimize Hand-Rolled Code

**Context**: v0.2.0 Session A created `Connapse.Identity` with custom services for JWT, PAT, audit logging, and scope-based authorization. Before building the login/register/token endpoints (Sessions B-C), we need to decide how much to rely on built-in ASP.NET Core Identity features vs hand-rolling.

**Decision**: Use built-in framework features first; only hand-roll what the framework genuinely doesn't provide.

**What changes from the original plan**:

1. **Use `AddIdentityApiEndpoints` + `MapIdentityApi`** for standard auth endpoints instead of hand-rolling `AuthEndpoints.cs`. This gives us 10 battle-tested endpoints for free:
   - `POST /register`, `POST /login`, `POST /refresh`
   - `GET /confirmEmail`, `POST /resendConfirmationEmail`
   - `POST /forgotPassword`, `POST /resetPassword`
   - `POST /manage/2fa`, `GET /manage/info`, `POST /manage/info`

2. **Keep `AddIdentity` alongside `AddIdentityApiEndpoints`** — we need full role support (`ConnapseRole`) which `AddIdentityApiEndpoints` alone doesn't provide. Register Identity first for roles, then layer API endpoints on top.

3. **Keep the custom PAT system** (`PatService`, `ApiKeyAuthenticationHandler`) — ASP.NET Core Identity has no built-in API key mechanism. The `cnp_` prefix token system is genuinely custom.

4. **Keep the custom JWT service** (`JwtTokenService`, `ITokenService`) — `MapIdentityApi`'s built-in tokens are proprietary (not standard JWTs). We need real JWTs with standard claims for SDK/external clients. The built-in bearer tokens from `MapIdentityApi` are fine for first-party use (Blazor UI, CLI), but our JWT tier serves third-party SDK consumers.

5. **Keep audit logging** (`AuditLogger`, `IAuditLogger`) — no built-in equivalent.

6. **Keep scope-based authorization** (`ScopeAuthorizationHandler`) — goes beyond built-in role-based policies.

7. **Remove hand-rolled endpoints that `MapIdentityApi` provides for free**: login, register, refresh, password reset, email confirmation, 2FA setup, account info management.

8. **Add only custom endpoints**: PAT CRUD (`/api/v1/auth/pats`), JWT token exchange (`/api/v1/auth/token`), user admin (`/api/v1/auth/users`).

**What Session A built that's still valid** (audited):
- `ConnapseIdentityDbContext` — correctly extends `IdentityDbContext`, no duplication
- `ConnapseUser` / `ConnapseRole` — pure extensions of built-in classes
- `PersonalAccessTokenEntity`, `RefreshTokenEntity`, `AuditLogEntity` — all genuinely custom
- `ApiKeyAuthenticationHandler` — custom scheme, no built-in equivalent
- `ScopeAuthorizationHandler` — custom authorization beyond roles
- `PatService`, `JwtTokenService`, `AdminSeedService`, `AuditLogger` — all needed
- `IdentityServiceExtensions` — well-structured, uses official APIs throughout

**What needs to change in Session A code**:
- `IdentityServiceExtensions.AddConnapseIdentity()` — add `.AddApiEndpoints()` to the Identity builder chain so `MapIdentityApi` works
- `Program.cs` — add `app.MapIdentityApi<ConnapseUser>()` call
- Blazor login page — can POST to `/login?useCookies=true` (built-in endpoint) instead of hand-rolling SignInManager calls

**Alternatives considered**:
- Option A: Hand-roll all auth endpoints (original plan) — more code to maintain, more security surface area to get wrong
- Option B: Use `MapIdentityApi` for standard flows + custom endpoints only for PAT/JWT/admin (chosen) — less code, battle-tested, Microsoft-maintained security flows
- Option C: OpenIddict — premature for single-service app, adds complexity, planned for v0.3.0 via `ITokenService` abstraction
- Option D: Keycloak — too heavyweight (Java, 500MB+), breaks local-first principle

**Consequences**:
- Fewer hand-written security-critical endpoints (reduced attack surface)
- Get 2FA, password reset, email confirmation for free (weren't even in original v0.2.0 scope)
- `MapIdentityApi` uses proprietary tokens (not JWTs) — fine for first-party, custom JWT tier handles third-party
- Must test that `AddIdentity` + `AddIdentityApiEndpoints` coexist correctly (role support + API endpoints)
- Login page can be simpler (POST to built-in endpoint vs custom SignInManager wiring)

---

### 2026-02-18 — Search Architecture: Connector + Scope + Query Model

**Context**: Current search is scoped to single MinIO-backed containers with folder path filtering. Need a north-star architecture that supports searching across any data source (S3, Slack, Discord, Notion, GitHub, etc.) through a unified interface.

**Decision**: Adopt a **Connector + Scope + Query** model where:
- **Container** = any configured connector instance (MinIO bucket, Slack server, S3 bucket, etc.)
- **Scope** = connector-specific recursive filters (folders, channels, repos) within a container
- **Query** = one search query applied across a list of container+scope pairs

**Design**: Published as [GitHub Discussion #8](https://github.com/Destrayon/Connapse/discussions/8).

**Key Principles**:
- Current container system becomes the first connector type (no breaking changes)
- Connectors are pluggable via `IConnector` interface
- Scopes are connector-specific (folders for filesystems, channels for Slack, etc.)
- RBAC attaches naturally at scope level (ties into security model from issue #7)
- Cross-container results merged via existing RRF/CrossEncoder pipeline

**Implementation**: Incremental — Phase 0 (current) is complete. Phase 1 abstracts the connector interface. Phases 2-5 add connector types.

**Consequences**:
- All future search API design must respect the container+scope model
- Auth/RBAC design (issue #7) should include scope-level permissions from the start
- Connector interface must accommodate read-only (Slack) and read-write (S3) sources

---

### 2026-02-18 — Versioning Strategy (SemVer, Pre-1.0)

**Context**: Project had no formal version tagging. Issue #7 referenced v0.2.0, v0.3.0 etc. without an established baseline.

**Decision**: Use Semantic Versioning (SemVer). Tag current state as `v0.1.0`. Pre-1.0 convention:
- `0.x.0` = feature milestones (0.1.0 = current, 0.2.0 = auth, 0.3.0 = OIDC/connectors)
- `0.x.y` = patches/fixes within a milestone
- `1.0.0` = production-ready public release

**Consequences**: All releases get git tags. README and SECURITY.md already reference v0.1.0-alpha.

---

### 2026-02-08 — Open Source + Commercial Hosting Business Model

**Context**: Project features are complete (Feature #1 and #2, 171 passing tests). Need to decide on licensing, public release strategy, and business model.

**Decision**: Open-source the full codebase under a permissive license (MIT or Apache 2.0) while building a commercial hosted service for revenue.

**Alternatives**:
- Option A: Keep proprietary, SaaS-only — limits adoption, no community
- Option B: Open core (basic features free, advanced paid) — complex, fragments community
- Option C: Fully open source + paid hosting — proven model, maximizes adoption
- Option D: Source-available with commercial restrictions (BSL, etc.) — limits business flexibility

**Rationale**: The "open source + commercial hosting" model (Supabase, GitLab, PostHog, Sentry) provides:
- Community trust and adoption through transparency
- Free self-hosting for individuals and small teams
- Revenue from managed hosting (zero-ops, support, SLA)
- Contributions and feedback to improve the product
- Clear value prop: convenience vs DIY

**Consequences**:
- Public repository with permissive license
- Must implement authentication before commercial hosting (security baseline)
- Hosted service adds operational value (backups, scaling, support) not just features
- Community contributions welcome but core team maintains direction
- Future commercial features (if any) would be hosting-specific (multi-tenant, billing, analytics)

**Next Steps**: See [PUBLIC_RELEASE_PREP.md](../../PUBLIC_RELEASE_PREP.md) for launch checklist.

---

### 2026-02-04 — Project Structure: src/ Layout

**Context**: The initial VS template created a flat `Connapse/` directory. The CLAUDE.md and init.md specify a `src/` based layout with separate projects per domain.

**Decision**: Restructured to `src/{ProjectName}/` layout with 7 source projects and 3 test projects.

**Alternatives**:
- Option A: Keep flat layout with single project — simpler but no separation of concerns
- Option B: Use `src/` layout with domain-separated projects — matches CLAUDE.md architecture

**Rationale**: Domain separation enables swappable implementations (e.g., different vector stores, embedding providers) via DI without coupling. Each project has a clear responsibility.

**Consequences**: More projects to manage, but cleaner boundaries and testability.

---

### 2026-02-04 — Core Models in Root Namespace

**Context**: Needed to decide where to place shared record types (IngestionResult, SearchHit, etc.) used across multiple projects.

**Decision**: Model records live in `Connapse.Core` namespace (files in `Models/` folder) so they can be used without additional `using` statements when the Core project is referenced.

**Alternatives**:
- Option A: `Connapse.Core.Models` namespace — requires extra using
- Option B: `Connapse.Core` namespace — available immediately with project reference

**Rationale**: These are fundamental domain types used everywhere. Keeping them in the root namespace reduces boilerplate.

**Consequences**: Root namespace has more types, but they're all core domain concepts.

---

### 2026-02-04 — Local-First Default Configuration

**Context**: Need sensible defaults for `appsettings.json` that work without cloud services.

**Decision**: Default to Ollama (embeddings + LLM), SQLite-vec (vector store), file system (uploads), no web search.

**Alternatives**:
- Option A: Cloud-first (OpenAI, Pinecone) — requires API keys to run
- Option B: Local-first (Ollama, SQLite-vec) — runs without any external accounts

**Rationale**: Matches the "local-first design" principle in CLAUDE.md. Cloud services swap in via config changes, no code changes needed.

**Consequences**: Users need Ollama installed locally for full functionality, but the app starts and builds without it.

---

### 2026-02-04 — Virtual File System with Physical Root Mapping

**Context**: Need a file system service where virtual paths like "/folder/a/b" map to physical paths under a configurable root directory, used consistently by both Web UI and CLI.

**Decision**: Created `IKnowledgeFileSystem` interface in Core with `LocalKnowledgeFileSystem` implementation in Storage. Virtual paths are normalized, combined with a configurable `RootPath`, and validated to prevent path traversal. Default root is `knowledge-data/` relative to the working directory.

**Alternatives**:
- Option A: Use raw file paths throughout — no abstraction, path security issues
- Option B: Virtual file system service — consistent mapping, path traversal protection, testable via interface
- Option C: Database-backed file metadata with blob storage — adds complexity, not needed for local-first

**Rationale**: Option B gives a clean API for both UI and CLI, prevents path traversal attacks, and keeps the local-first philosophy (files on disk, no database needed for file management).

**Consequences**: All file operations go through `IKnowledgeFileSystem`. The root directory is created on startup. Cloud deployments could swap in a blob-storage-backed implementation.

---

### 2026-02-04 — Dark Mode Theme with Purple Accents

**Context**: The default Blazor template has a light theme. The project needs a distinct visual identity.

**Decision**: Dark mode by default using Bootstrap 5's `data-bs-theme="dark"` on the `<html>` tag, with purple (#8b5cf6) as the accent color. All theme values are CSS custom properties for easy customization.

**Alternatives**:
- Option A: Light theme (Bootstrap default) — generic, no identity
- Option B: Dark theme with purple accents — distinctive, modern, reduces eye strain
- Option C: Theme toggle (light/dark) — more complex, not needed yet

**Rationale**: Dark mode with purple gives the app a clear identity. Using CSS custom properties makes it straightforward to add a theme toggle later without changing component code.

**Consequences**: All new UI components should use the CSS custom properties (e.g., `var(--surface)`, `var(--accent)`) rather than hardcoding colors.

---

### 2026-02-04 — PostgreSQL + pgvector over SQLite

**Context**: Original plan used SQLite + sqlite-vec for local-first simplicity. With the decision to dockerize, we need concurrent write support, proper full-text search, and vector search in one engine.

**Decision**: Use PostgreSQL 17 with the pgvector extension as the single database for documents, chunks, full-text search (tsvector), and vector storage.

**Alternatives**:
- Option A: SQLite + sqlite-vec — single file, zero config, but single-writer lock bottleneck during batch ingestion
- Option B: PostgreSQL + pgvector — concurrent writes, built-in FTS, pgvector for similarity search, all in one engine
- Option C: PostgreSQL + separate vector DB (Qdrant) — better vector performance but more infrastructure

**Rationale**: PostgreSQL handles all three storage needs (relational, FTS, vector) in one service. Batch uploads of 100-200 files require concurrent writes. Docker Compose makes Postgres trivial to run locally. The `IVectorStore` and `IDocumentStore` interfaces keep the door open for Option C later.

**Consequences**: Requires Docker (or local Postgres install) to run. Migrations via EF Core or raw SQL. Connection string via environment variable.

---

### 2026-02-04 — MinIO for Object/File Storage

**Context**: Need to store original uploaded files. Options: database BLOBs, local filesystem, or S3-compatible object storage. Project will be dockerized.

**Decision**: Use MinIO as S3-compatible object storage for original uploaded files. The existing `IKnowledgeFileSystem` interface gets a new `MinioFileSystem` implementation using the AWS S3 SDK.

**Alternatives**:
- Option A: Docker volume + local filesystem — simplest, already have `LocalKnowledgeFileSystem`, but no S3 compatibility
- Option B: MinIO (S3-compatible) — one extra container, cloud migration trivial (swap endpoint to real S3/Azure Blob/GCS)
- Option C: Postgres large objects / bytea — single service but DB bloat, backup size explosion

**Rationale**: MinIO is a single binary, easy Docker container. S3-wire-compatible means the same code works against AWS S3, Azure Blob (via gateway), or GCS with only an endpoint change. Files don't belong in a database.

**Consequences**: Extra container in docker-compose. `IKnowledgeFileSystem` stays as the abstraction. MinIO web UI available for debugging. Original `LocalKnowledgeFileSystem` remains for non-Docker development.

---

### 2026-02-04 — Hybrid Search with RRF Fusion + Optional Cross-Encoder Reranking

**Context**: Need to combine semantic (vector) and keyword (FTS) search results into a single ranked list. Must decide on fusion/reranking strategy.

**Decision**: Default to Reciprocal Rank Fusion (RRF, k=60) for combining semantic and keyword results. Cross-encoder reranking available as a configurable option for higher quality at the cost of latency.

**Alternatives**:
- Option A: RRF only — no model needed, fast, mathematically combines rank positions
- Option B: Cross-encoder reranking only — most accurate, but requires model and adds latency per result
- Option C: RRF default + optional cross-encoder — best of both, user chooses quality vs speed

**Rationale**: RRF is proven effective in production RAG systems and requires no additional model. Making cross-encoder optional respects the local-first principle (works without extra models) while giving power users a quality upgrade. Both strategies implement `ISearchReranker`.

**Consequences**: New `ISearchReranker` interface. `RrfReranker` ships as default. `CrossEncoderReranker` requires a compatible model in Ollama or an API-based provider. Strategy selectable via Settings page.

---

### 2026-02-04 — Runtime-Mutable Settings via Database

**Context**: Settings are currently in `appsettings.json` only. Users need to change chunking strategies, embedding models, search modes, etc. from the UI without restarting the app.

**Decision**: Runtime-mutable settings stored in Postgres (`settings` table, JSONB per category). Layered on top of the existing .NET configuration hierarchy. `IOptionsMonitor<T>` triggers live reload when settings change.

**Resolution order** (lowest to highest priority):
```
appsettings.json → appsettings.{Env}.json → Environment vars → Database (Settings page)
```

**Alternatives**:
- Option A: Config files only — requires restart, no UI
- Option B: Database-backed settings with IOptionsMonitor — live reload, UI-editable, still respects env vars for deployment
- Option C: Separate settings microservice — overengineered

**Rationale**: Option B gives users a Settings page while preserving the standard .NET config pipeline for deployment overrides (env vars for connection strings, secrets).

**Consequences**: New `ISettingsStore` interface. Settings page in Blazor with tabs per category. Services must use `IOptionsMonitor<T>` (not `IOptions<T>`) to pick up changes. Sensitive values (API keys) still overridable via env vars.

---

### 2026-02-04 — Background Ingestion with Queue for Batch Uploads

**Context**: Need to support uploading 100-200 files at once. Synchronous processing would block the request and time out.

**Decision**: Upload endpoint streams files to MinIO and enqueues ingestion jobs. A background `IHostedService` worker processes jobs with configurable parallelism (default: 4 concurrent files). Clients track progress via polling or SignalR.

**Alternatives**:
- Option A: Synchronous inline processing — blocks request, times out on large batches
- Option B: In-process background queue (`Channel<T>`) + `IHostedService` — simple, no external deps
- Option C: External message queue (RabbitMQ, Redis Streams) — more resilient but adds infrastructure

**Rationale**: Option B is sufficient for a single-instance app. The `Channel<T>` provides backpressure. If scaling to multiple instances is needed later, swapping to Option C behind `IIngestionQueue` is straightforward.

**Consequences**: New `IIngestionQueue` interface. Batch tracking table in Postgres. SignalR hub for real-time progress. CLI can wait synchronously or run in background.

---

### 2026-02-05 — Testing Strategy: Unit Tests + Testcontainers Integration Tests

**Context**: Need comprehensive test coverage for production readiness. Core components (parsers, chunkers, search fusion) need unit tests. End-to-end workflows (upload → ingest → search, reindex, settings reload) need integration tests with real services.

**Decision**: Two-tier testing approach:
1. **Unit tests** (xUnit + FluentAssertions + NSubstitute) for isolated component testing
2. **Integration tests** (Testcontainers + WebApplicationFactory) for end-to-end workflows with real PostgreSQL and MinIO

**Alternatives**:
- Option A: Unit tests only with mocks — fast but misses integration bugs, doesn't test real DB/storage behavior
- Option B: Unit tests + Testcontainers integration tests — comprehensive, catches real-world issues, requires Docker
- Option C: Manual testing only — error-prone, not repeatable, no regression protection

**Rationale**: Option B provides best balance of speed (unit tests run in milliseconds) and confidence (integration tests catch real bugs). Testcontainers automatically manages container lifecycle, making tests self-contained and reproducible. TDD approach caught 4 production bugs (IndexOutOfRangeException, ArgumentOutOfRangeException, exception handling) before deployment.

**Consequences**:
- Unit tests: 65 tests for parsers (29), chunkers (27), RRF reranker (11)
- Integration tests: 10 tests for ingestion (2), reindex (3), settings (4) — require Docker
- Build: 0 warnings, 0 errors, 100% pass rate
- All tests follow `MethodName_Scenario_ExpectedResult` naming convention
- Integration tests use `IAsyncLifetime` for proper container cleanup

---

### 2026-02-06 — Container-Based File Browser with Vector Index Isolation (IMPLEMENTED)

**Context**: The upload page was a simple file upload interface. Users needed a full object storage browser (like S3/MinIO) where they can organize files into projects with folder hierarchies. Each project should have isolated search - searching one project should never return results from another.

**Decision**: Replaced the upload page with a container-based file browser. Containers are top-level isolated units (representing projects). Each container has its own logical vector space. Folders provide organizational hierarchy within containers. Full path (`/{container}/{folder-path}/{filename}`) determines uniqueness.

**Status**: Fully implemented across all 9 phases (schema, core services, API, UI, CLI, MCP, tests). 171 tests passing.

**Key Design Decisions**:

1. **Container Isolation**: Single `chunks` table with `container_id` column, always filtered by container. No cross-container search allowed. Containers represent isolated projects.

2. **Folder Hierarchy**: Folders are organizational units within containers. Path-based filtering for search (e.g., search in `/docs/2026/` only searches that subtree recursively). Empty folders are explicitly supported.

3. **File Uniqueness**: Full path including container is the unique identifier. Same filename in same folder → gets `file (1).pdf` pattern. Same file content in different containers → completely independent, no cross-container deduplication.

4. **Chunk Lifecycle**: Chunks are tied to file lifecycle via `CASCADE DELETE`. File deleted → chunks deleted. File re-uploaded with same path and different hash → re-index. Same hash → skip.

5. **File Editing**: Delete + re-upload only (no in-place editing). Most document types can't be edited in-browser anyway, and edits require full re-chunking/re-embedding.

6. **Folder Deletion**: Confirmation required, then cascade delete all nested files/folders and their chunks.

7. **Container Deletion**: Must be empty first (fail if not empty). User must delete all contents before deleting container.

8. **Cross-Container Operations**: Moving files between containers is prohibited. Must delete from source and re-upload to destination.

9. **CollectionId Removal**: The existing `CollectionId` field is replaced by containers. Containers serve the same purpose but are required, structured, and first-class.

**Alternatives**:
- Option A: Keep simple upload page + CollectionId tags — no visual organization, soft filtering only
- Option B: Container-based file browser with hard isolation — full object storage UX, true project isolation
- Option C: Virtual folders without isolation — organizational but no search separation

**Rationale**: Option B provides the project isolation users need (different knowledge bases shouldn't mix), gives a familiar S3-like UX for organizing files, and makes the folder structure meaningful for search filtering. The container concept maps naturally to "projects" or "workspaces".

**Consequences**:
- New `containers` table in database
- `documents` table gets required `container_id` (replaces optional `CollectionId`)
- `chunks` table gets denormalized `container_id` for query performance
- New `folders` table for empty folder support
- All API endpoints scoped under `/api/containers/{id}/...`
- Search API requires container ID
- UI becomes a file browser with container list → folder navigation → file details
- All access surfaces (Web UI, REST API, CLI, MCP) must support full file management

---

<!-- Add new decisions above this line, newest first -->
