# Research Checklist Reference

Detailed checklists for each research dimension. The skill body describes *what* to do; this file describes *how* to do it thoroughly.

## Codebase Marker Patterns

### Primary markers (high signal)
```regex
TODO(?!Write)        # Skip TodoWrite tool references
FIXME
HACK
WORKAROUND
TEMPORARY
DEFERRED|deferred
XXX
```

### Secondary markers (check context)
```regex
NotImplementedException
throw new NotSupportedException
// removed
// disabled
// old
// legacy
// placeholder
// stub
// mock.*prod
```

### Code smell patterns
```regex
catch\s*\(Exception\s+\w+\)\s*\{?\s*\}    # Empty catch blocks
Thread\.Sleep                                # Blocking sleep in async code
\.Result\b|\.Wait\(\)                        # Sync-over-async
Console\.Write                               # Console output in non-CLI code
#pragma warning disable                      # Suppressed warnings
\[Obsolete\]                                 # Deprecated code still in use
```

## GitHub Query Templates

### Stale issue detection
An issue is "stale" if:
- `needs-triage` label AND created > 7 days ago
- No milestone AND no recent comments (14+ days)
- Assigned but no linked PR AND no update in 14+ days

### PR follow-up detection
Search merged PR bodies for:
- "follow-up", "follow up", "followup"
- "TODO", "deferred", "future"
- "out of scope", "separate PR", "separate issue"
- "tech debt", "cleanup"

### Discussion analysis
Check each discussion for:
- Unanswered questions (no replies, or replies without resolution)
- Design decisions that haven't been implemented
- Feature requests from users

## Test Coverage Analysis

### Coverage gap detection strategy
1. List all public classes in `src/` (excluding models/DTOs/records)
2. For each, check if a corresponding test file exists in `tests/`
3. Flag services, endpoints, and providers without tests
4. Check recently modified files (last 2 weeks) for test coverage

### Test quality signals
- Tests that only assert `NotNull` (weak assertions)
- Tests with no arrange phase (testing nothing)
- Tests that depend on external services without mocking
- Commented-out test methods

## Cross-Cutting Concern Checklist

### Security
- [ ] All API endpoints have `[Authorize]` or explicit `[AllowAnonymous]`
- [ ] User input is validated before use (especially in SQL, file paths, redirects)
- [ ] Secrets are not logged or exposed in error messages
- [ ] CORS is configured (not `*` in production)
- [ ] Rate limiting on auth endpoints
- [ ] CSRF protection on state-changing operations

### Error handling
- [ ] All async operations have try-catch at boundary
- [ ] Error responses include correlation ID
- [ ] Exceptions are logged with context (not just message)
- [ ] Cancellation tokens are propagated
- [ ] Timeout handling on external calls

### Performance
- [ ] Database queries use pagination (not `.ToListAsync()` on unbounded sets)
- [ ] N+1 queries are avoided (use `.Include()` or batch)
- [ ] Heavy operations use caching where appropriate
- [ ] Background processing for long-running tasks
- [ ] Connection pooling configured

### Observability
- [ ] Critical paths have structured logging
- [ ] Error paths log sufficient context for debugging
- [ ] Performance-sensitive paths have timing logs
- [ ] Health check endpoint exists
- [ ] Configuration is logged at startup (sans secrets)

## De-duplication Strategy

Before presenting a finding, search for it in:

1. **GitHub issues**: `gh issue list --search "{keywords}" --json number,title`
2. **State files**: Grep `.claude/state/issues.md` for related keywords
3. **Project board**: Check if a similar item exists in board items
4. **MEMORY.md**: Check if it's a known limitation or intentional decision

If found, categorize as "Already Tracked" with the reference.
