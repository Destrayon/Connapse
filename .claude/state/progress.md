# Progress

Current status and recent work. Update at end of each session. For detailed implementation plans, see [docs/architecture.md](../../docs/architecture.md).

---

## Current Status (2026-02-18)

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
- All 10 projects build with 0 errors

### In Progress (2026-02-18)
- Establishing versioning (v0.1.0 tag)
- Search architecture design: Connector + Scope + Query model (see GitHub Discussions)
- Security quick wins from issue #7
- Security model planning (Identity + PAT + JWT, see [issue #7](https://github.com/Destrayon/Connapse/issues/7))

---

## Known Issues

See [issues.md](issues.md) for detailed tracking of bugs and tech debt.

---

## Session History

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
