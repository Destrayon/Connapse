# Progress

Current status and recent work. Update at end of each session. For detailed implementation plans, see [docs/architecture.md](../../docs/architecture.md).

---

## Current Status (2026-02-26) — Browser-based CLI login (PKCE)

### Session 17 — Browser-Based CLI Login (2026-02-26)

Implemented OAuth 2.0 PKCE loopback redirect (RFC 8252 + RFC 7636) for `connapse auth login`.

**Files created:**
- `src/Connapse.Identity/Data/Entities/CliAuthCodeEntity.cs` — short-lived auth code entity
- `src/Connapse.Identity/Services/CliAuthService.cs` — initiate + PKCE exchange service
- `src/Connapse.Web/Components/Pages/Auth/CliAuthorize.razor` — "Authorize CLI?" Blazor page
- `src/Connapse.Identity/Migrations/*_AddCliAuthCodes.cs` — EF migration (auto-generated)

**Files modified:**
- `src/Connapse.Identity/Data/ConnapseIdentityDbContext.cs` — added `CliAuthCodes` DbSet + model builder
- `src/Connapse.Identity/IdentityServiceExtensions.cs` — registered `CliAuthService`
- `src/Connapse.Web/Endpoints/AuthEndpoints.cs` — added `POST /api/v1/auth/cli/exchange`
- `src/Connapse.Core/Models/AuthModels.cs` — added `CliExchangeRequest` + `CliExchangeResponse`
- `src/Connapse.CLI/Program.cs` — new browser flow in `AuthLoginBrowser`, password flow moved to `AuthLoginPassword`, `--no-browser` flag

**New flow:** `connapse auth login` opens browser → `/cli/authorize` (Blazor, requires cookie auth) → user clicks Authorize → server creates PKCE-bound 5-min code → browser redirects to `http://127.0.0.1:PORT/callback` → CLI verifies state, POSTs code + verifier to `/api/v1/auth/cli/exchange` → server creates PAT → CLI saves credentials.

**Fallback:** `connapse auth login --no-browser` uses the original email/password flow.

---

## Previous Status (2026-02-26)

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

### Session 14 (2026-02-26): First-Class Agent Entities

Replaced the Agent IdentityRole with a dedicated AgentEntity system:
- Removed "Agent" from `AdminSeedService.DefaultRoles` and `RoleDescriptions`
- Blocked "Agent" role assignment in `AuthEndpoints` and via `InviteService` (automatic via DefaultRoles check)
- New `AgentEntity` + `AgentApiKeyEntity` tables (EF migrations: `AddAgentEntities`, `RemoveAgentIdentityRole`)
- New `IAgentService` / `AgentService` with full CRUD + key lifecycle management
- Updated `ApiKeyAuthenticationHandler` with two-table lookup; agent keys inject synthetic `ClaimTypes.Role = "Agent"` so `RequireAgent` policy on MCP continues to work
- New `AgentEndpoints` at `/api/v1/agents` (7 endpoints, all `RequireAdmin`)
- New `AgentIntegrationTests.cs` (16 tests); added `AssignRoles_AssignAgentRole_Returns400` to `AuthEndpointTests`

### Test Counts
- 78 core unit tests (PathUtilities, parsers, chunkers, rerankers)
- 53 ingestion unit tests
- 40 integration tests (containers, folders, files, search isolation, cascade deletes, ingestion, reindex)
- 29 auth endpoint integration tests (token, refresh, PATs, users, roles; +1 Agent role blocked)
- 16 agent integration tests (agent CRUD, key lifecycle, MCP auth, disable/delete flows)
- **216 total tests**
- All 11 projects build with 0 errors

### Session 17 (2026-02-26): Session F — CLI Auth Updates

- Added credential storage: `~/.connapse/credentials.json` with `{ apiKey, apiBaseUrl, userEmail }`
- Credentials loaded at startup; `X-Api-Key` header auto-injected into all HTTP requests
- If credentials specify a different server URL, it overrides `apiBaseUrl` from config
- New `auth` command group:
  - `auth login [--url <server>]` — prompts email+password → JWT → creates CLI PAT → saves credentials
  - `auth logout` — deletes credentials file
  - `auth whoami` — shows current identity, verifies token against server
  - `auth pat create <name> [--expires <date>]` — creates PAT, displays token once
  - `auth pat list` — lists all PATs with status (Active/REVOKED/EXPIRED)
  - `auth pat revoke <id>` — revokes a PAT by GUID
