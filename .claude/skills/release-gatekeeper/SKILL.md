---
name: release-gatekeeper
description: "End-to-end release validation for Connapse — the 'final boss' before any version ships. Downloads the latest alpha from GitHub Releases, deploys an isolated Docker instance (separate from production), installs the CLI without touching the existing install, then systematically tests every feature: UI via Playwright, API via curl/REST, CLI commands, MCP tools, search quality, security testing, boundary conditions, and adversarial inputs. Produces a structured go/no-go release decision with evidence. Use this skill whenever someone says: release test, release validation, release gatekeeper, ready to ship, final QA, pre-release check, validate alpha, test the release, ship it, go/no-go, release candidate testing, end-to-end release test, or wants to verify a Connapse build is ready for public release."
---

# Release Gatekeeper

You are the final authority on whether a Connapse release is ready to ship. Your job is to be thorough, skeptical, and evidence-driven. You are an adversary, not a validator — your primary goal is to find bugs, not confirm things work.

## Philosophy

A release is guilty until proven innocent. Every feature claim must be verified with actual evidence. If something doesn't work, you document it as a failure. Your release decision carries real weight, so be honest.

**The mutation testing mindset:** For every positive test ("container create returns 201"), add a negative counterpart. Ask yourself: "Would this test pass on a completely broken server that returns 200 for everything?" If yes, the test is worthless — add assertions that verify the response body contains the expected data, that different inputs produce different outputs, and that invalid inputs are rejected.

**Evidence, not status codes:** Every test must capture the actual HTTP response body (or screenshot, or CLI output) as proof. Never conclude a test passed based solely on a status code. Log the response, verify specific fields, cross-validate with a separate query.

**Default to FAIL on ambiguity:** If a test result is unclear, mark it as FAIL and flag it for human review. It's safer to raise a false alarm than to miss a real bug.

Three possible verdicts:
- **SHIP IT** — All critical paths pass, no security issues, no data integrity issues, overall score >= 85%
- **SHIP WITH KNOWN ISSUES** — Minor issues documented, no blockers, score >= 75%
- **DO NOT SHIP** — Critical failures, security holes, data loss risks, or score < 75%

## Critical Lessons from Past Runs

These are hard-won lessons from 3 live test runs. Violating any of them will produce false failures and waste time.

### 1. Discover API endpoints before testing — don't hardcode paths

Connapse has TWO auth models and TWO endpoint path conventions:
- **Cookie auth (Blazor Server)** — The UI uses cookie-based auth. There is NO REST `POST /api/v1/auth/login` or `/api/v1/auth/token` endpoint. JWT login is Blazor-internal.
- **PAT auth (X-Api-Key header)** — The only scriptable auth. Create a PAT via the admin UI or use the seeded admin's PAT.
- **Versioned endpoints** (`/api/v1/agents`, `/api/v1/auth/pats`) — Auth and agents use v1 prefix.
- **Unversioned endpoints** (`/api/containers`, `/api/settings`) — Containers, files, search, settings have no version prefix.

**Before generating any test script**, probe the actual endpoints:
```bash
# Discover which paths exist
for path in /api/containers /api/v1/agents /api/v1/auth/pats /api/settings/embedding; do
  STATUS=$(curl -s -o /dev/null -w "%{http_code}" "$BASE_URL$path" -H "X-Api-Key: $PAT")
  echo "$path → $STATUS"
done
```

### 2. Understand response shapes before writing assertions

Container list returns a **paginated wrapper**, not a bare array:
```json
{"items": [...], "totalCount": 5, "hasMore": false}
```
All list endpoints require `?skip=0&take=50` pagination params. File upload returns HTTP **200** (not 201). Always probe one real request and inspect the response before writing assertions.

### 3. Run tests yourself — don't delegate to subagents

Subagents cannot execute Python scripts or curl commands due to sandbox restrictions. They write the scripts but can't run them. **You must run all test scripts directly in the main session.** Don't dispatch subagents to "run the tests" — they will produce scripts but not results.

### 4. Python test scripts: stdlib only, ASCII comments, UTF-8 pragma

All test scripts MUST:
- Use `urllib.request` (stdlib), NOT `requests` (not installed)
- Use `# -*- coding: utf-8 -*-` as the first line
- Stick to ASCII in comments (no box-drawing characters like ═══)
- Use `json.loads()` for parsing (no `jq`)
- Work on Windows Git Bash (no single-quoted JSON in curl)

### 5. Security headers check early — Phase 1, not Phase 3.5

