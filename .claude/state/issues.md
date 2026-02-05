# Known Issues

Bugs, tech debt, and workarounds. Prevents future sessions from re-discovering the same problems.

---

## Template

### Issue Title

**Severity**: Low | Medium | High | Critical

**Description**: What's wrong?

**Repro**: How to trigger?

**Workaround**: Current mitigation?

**Status**: Open | In Progress | Fixed in vX.X

---

## Expected Gotchas

### Ollama Cold Start

**Severity**: Low

**Description**: First Ollama request after startup takes 30-60s while model loads.

**Workaround**: Send warmup request on app start, or document expected delay.

### SQLite Write Contention

**Severity**: Medium

**Description**: SQLite struggles with concurrent writes → "database is locked" errors.

**Workaround**: Queue writes or use PostgreSQL for multi-user scenarios.

### Large File Chunking Memory

**Severity**: Medium

**Description**: Very large files (>100MB) can spike memory during chunking.

**Workaround**: Stream-based chunking for large files, or reject files over threshold.

---

## Fixed Issues

### DI Scope Violation: Singleton Services Consuming Scoped Services

**Severity**: Critical (app wouldn't start)

**Description**: `IngestionWorker` (singleton via `AddHostedService`) directly injected `IKnowledgeIngester` (scoped). `McpServer` (singleton) directly injected `IKnowledgeSearch` and `IDocumentStore` (both scoped). This violated DI scope rules and prevented app startup.

**Root Cause**: Scoped services depend on DbContext (also scoped). Singletons cannot hold references to scoped services because scoped instances are disposed after each request.

**Fix**: Both services now inject `IServiceScopeFactory` and create scopes when accessing scoped dependencies:
- `IngestionWorker.ProcessJobAsync()` creates a scope per job
- `McpServer.ExecuteSearchKnowledgeAsync()` and `ExecuteListDocumentsAsync()` create scopes per tool invocation

**Status**: Fixed

---

<!-- Add issues as discovered -->