- Updated `PrintUsage()` to document all commands including auth
- Login flow: `POST /api/v1/auth/token` → `POST /api/v1/auth/pats` (PAT named "CLI ({MachineName})")
- Password input masked with `*` characters via `Console.ReadKey(intercept: true)`
- Build: 0 errors, 1 pre-existing warning

### Session 16 (2026-02-26): Session E — Audit Logging + Personal Tokens UI

- Skipped rate limiting (not appropriate for self-hosted; no plan to add)
- Added `IAuditLogger` to `DocumentsEndpoints`: `doc.uploaded` on successful upload, `doc.deleted` on delete
- Added `IAuditLogger` to `ContainersEndpoints`: `container.created` on create, `container.deleted` on delete
- Created `PersonalTokens.razor` at `/settings/tokens` (InteractiveServer):
  - Create token (name + optional expiry date)
  - One-time token display with copy button on creation
  - List all tokens (prefix, created, last used, expiry, revoked badge)
  - Revoke with confirmation step
- Added "API Tokens" nav link (`bi-key-fill`) in NavMenu for all authenticated users
- Build: 0 errors, 1 pre-existing warning

### Session 15 (2026-02-26): Agent Management UI

- Removed "Agent" from user role dropdowns in `UserManagement.razor` (invite + change-role selects)
- Added server-side guard in `ChangeRoleAsync` blocking "Agent" and "Owner" role assignment via user management
- Created `AgentManagement.razor` at `/admin/agents`:
  - Create agent (name + description)
  - List agents (status badge, active key count, created date)
  - Enable/Disable toggle per agent
  - Expand row → inline key management (add key, revoke keys, one-time token display)
  - Delete agent with confirmation
- Added "Agents" nav link in NavMenu (Admin section, `bi-robot` icon)
- Build: 0 errors, 1 pre-existing warning

### Next Up

**v0.2.0 Security & Auth** — Full plan at [docs/v0.2.0-plan.md](../../docs/v0.2.0-plan.md)

| Session | Phases | Status |
|---------|--------|--------|
| A | 1-2: Identity project + EF migration | **COMPLETE** |
| B | 3: Cookie auth + Blazor wiring + MapIdentityApi | **COMPLETE** |
| B2 | Invite-only registration system | **COMPLETE** |
| C | 4-5: PAT + JWT systems | **COMPLETE** |
| D | 6: RBAC + endpoint protection | **COMPLETE** |
| E | 7-8: Audit logging + Personal Tokens UI | **COMPLETE** |
| F | 9: CLI updates | **COMPLETE** |
| G | 10-11: Testing + deployment | Pending |

Key decisions made:
- Three-tier auth: Cookie + PAT + JWT (HS256, migrate to RS256 in v0.3.0)
- New Connapse.Identity project (separate DbContext, shared DB)
- Admin seed via env vars, minimal UI
- JWT for future SDK clients (60-90 min tokens)
- **NEW (Session B)**: Maximize built-in ASP.NET Core Identity; use `MapIdentityApi` for standard endpoints, hand-roll only PAT/JWT/admin
- **NEW (Session B2)**: Invite-only registration — first user becomes admin via setup page, all subsequent users must be invited by admin

### Completed (2026-02-18)
- Established versioning (v0.1.0 tag)
- Search architecture design: Connector + Scope + Query model (see GitHub Discussions)
- Security quick wins from issue #7

---

## Known Issues

See [issues.md](issues.md) for detailed tracking of bugs and tech debt.

---

## Session History

### 2026-02-25 (Session 13) — v0.2.0 Session D: RBAC + Endpoint Protection

**Phase 6 complete.** All API endpoints now require authentication and enforce role-based access:

- **ContainersEndpoints**: GET→RequireViewer, POST/DELETE/reindex→RequireEditor (per-endpoint)
- **DocumentsEndpoints**: GET→RequireViewer, POST/DELETE→RequireEditor (per-endpoint)
- **FoldersEndpoints**: RequireEditor (group-level — all ops are writes)
- **SearchEndpoints**: RequireViewer (group-level)
- **BatchesEndpoints**: RequireViewer (group-level)
- **SettingsEndpoints**: RequireAdmin (group-level)
- **McpEndpoints**: RequireAgent (group-level)
- **Settings.razor**: Changed `[Authorize]` → `[Authorize(Policy = "RequireAdmin")]`

**Integration tests updated**: All 5 test classes (ContainerIntegrationTests, IngestionIntegrationTests, ReindexIntegrationTests, SettingsIntegrationTests, ConnectionTestIntegrationTests) now:
- Seed admin via `CONNAPSE_ADMIN_EMAIL` / `CONNAPSE_ADMIN_PASSWORD` + `Identity:Jwt:Secret` env vars
- Obtain JWT token via `POST /api/v1/auth/token` in `InitializeAsync`
- Set `DefaultRequestHeaders.Authorization = Bearer <token>` on all test clients