Immediately after health verification, check security headers:
```bash
curl -sI "$BASE_URL/api/containers" -H "X-Api-Key: $PAT" | grep -iE "x-content-type|x-frame|strict-transport|content-security|referrer-policy|server:"
```
This is a 5-second check that catches a common issue. Don't bury it in a 55-test security suite.

### 6. Port override requires editing docker-compose.yml directly

`docker-compose.override.yml` **adds** ports, it doesn't replace them. If the base file has `"5001:8080"` and the override has `"6001:8080"`, Docker maps BOTH ports. To remap, edit the base `docker-compose.yml` directly:
```bash
sed -i 's/"5001:8080"/"6001:8080"/' docker-compose.yml
```

### 7. First-time registration must be tested end-to-end via Playwright

Don't just check "the register page loads". Actually fill and submit the form:
1. Deploy without admin env vars
2. Navigate to `/` — should redirect to `/register`
3. Fill the registration form (email, password, confirm password)
4. Submit and verify the account is created
5. Verify the first user gets Admin role
6. Verify `/register` is no longer accessible (locked down after first user)
7. Tear down and redeploy with seeded admin for remaining tests

### 8. Classify test failures: real bugs vs test bugs

Many "failures" are test script bugs (wrong API path, wrong expected status code, wrong response shape). After running tests, review every failure and classify it:
- **Real product bug** — The product behaves incorrectly
- **Test script bug** — The test used the wrong endpoint/assertion
- **Environment issue** — Timing, network, Docker state

Report the adjusted pass rate alongside the raw pass rate. The adjusted rate (excluding test script bugs) is the one that matters for scoring.

### 9. Path traversal: verify storage, not just HTTP status

When testing path traversal (`../../../etc/passwd` as filename), don't stop at "server returned 200". Follow through:
1. List the container's files — does the file appear with a sanitized name or the traversed path?
2. Try to download the file — does it serve content from outside the container?
3. Check MinIO directly if possible — where was the object actually stored?

### 10. curl on Windows Git Bash: escape JSON properly

Single-quoted JSON doesn't work in Git Bash on Windows. Always use:
```bash
curl -X POST "$URL" -H "Content-Type: application/json" --data-raw "{\"name\":\"test\"}"
```
Or use Python scripts for anything involving JSON request bodies.

## Reference Files

Read these based on your current phase:
- `references/setup-guide.md` — Docker isolation, CLI installation (including credential pre-seeding for non-interactive CLI testing), teardown
- `references/test-checklist.md` — Functional test matrix with scoring weights and mutation testing patterns
- `references/api-surface.md` — Known API surface baseline (compare against what you discover)
- `references/security-tests.md` — **74 security test cases** across auth bypass, IDOR, injection, file upload, CORS, rate limiting, MCP security
- `references/boundary-tests.md` — **100+ boundary condition tests** for strings, pagination, file uploads, concurrent operations, adversarial inputs
- `references/search-golden-dataset.md` — **12 purpose-built test documents + 16 golden queries** with IR metrics (Precision@3, MRR, NDCG)

## Adaptive Testing

The reference files reflect a specific point in time. Your job is to test **what actually exists in the release you're validating**:
1. Read all documentation (Phase 0) — what the product *claims* to do
2. Read the source code (Phase 0.5) — what it *actually* does
3. Build your test plan from the union of docs + code discovery
4. Add tests for every feature you discover, remove tests for removed features

## Tiered Testing Model

Allocate your testing effort according to this model — the current skill spent too much time on smoke/functional and not enough on adversarial/exploratory:

| Tier | Time | Purpose |
|------|------|---------|
| Smoke | 5% | Does it start? Can you log in? Basic health. |
| Functional | 20% | Do features work as documented? (Happy path) |
| Adversarial | 50% | Security testing, boundary conditions, error handling, negative tests |
| Exploratory | 25% | What did we miss? SFDIPOT heuristics, follow-up from earlier findings |

## Execution Pipeline

### Phase 0: Preparation

1. **Identify the release** — `gh release list --repo Destrayon/Connapse` for the latest pre-release/alpha.

2. **Read ALL documentation** — Fetch and read release notes, README, every file in `docs/`, CHANGELOG.md. Build a feature inventory comparing against `references/api-surface.md`.

3. **Check for existing production instance** — `docker ps` and `docker compose ls`. The test instance MUST NOT interfere with production.

4. **Create workspace:**
   ```bash
   WORKSPACE="d:/tmp/connapse-release-test-$(date +%Y%m%d-%H%M%S)"
   mkdir -p "$WORKSPACE"/{evidence,logs,reports}
   ```

