# Project Conventions

Patterns and style choices specific to AIKnowledgePlatform. Update when new patterns emerge to keep future sessions consistent.

---

## Naming

- **Services**: `{Domain}Service` → `IngestionService`, `SearchService`
- **Interfaces**: `I{Name}` → `IKnowledgeIngester`, `IVectorStore`
- **DTOs**: `{Name}Dto` (external API), `{Name}Model` (internal)
- **Options**: `{Feature}Options` → `IngestionOptions`, `SearchOptions`
- **Results**: `{Operation}Result` → `IngestionResult`, `SearchResult`

## File Organization

- One public type per file
- Group by feature, not by type (Controllers/, Services/, etc.)
- Tests mirror source structure

## Async

- All I/O is async
- `Async` suffix on async methods
- Use `ValueTask<T>` for hot paths that often complete synchronously
- Prefer `await foreach` over `.ToListAsync()`

## Error Handling

- `Result<T>` pattern for expected failures (validation, not found)
- Exceptions for unexpected/programmer errors only
- Error messages must be actionable

## Configuration

- `IOptions<T>` everywhere
- Validate at startup with `ValidateOnStart()`
- Sensible defaults in `appsettings.json`
- Never hardcode connection strings or secrets

## Dependency Injection

- Register in feature-specific extension methods: `services.AddIngestion()`
- Prefer interfaces over concrete types
- Scoped for per-request, Singleton for stateless services

---

<!-- Add new conventions as they emerge -->