**Build**: 0 errors, 0 warnings across all 11 projects.
**Unit tests**: 77 core + 52 ingestion = **129 unit tests**, all passing.

### 2026-02-23 (Session 12) — Auth Endpoint Integration Tests + Two Critical Bug Fixes

**Tests Added**: `tests/Connapse.Integration.Tests/AuthEndpointTests.cs` — 28 tests covering all 7 auth endpoints under `/api/v1/auth/`:
- `POST /token` — valid login, wrong password, unknown email, LastLoginAt update
- `POST /token/refresh` — valid rotation, invalid token, revoked token reuse
- `GET /pats` — list PATs (authed), unauthenticated
- `POST /pats` — create PAT, duplicate name
- `DELETE /pats/{id}` — revoke, already-revoked, not-found, cross-user
- `GET /users` — admin access, viewer denied, unauthenticated
- `PUT /users/{id}/roles` — assign roles (admin), viewer denied, Owner protection, user not found
- PAT authentication end-to-end (create via JWT, then auth with `X-Api-Key`)

**All 28 tests pass.** Test infrastructure: Testcontainers (PostgreSQL + MinIO), `WebApplicationFactory<Program>`, seeded admin + viewer users, pinned JWT secret via `UseSetting`.

**Bug #1 Fixed**: `AddIdentity<>()` overrides `DefaultAuthenticateScheme = "Identity.Application"`, silently breaking JWT/PAT auth.
- Fixed in `src/Connapse.Identity/IdentityServiceExtensions.cs`: explicitly set `DefaultAuthenticateScheme = "MultiScheme"` and `DefaultSignInScheme`/`DefaultSignOutScheme` = `IdentityConstants.ApplicationScheme`.

**Bug #2 Fixed**: `UseStatusCodePagesWithReExecute` intercepted empty-body API 401 responses, re-POSTing to `/not-found`, where antiforgery returned 400.
- Fixed in `src/Connapse.Web/Program.cs`: added middleware to disable `IStatusCodePagesFeature` for all `/api` paths.
- Also fixed `POST /token/refresh` to return `Results.Json(...)` with body instead of empty `Results.Unauthorized()`.

**Test counts now**: 78 core + 53 ingestion + 40 integration + 28 auth = **199 total tests**

### 2026-02-22 (Session 11) — v0.2.0 Session C: PAT + JWT Auth Endpoints

- Created `src/Connapse.Web/Endpoints/AuthEndpoints.cs` (7 routes under `/api/v1/auth`):
  - `POST /token` — email+password → JWT TokenResponse (anonymous, updates LastLoginAt, audit log)
  - `POST /token/refresh` — rotate refresh token → new token pair (anonymous)
  - `GET /pats` — list authenticated user's PATs (RequireAuthorization)
  - `POST /pats` — create PAT, returns raw token once (RequireAuthorization)
  - `DELETE /pats/{id}` — revoke PAT by ID (RequireAuthorization)
  - `GET /users` — list all users with roles (RequireAdmin policy)
  - `PUT /users/{id}/roles` — assign roles; Owner role protected from removal/assignment (RequireAdmin policy)
- Added `[Authorize]` to `IngestionHub` — connections require cookie or `?access_token=` JWT (already configured in IdentityServiceExtensions)
- Registered `app.MapAuthEndpoints()` in Program.cs
- All 11 projects build with 0 errors, 0 warnings
- Note: The underlying PAT/JWT services (PatService, JwtTokenService, ITokenService) and SignalR JWT query-string support were already implemented in Sessions A/B

### 2026-02-22 (Session 10) — v0.2.0 Session B2: Invite-Only Registration System
- Replaced open registration with invite-only model
- First-user setup: Login page detects no users → shows admin account creation form (IsSystemAdmin + Admin role)
- UserInvitation entity + EF migration (user_invitations table with token_hash, email, role, expiry)
- InviteService: create/validate/accept/list/revoke invitations (SHA-256 hashed tokens, 7-day expiry)
- Register page (/register) now requires `?token=` query param → validates invite → creates account with assigned role
- Admin UserManagement page (/admin/users): invite users, view pending invites (revoke), list all users
- Invite links auto-generated using NavigationManager.BaseUri
- NavMenu: "Users" link visible only to Admin role
- Blocked public API registration (MapIdentityApi /register returns 403)
- Removed `Identity:AllowRegistration` config flag (no longer needed)
- All projects build with 0 errors

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