### Phase 0.5: Source Code Analysis

The documentation tells you what the product claims to do. The code tells you what it actually does. **Read `references/api-surface.md` for the project structure**, then read the actual source at `D:/CodeProjects/Connapse/src/`.

Focus on:
- All endpoint files (`Connapse.Web/Endpoints/*.cs`) — find undocumented endpoints, required params, auth attributes
- Blazor pages (`Connapse.Web/Components/Pages/**/*.razor`) — find undocumented pages
- MCP tools (`Connapse.Web/Mcp/McpTools.cs`) — find undocumented tools
- CLI commands (`Connapse.CLI/`) — identify all commands and flags
- Auth and identity (`Connapse.Identity/`) — registration flow, role checks, token lifecycle
- Program.cs — middleware, env-specific behavior, admin seeding logic

Produce a **code-vs-docs diff**: features in code but not docs, features in docs but questionable in code, deployment paths not tested.

### Phase 1: Deploy Test Instance

Follow `references/setup-guide.md`. Test two deployment paths:

1. **No seeded admin** (env vars empty) — Deploy WITHOUT `CONNAPSE_ADMIN_EMAIL`/`CONNAPSE_ADMIN_PASSWORD`. Then test the **full first-time registration flow via Playwright**:
   - Navigate to `/` → should redirect to `/register` (setup page)
   - Use Playwright to fill the registration form: email, password, confirm password
   - Submit the form and verify the account is created (redirect to dashboard or login)
   - Verify the first registered user has Admin role (check via API or UI)
   - Navigate to `/register` again — it should be inaccessible (redirect to login, or 404)
   - Try the API with the new account's credentials — verify full access
   - Capture screenshots of each step as evidence
   - Tear down this instance (`docker compose -p connapse-e2e-test down -v`)

2. **Seeded admin** (env vars set) — Redeploy with admin credentials for the full test suite

3. **Immediately after health check**, run the security headers check:
   ```bash
   curl -sI "$BASE_URL/api/containers" -H "X-Api-Key: $PAT" | grep -iE "x-content-type|x-frame|strict-transport|content-security|referrer-policy|server:"
   ```
   Document any missing headers as early findings.

### Phase 2: Install CLI (Isolated)

Download the native binary from the GitHub release. **Use credential pre-seeding** (documented in `references/setup-guide.md`) to enable non-interactive CLI testing:
1. Get a PAT via the API (use Python urllib, not curl with jq)
2. Set `USERPROFILE` to an isolated directory
3. Write `credentials.json` to the isolated `~/.connapse/` path
4. CLI commands now work without interactive login

**Known issue (as of v0.3.2-alpha):** On Windows, the native .NET binary reads `USERPROFILE` from the Windows registry, not the process environment variable. The `USERPROFILE` override may not work. If credential pre-seeding fails, test CLI against the production instance (for non-destructive commands like `--version`, `--help`, `auth whoami`) and note the isolation bug.

### Phase 3: Core Feature Testing (Functional Tier)

Work through `references/test-checklist.md` systematically. For each test:
1. State what you're testing and why
2. Execute via the appropriate surface (UI, API, CLI, MCP)
3. **Capture the full response body** as evidence (not just the status code)
4. **Add a negative counterpart** — verify the system rejects invalid input
5. **Cross-validate** — if API says 201, verify the resource exists via a separate GET
6. Record pass/fail with confidence score (1.0 = verified with evidence, 0.5 = ambiguous, 0.0 = failed)

**Testing surfaces:** API (curl or Python urllib), UI (Playwright snapshots preferred over screenshots — 27K vs 114K tokens), CLI (isolated binary with pre-seeded credentials), MCP (Connapse MCP tools or REST endpoint).

**Test ordering:** Auth → Containers → Files → Search → Bulk Ops → Users → Agents → Settings → Connectors → New features → Cross-surface consistency → Error handling → Documentation accuracy.

**Writing and running test scripts:**
- Write Python test scripts using ONLY `urllib.request` (stdlib). Never use `requests`.
- Add `# -*- coding: utf-8 -*-` as the first line. Use ASCII-only in comments.
- Run scripts with `PYTHONUTF8=1 python3 script.py` on Windows.
- **Run scripts yourself in the main session.** Do NOT delegate to subagents — they lack Bash/Python permissions and will write scripts but cannot execute them.
- Before writing assertions, probe one real endpoint to learn the response shape (paginated wrapper? status 200 vs 201? field names?).

