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

<!-- Add issues as discovered -->
