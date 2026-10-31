---
title: Architecture internals
type: explanation
audience: contributor
---

# Architecture internals

This page is for people changing Connapse's code. How the product behaves for its users is in [How Connapse works](../concepts/how-connapse-works.md), [About search](../concepts/search.md) and [About ingestion](../concepts/ingestion.md); this page covers the projects, the patterns the code follows, and where to look.

## Projects

| Project | Holds | References |
|---------|-------|------------|
| `Connapse.Core` | Domain models, interfaces, settings records. No dependencies. | — |
| `Connapse.Storage` | EF Core and PostgreSQL/pgvector, connectors, embedding and LLM providers, settings store | Core |
| `Connapse.Identity` | ASP.NET Core Identity, PATs, JWTs, OAuth, agents, audit log | Core |
| `Connapse.Ingestion` | Parsers, chunkers, the ingestion pipeline, reindexing | Core, Storage |
| `Connapse.Search` | Vector, keyword and hybrid search, fusion, reranking | Core, Storage |
| `Connapse.Agents` | Agent orchestration | Core, Search, Storage |
| `Connapse.Application` | The operations every surface calls | Core, Storage, Identity, Ingestion, Search |
| `Connapse.Background` | Hangfire jobs for ingestion and summaries | Core, Ingestion, Storage |
| `Connapse.ParserHost` | The sandboxed process parsers run in | Core, Ingestion |
| `Connapse.Web` | Blazor Server UI, Minimal API endpoints, the MCP server, hosting | All of the above |

References only point down the table. `Connapse.Architecture.Tests` enforces the rules that matter most, such as `Connapse.Application` never compiling against ASP.NET Core.

## Operations

Every action a user or agent can take is an operation in `Connapse.Application`: a class with a `RunAsync` method and an `[Operation]` attribute naming it and declaring its scope, risk tier and MCP exposure:

```csharp
[Operation("settings.reindex", Scope = SettingsScope.Name, Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
```

The surfaces are thin. A Blazor component, a REST endpoint in `src/Connapse.Web/Endpoints/` and an MCP tool all build an operation context from the caller and call the same operation, so authorisation, validation, auditing and error shapes are decided once. Failures come back as results, and the REST and MCP surfaces turn them into RFC 9457 problem details whose `type` points at [`docs/problems.md`](../problems.md). Operations with `McpExposure.Generic` are reachable through the generic `connapse_api_*` MCP tools, described by `docs/api/openapi.json`, which the build writes.

## Patterns

- **Short-lived database contexts.** Use `IDbContextFactory<T>` and `await using var ctx = await factory.CreateDbContextAsync(ct)`. Blazor Server circuits run on many threads, so a scoped context must never be shared.
- **Providers resolved per scope.** `IEmbeddingProvider` and `ILlmProvider` are registered as factory delegates that read the current settings, so switching provider needs no restart.
- **Settings.** appsettings.json, then environment-specific files, user secrets, environment variables, the PostgreSQL `settings` table and command-line arguments, each overriding the last. Services read `IOptionsMonitor<T>`. Per-container overrides resolve through `IContainerSettingsResolver`: the container's override for a category, else the global stored category, else the defaults in `SettingsModels.cs`. An override replaces the whole category.
- **DI registration.** Each project exposes `Add{Layer}()` in its `ServiceCollectionExtensions`; endpoints register through `Map{Feature}Endpoints()`.

## Storage

- `KnowledgeDbContext` (Storage) and `ConnapseIdentityDbContext` (Identity) share one database with separate migration histories, the identity one in `__identity_ef_migrations_history`. Migrations run at startup, before hosted services start, so a migration can't depend on one.
- `chunk_vectors.embedding` is an unconstrained `vector` column, so vectors of different models and dimensions live side by side. Each row carries its `model_id`, and searches filter on it and cast the query vector to the matching dimension. Per container and model, an HNSW index is built in the background once there are 20,000 vectors (`SearchSettings.VectorIndexMinVectors`).
- Original uploads are kept in managed storage (MinIO by default). Only the managed-storage connector implements `IWritableConnector`; `IConnector` has no write methods, so a source can't be written to by construction.
- Identity links to outside systems are encrypted at rest with ASP.NET Core Data Protection.

## Sources and sync

`SourceSyncService` (in `Connapse.Web/Services`) polls every enabled source, every 5 minutes or at the source's own interval, and reconciles it with the remote by last-modified time and size. A source already syncing is skipped, not queued. Sync cursors advance by compare-and-swap, so a cycle racing a configuration change can't overwrite newer state. The service is registered as a singleton and as a hosted service, so `POST /api/sources/{id}/sync` runs a cycle on the same instance.

## Per-user filtering

Search passes the caller's user id into `HybridSearchService`, which asks an `ISearchScopeResolver` what that user may reach and fails closed when the resolver errors. `UnrestrictedScopeResolver` is the default; `AwsSearchScopeResolver` replaces it once AWS identity linking is configured and reads S3 Access Grants. Azure, GitHub, Atlassian and custom sources check each hit against their own system at search time. `ConnectorScopeCache` keeps an allow for 15 minutes and a deny for 5. The principle every connector follows is in [About permissions](../concepts/permissions.md).

## Related

- [Development setup](development.md)
- [How Connapse works](../concepts/how-connapse-works.md)
- [Problem types](../problems.md)