**After running tests, classify failures:**
Every failure must be classified as a real product bug, a test script bug (wrong path/assertion), or an environment issue. Report the adjusted pass rate alongside raw numbers.

### Phase 3.5: Security Testing (Adversarial Tier)

**This is the most important new phase.** Read `references/security-tests.md` for the full test suite. The current skill has zero security tests — this phase fixes that.

Test categories (74 tests total):

| Category | Weight | Severity |
|----------|--------|----------|
| Authentication Bypass (JWT, PAT, Cookie) | 25% | CRITICAL |
| Authorization (BOLA/IDOR/Role) | 20% | CRITICAL |
| Injection (SQL, XSS, Command) | 15% | HIGH |
| File Upload Security | 10% | HIGH |
| Information Disclosure | 10% | MEDIUM |
| CORS/CSP/Headers | 8% | MEDIUM |
| Rate Limiting/DoS | 7% | MEDIUM |
| MCP Security | 5% | HIGH |

**Security verdict thresholds:**
- Any CRITICAL failure → **DO NOT SHIP**
- More than 2 HIGH failures → **DO NOT SHIP**
- More than 5 MEDIUM failures → **SHIP WITH KNOWN ISSUES**

Key tests to prioritize:
- JWT `alg:none` bypass, expired token reuse, signature stripping
- IDOR: access container/file/PAT belonging to another user/role
- Role escalation: Viewer creating containers, Editor managing agents
- Path traversal in file uploads and folder paths
- SQL injection in search queries and path filters
- XSS in filenames, container descriptions, search results
- MCP endpoint auth, tool poisoning, oversized payloads

**Implementation:** Use `curl` or Python `urllib` for API-level security tests (need explicit auth headers). Use `browser_evaluate` with cookie-based fetch for browser-context tests. Create two user accounts with different roles to test IDOR/authorization.

**Critical: Auth model awareness for security tests.** Connapse does NOT have a REST JWT login endpoint. Auth bypass tests must target:
- **PAT auth** (`X-Api-Key` header) — test with invalid/expired/revoked PATs
- **Agent key auth** — test with wrong keys, disabled agent keys
- **Cookie auth** — test via Playwright `browser_evaluate` with tampered cookies
- Do NOT test `POST /api/v1/auth/token` — this endpoint does not exist and will produce 404 false failures.

**Path traversal follow-through:** When `../` filenames are accepted (HTTP 200), verify whether the file was actually stored at a traversed path by listing files and downloading the content. A 200 status alone doesn't confirm the traversal worked.

### Phase 3.7: Boundary & Adversarial Testing (Adversarial Tier)

Read `references/boundary-tests.md` for the full test suite. Focus on the highest-value categories:

1. **String boundaries** — Empty, 1-char, max-length, over-max, Unicode, null bytes, control chars
2. **Pagination abuse** — Negative values, MAX_INT, floats, NaN, missing params, overflow
3. **File upload edge cases** — 0-byte files, double extensions, long filenames, corrupted PDFs, content-type mismatch
4. **Concurrent operations** — Duplicate container creation, upload+delete race, bulk ops during ingestion
5. **Search adversarial inputs** — 10K+ char queries, special chars only, SQL injection, prompt injection, embedding model artifacts

### Phase 4: Search Quality Validation (Deep Testing)

Search is the core value proposition. Read `references/search-golden-dataset.md` for the complete test design.

1. **Upload the 12 purpose-built test documents** (covering disambiguation, paraphrasing, boundary testing, negative controls)
2. **Run the 16 golden queries** with expected results
3. **Compute IR metrics:**
   - Precision@3 >= 0.6 (at least 2 of top 3 relevant)
   - MRR >= 0.7 (first relevant result usually in top 2)
   - NDCG@5 >= 0.6 (good ranking among top 5)
4. **Score calibration:** Verify score distribution (stdev > 0.05, no clustering)
5. **Score determinism:** Same query 3x produces identical results
6. **Cross-mode consistency:** Compare Semantic, Keyword, and Hybrid results
7. **Adversarial search:** Injection attempts, embedding artifacts, oversized queries

### Phase 5: UI Walkthrough

Navigate every page via Playwright. Snapshot before acting, target elements by accessibility role/name. Take screenshots for evidence of important states.

### Phase 5.5: Documentation Quality Assessment

Produce a Documentation Quality section: Overall Grade (A-F), Strengths, Issues (with file paths), Suggestions. Include the code-vs-docs gap analysis from Phase 0.5.

### Phase 5.7: Bug Documentation & Triage

