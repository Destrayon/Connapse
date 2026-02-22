# Progress

Current status and recent work. Update at end of each session. For detailed implementation plans, see [docs/architecture.md](../../docs/architecture.md).

---

## Current Status (2026-02-21)

### Completed Features

**Feature #1: Document Upload + Ingestion + Hybrid Search** — COMPLETE
- Infrastructure (Docker, PostgreSQL+pgvector, MinIO)
- Settings system (runtime-mutable, DB-backed, live reload)
- Storage layer (document store, vector store, embeddings)
- Ingestion pipeline (parsers, chunkers, background queue)
- Hybrid search (vector + keyword FTS + RRF/CrossEncoder)
- Access surfaces (Web UI, REST API, CLI, MCP server)
- Reindexing (content-hash dedup, settings-change detection)

**Feature #2: Container-Based File Browser** — COMPLETE (9 phases)
- Database schema migration (containers, folders, container_id on docs/chunks/vectors)
- Core services (IContainerStore, IFolderStore, PathUtilities)
- API endpoints (container CRUD, file ops, folder ops, search, reindex — all container-scoped)
- Web UI (container list, file browser, file details panel, SignalR progress)
- CLI (container CRUD, upload/search/reindex with --container)
- MCP (7 tools: container_create/list/delete, search_knowledge, list_files, upload_file, delete_file)
- Testing + 6 bugs found and fixed

**Session 5 Fix: Semantic Search** (2026-02-07)
- Critical Fix: `PgVectorStore.SearchAsync` — `Vector` type silently dropped by `SqlQueryRaw` positional params. Fixed with named `NpgsqlParameter` objects.
- MinScore Tuning: Default 0.7 was too aggressive for nomic-embed-text. Changed to configurable `MinimumScore` (default 0.5).

### Test Counts
- 78 core unit tests (PathUtilities, parsers, chunkers, rerankers)
- 53 ingestion unit tests
- 40 integration tests (containers, folders, files, search isolation, cascade deletes, ingestion, reindex)
- **171 total tests**
- All 11 projects build with 0 errors (10 existing + Identity)

### In Progress (2026-02-21)

**v0.2.0 Security & Auth** — Full plan at [docs/v0.2.0-plan.md](../../docs/v0.2.0-plan.md)

| Session | Phases | Status |
|---------|--------|--------|
| A | 1-2: Identity project + EF migration | **COMPLETE** |
| B | 3: Cookie auth + Blazor wiring + MapIdentityApi | **COMPLETE** |
| C | 4-5: PAT + JWT systems | Pending |
| D | 6: RBAC + endpoint protection | Pending |
| E | 7-8: Rate limiting, audit, UI pages | Pending |
| F | 9: CLI updates | Pending |
| G | 10-11: Testing + deployment | Pending |

Key decisions made:
- Three-tier auth: Cookie + PAT + JWT (HS256, migrate to RS256 in v0.3.0)
- New Connapse.Identity project (separate DbContext, shared DB)
- Admin seed via env vars, minimal UI
- JWT for future SDK clients (60-90 min tokens)
- **NEW (Session B)**: Maximize built-in ASP.NET Core Identity; use `MapIdentityApi` for standard endpoints, hand-roll only PAT/JWT/admin

### Completed (2026-02-18)
- Established versioning (v0.1.0 tag)
- Search architecture design: Connector + Scope + Query model (see GitHub Discussions)
- Security quick wins from issue #7

---

## Known Issues

See [issues.md](issues.md) for detailed tracking of bugs and tech debt.

---

## Session History

