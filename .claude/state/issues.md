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

### FixedSizeChunker IndexOutOfRangeException

**Severity**: High (runtime crash)

**Description**: `FindNaturalBreakpoint` method accessed array indices beyond content length when target position equaled or exceeded content.Length, causing IndexOutOfRangeException.

**Root Cause**: Loop started at `target` without checking if `target < content.Length` before accessing `content[i]`.

**Fix**: Added bounds check at method start (`if (target >= content.Length) return content.Length`) and added `i < content.Length` check in all four search loops.

**Status**: Fixed in Phase 8 (2026-02-05) — discovered by unit tests before reaching production

### RecursiveChunker ArgumentOutOfRangeException

**Severity**: High (runtime crash)

**Description**: `ChunkAsync` method passed invalid `startIndex` to `IndexOf`, causing ArgumentOutOfRangeException when `currentOffset` exceeded content length.

**Root Cause**: `currentOffset` tracking could grow beyond actual content length, especially with overlap calculations.

**Fix**: Clamped `currentOffset` with `Math.Min(currentOffset, content.Length)` and added bounds check before calling `IndexOf`.

**Status**: Fixed in Phase 8 (2026-02-05) — discovered by unit tests before reaching production

### Parser Exception Handling - Cancellation Suppressed

**Severity**: Medium (cancellation didn't work)

**Description**: TextParser and OfficeParser caught `OperationCanceledException` in generic exception handler, preventing cancellation tokens from propagating properly.

**Root Cause**: Generic `catch (Exception ex)` block caught all exceptions including `OperationCanceledException` and `NotSupportedException`, returning empty documents instead of throwing.

**Fix**: Added explicit catch blocks to rethrow `OperationCanceledException` and `NotSupportedException` before the generic handler in both parsers.

**Status**: Fixed in Phase 8 (2026-02-05) — discovered by unit tests before reaching production

---

<!-- Add issues as discovered -->