For each bug: Title, Severity (Critical/Major/Minor/Cosmetic), Steps to reproduce, Expected vs Actual, Evidence, Surface affected. Classify as: Release blocker, Should fix, Can ship with, or Unsure — discuss with user.

**Discuss borderline bugs with the user** — don't silently decide severity. Surface ambiguous findings and ask.

### Phase 6: Evidence Collection & Scoring

Use **confidence-weighted scoring** instead of simple pass/fail:

| Confidence | Meaning |
|------------|---------|
| 1.0 | Verified with evidence, cross-validated |
| 0.75 | Verified but not cross-validated |
| 0.5 | Ambiguous, needs human review |
| 0.25 | Likely failing but inconclusive |
| 0.0 | Definitively failed |

**Scoring categories:**

| Category | Weight |
|----------|--------|
| Critical Path (upload → search → results) | 25% |
| Security | 20% |
| Data Integrity | 15% |
| API/CLI Parity | 10% |
| Setup & Install | 10% |
| Error Handling & Boundaries | 10% |
| Documentation Accuracy | 5% |
| Performance | 5% |

**Report quality metrics:**
- Feature coverage % (documented features tested)
- Test depth score (smoke=1, functional=2, error handling=3, boundary=4, adversarial=5)
- Assertion density (meaningful assertions per test)
- SFDIPOT dimension coverage (Structure, Function, Data, Interface, Platform, Operations, Time)

### Phase 7: Document Findings into Connapse

Push findings into the **production** Connapse instance using MCP tools. Check if containers exist first with `container_list`.

**Container: `connapse-release-testing`** — Upload: `release-test-{version}-{date}.md`, `known-issues-{version}.md`, individual bug reports.

**Container: `connapse-architecture`** — Upload/update: `design-patterns.md`, `architecture-decisions.md`, `api-behaviors.md`, `business-rules.md`, `testing-insights.md`.

**Container: `connapse-developer-guide`** — Upload/update: `setup-gotchas.md`, `feature-map.md`.

When updating: search → get → merge → delete → upload (knowledge accumulates, never replaced).

### Phase 8: Release Decision

Present your verdict:

```markdown
# Connapse {version} Release Decision

**Date**: {date}
**Tester**: Release Gatekeeper (AI)
**Verdict**: {SHIP IT | SHIP WITH KNOWN ISSUES | DO NOT SHIP}
**Overall Score**: {score}%

## Score Breakdown
| Category | Weight | Score | Confidence | Weighted |
|---|---|---|---|---|
| Critical Path | 25% | {x}% | {conf} | {y}% |
| Security | 20% | {x}% | {conf} | {y}% |
| Data Integrity | 15% | {x}% | {conf} | {y}% |
| API/CLI Parity | 10% | {x}% | {conf} | {y}% |
| Setup & Install | 10% | {x}% | {conf} | {y}% |
| Error Handling & Boundaries | 10% | {x}% | {conf} | {y}% |
| Documentation Accuracy | 5% | {x}% | {conf} | {y}% |
| Performance | 5% | {x}% | {conf} | {y}% |

## Testing Quality Metrics
- Feature coverage: {x}%
- Test depth score: {x}/5
- Assertion density: {x} per test
- SFDIPOT coverage: {dimensions tested}/7

## Security Findings
{Critical/High/Medium findings with evidence}

## Bugs Found
### Release Blockers | ### Should Fix | ### Can Ship With | ### Discussed with User

## Documentation Quality
{Grade, Strengths, Issues, Suggestions}

## What Worked Well
## Recommendations
## Evidence
All test artifacts saved to: {workspace_path}
```

### Phase 9: Teardown

Ask the user before teardown. Then:
1. `docker compose -p connapse-e2e-test down -v`
2. Remove test CLI binary
3. Verify production is still running
4. Keep workspace for review

## When to Ask for Human Help

Be autonomous by default. Ask only when:
- **Cloud features** need real credentials (mark as SKIPPED)
- **Email-based features** can't be tested without real email
- **Ambiguous failures** — can't tell if it's a bug or environment issue
- **Borderline security findings** — unclear severity

## Retry & Resilience

- Retry once on failure (timing issues)
- Poll health checks rather than failing immediately
- Wait for ingestion completion before testing search
- Use `browser_wait_for` for UI loading states

## Important Notes

- NEVER modify production Docker containers or configuration
- NEVER use production database or MinIO for test data
- Test instance uses completely separate volumes, networks, and ports
- Evidence files in workspace are the permanent record
- Documents uploaded to production Connapse containers are the knowledge legacy
