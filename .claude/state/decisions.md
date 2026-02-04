# Architectural Decisions

Record significant decisions with context and rationale. Future sessions should check this before making architectural changes.

---

### 2026-02-04 — Project Structure: src/ Layout

**Context**: The initial VS template created a flat `AIKnowledgePlatform/` directory. The CLAUDE.md and init.md specify a `src/` based layout with separate projects per domain.

**Decision**: Restructured to `src/{ProjectName}/` layout with 7 source projects and 3 test projects.

**Alternatives**:
- Option A: Keep flat layout with single project — simpler but no separation of concerns
- Option B: Use `src/` layout with domain-separated projects — matches CLAUDE.md architecture

**Rationale**: Domain separation enables swappable implementations (e.g., different vector stores, embedding providers) via DI without coupling. Each project has a clear responsibility.

**Consequences**: More projects to manage, but cleaner boundaries and testability.

---

### 2026-02-04 — Core Models in Root Namespace

**Context**: Needed to decide where to place shared record types (IngestionResult, SearchHit, etc.) used across multiple projects.

**Decision**: Model records live in `AIKnowledge.Core` namespace (files in `Models/` folder) so they can be used without additional `using` statements when the Core project is referenced.

**Alternatives**:
- Option A: `AIKnowledge.Core.Models` namespace — requires extra using
- Option B: `AIKnowledge.Core` namespace — available immediately with project reference

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

<!-- Add new decisions above this line, newest first -->
