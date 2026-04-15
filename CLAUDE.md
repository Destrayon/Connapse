# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build
dotnet build

# Run web app (requires backing services)
dotnet run --project src/Connapse.Web

# Start backing services (PostgreSQL + MinIO, ports exposed to host)
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d

# Full stack via Docker
docker compose up -d
```

## Tests

```bash
# All tests
dotnet test

# Unit tests only (no Docker required)
dotnet test --filter "Category=Unit"

# Integration tests (needs Docker for Testcontainers)
dotnet test --filter "Category=Integration"

# Single test class
dotnet test --filter "FullyQualifiedName~Connapse.Core.Tests.CloudScope.CloudScopeServiceTests"

# Single test method
dotnet test --filter "FullyQualifiedName~ClassName.MethodName_Scenario_ExpectedResult"
```

- **xUnit** framework, **FluentAssertions** (`.Should()`), **NSubstitute** for mocking
- Integration tests share a single PostgreSQL+MinIO container pair via `SharedWebAppFixture` (collection-based)
- Tag tests: `[Trait("Category", "Unit")]` or `[Trait("Category", "Integration")]`
- Test naming: `MethodName_Scenario_ExpectedResult`

## Database Migrations

Two separate EF Core migration histories:
- `KnowledgeDbContext` (Storage): `dotnet ef migrations add Name --project src/Connapse.Storage`
- `ConnapseIdentityDbContext` (Identity): `dotnet ef migrations add Name --project src/Connapse.Identity`

Migrations run automatically on app startup.

## Architecture

```
Connapse.Web          (Blazor Server + Minimal API endpoints + MCP server)
├── Connapse.Identity (Auth: OAuth 2.1/JWT/PAT/cookie, cloud identity, audit)
├── Connapse.Ingestion(Parsers, chunking strategies, background worker)
├── Connapse.Search   (Vector, keyword, hybrid search + reranking)
├── Connapse.Storage  (PostgreSQL/pgvector, EF Core, connectors, embedding/LLM providers)
├── Connapse.Agents   (Agent orchestration)
└── Connapse.Core     (Domain models, interfaces, settings records — zero dependencies)
```

Dependencies flow downward. `Core` has zero external dependencies. All projects reference `Core`. The `connapse` CLI lives in a separate repository (https://github.com/Destrayon/connapse-cli) and talks to this server only via the REST API.

### Key Patterns

- **DbContext Factory**: Always use `IDbContextFactory<T>` and create short-lived contexts (`await using var ctx = await factory.CreateDbContextAsync(ct)`) — never share a scoped DbContext across threads (Blazor Server requirement)
- **Provider factory delegates**: `IEmbeddingProvider` and `ILlmProvider` resolved via factory delegates that read settings at scope time (swap providers without restart)
- **Endpoint registration**: Each domain has `Map{Feature}Endpoints()` extension methods in `src/Connapse.Web/Endpoints/`, registered in `Program.cs`
- **DI registration**: Each layer has `ServiceCollectionExtensions` with `Add{Layer}()` methods
- **Settings hierarchy**: appsettings.json < environment-specific < User Secrets < env vars < PostgreSQL `settings` table < CLI args. Services use `IOptionsMonitor<T>` for runtime reload.
- **Container isolation**: Each Container has its own storage connector, vector index, and optional settings overrides — no cross-container data sharing

### Vector Storage

- `chunk_vectors.embedding` is unconstrained `vector` (no fixed dimension) — supports mixed embedding models
- Partial IVFFlat indexes per `model_id`, managed by `VectorColumnManager`
- Search filters by `model_id` and casts query vector to matching dimensions

### Access Surfaces

1. **Blazor Server UI** — interactive components at `/`
2. **REST API** — Minimal API endpoints at `/api/...`
3. **MCP Server** — at `/mcp` via ModelContextProtocol SDK

The standalone [connapse-cli](https://github.com/Destrayon/connapse-cli) is an additional client that talks to this server over the REST API.

## Code Conventions

- .NET 10, file-scoped namespaces, nullable enabled, implicit usings
- Records for DTOs and settings models
- Primary constructors for DI
- Async all the way (never `.Result` or `.Wait()`)
- Parameterized SQL queries only (never string interpolation)
- Don't use `var` for primitive types
- Don't use `dynamic`
- When logging user-controlled values (search queries, request bodies, headers, client-supplied IDs), wrap them with `LogSanitizer.Sanitize(...)` from `Connapse.Core.Utilities` to prevent CodeQL `cs/log-forging` alerts

## Commit Messages

```
<type>: <summary>
```
Types: `feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `perf:`, `chore:`

## Branch Naming

`feature/<issue>-desc`, `fix/<issue>-desc`, `refactor/<issue>-desc`, `docs/<issue>-desc`

## Knowledge Base (Connapse MCP)

If the Connapse MCP server is available, use it as your primary source of project context before relying on assumptions or general knowledge.

### When to search

Before starting any task, search relevant containers for context on:

- **Architecture decisions** and design rationale
- **Open issues** and known bugs
- **Developer guides** and setup gotchas
- **Research** related to the feature or area you're working in
- **Release history** and test results
- **Brand guidelines** when working on UI or public-facing content

### Key containers

| Container | What's in it |
|-----------|-------------|
| `connapse-architecture` | Design patterns, architecture decisions, API behaviors, business rules |
| `connapse-developer-guide` | Setup guides, feature maps, how-tos for extending Connapse |
| `connapse-release-testing` | Release test results, pass/fail reports, release decisions |
| `connapse-brand` | Brand guidelines, logo rules, color palettes |
| `connapse-roadmap` | Milestone plans, feature priorities, version goals |
| `connapse-bugs` | Known bugs, reproduction steps, root cause analysis |
| `connapse-business-rules` | Domain rules, validation logic, behavioral requirements |

Use `container_list` to discover all available containers. Research containers are typically prefixed with `research-` and contain deep-dive findings on specific topics. Search multiple containers if the topic could span areas.

### How to search

Use `search_knowledge` with the most relevant container and a natural language query. Use Hybrid mode for best results. Search multiple containers if the topic spans areas (e.g., an architecture question might have context in both `connapse-architecture` and a `research-*` container).

### Contributing back to the knowledge base

While working, you may add to the knowledge base if you produce insights, decisions, or research that would be valuable for future sessions. Examples:

- A new architecture decision or design rationale
- Research findings from investigating a bug or evaluating an approach
- Test results or release validation reports

**To add a new file:** Use `upload_file` with `textContent` and a descriptive `fileName`. Place it in the most appropriate existing container.

**To update an existing file:** Connapse does not support in-place edits. To update a file:

1. Use `get_document` to retrieve the current content
2. Use `delete_file` to remove the old version
3. Use `upload_file` to upload the new version with the appended or revised content

Keep the same `fileName` and `path` so the file retains its identity.

**Do not** create new containers without asking the user first.