### 2026-02-21 (Session 9) — v0.2.0 Session B: Cookie Auth + Blazor Wiring + MapIdentityApi
- **Architectural Decision**: Maximize built-in ASP.NET Core Identity features; use `MapIdentityApi` for standard auth endpoints (documented in decisions.md)
- Added `.AddApiEndpoints()` to IdentityServiceExtensions for built-in endpoint support
- Added `AddCascadingAuthenticationState()` + `MapIdentityApi<ConnapseUser>()` at `/api/v1/identity/` (10 built-in endpoints: register, login, refresh, 2FA, password reset, etc.)
- Replaced `<RouteView>` with `<AuthorizeRouteView>` in Routes.razor (redirects unauthenticated to /login)
- Created static SSR auth pages (Login, Register, Logout, AccessDenied) — no `@rendermode` to avoid SignalR chicken-and-egg
- Login uses `SignInManager.PasswordSignInAsync()` directly (standard Blazor Server pattern)
- Register uses `UserManager.CreateAsync()`, assigns Viewer role, auto-signs-in
- Logout is POST-only (CSRF-safe via antiforgery token)
- Created AuthLayout (centered card, no sidebar) for auth pages
- Created RedirectToLogin/RedirectToAccessDenied helper components with `forceLoad: true`
- Added `@attribute [Authorize]` to Home, Search, Settings, FileBrowser pages
- Updated NavMenu with `<AuthorizeView>`: shows nav links + user info + logout when authenticated, login link when not
- Added auth usings to `_Imports.razor` (`Microsoft.AspNetCore.Authorization`, `Microsoft.AspNetCore.Components.Authorization`)
- Register respects `Identity:AllowRegistration` config flag
- All 11 projects build with 0 errors, 0 warnings
- 129 unit tests passing (77 core + 52 ingestion)
- **Auth is now enforced on Blazor UI** — no auth on API endpoints yet (Session D)

### 2026-02-21 (Session 8) — v0.2.0 Session A: Identity Project + EF Migration
- Created `src/Connapse.Identity/` project (Phases 1-2 complete)
- Entities: ConnapseUser, ConnapseRole, PersonalAccessTokenEntity, RefreshTokenEntity, AuditLogEntity
- ConnapseIdentityDbContext with full snake_case table/column mapping, separate migration history
- Core additions: IAuditLogger interface, AuthModels DTOs (LoginRequest, TokenResponse, PatCreateResponse, etc.)
- ApiKeyAuthenticationHandler: SHA-256 hash lookup, scope claims, fire-and-forget last_used_at update
- ScopeAuthorizationHandler: role-to-scope mapping (Admin→all, Editor→read+write, Viewer→read, Agent→read+ingest)
- Services: PatService (cnp_ token generation), JwtTokenService (HS256, refresh rotation), AdminSeedService (env var seed), AuditLogger
- IdentityServiceExtensions: AddConnapseIdentity(), AddConnapseAuthentication() (multi-scheme: Cookie+ApiKey+JWT), AddConnapseAuthorization()
- Program.cs: Identity services registered, auth middleware added, DbContext migration + admin seed on startup
- EF migration generated: 10 tables (users, roles, user_roles, user_claims, role_claims, user_logins, user_tokens, personal_access_tokens, refresh_tokens, audit_logs)
- appsettings.json: Identity config section added
- All 11 projects build with 0 errors, 0 warnings
- **No auth enforced yet** — that comes in Session D (Phase 6)

### 2026-02-18 (Session 7) — Roadmap, Versioning, Search Architecture Design
- Cleaned up progress.md (collapsed completed feature details)
- Established v0.1.0 version tag
- Published search architecture design (Connector + Scope + Query model) as GitHub Discussion
- Applied security quick wins from issue #7
- Updated README roadmap section

### 2026-02-07 (Session 6) — Semantic Search Bug Fix & MinScore Tuning
- Fixed critical pgvector parameter binding (named NpgsqlParameter objects)
- Made minScore configurable across all surfaces (Settings, API, CLI, MCP)

### 2026-02-06 (Session 5) — Feature #2 Phase 9: Testing (COMPLETE)
- 78 core + 53 ingestion + 40 integration = 171 tests, all passing
- 6 bugs found and fixed during testing

### 2026-02-06 (Session 4) — Feature #2 Phases 1-8
- Complete implementation of container-based file browser

### 2026-02-05 (Session 3) — Critical Bug Fixes
- Fixed JSONB deserialization, DbContext threading, settings reload architecture

### 2026-02-05 (Session 2) — Integration Test Fixes

### 2026-02-05 (Session 1) — Initial Integration Tests
