# Test Checklist — Connapse Release Validation

> **This checklist is a baseline from v0.3.2, not a fixed contract.** Use it as a structural template — the categories, scoring weights, and testing depth are the important parts. The specific endpoints and commands may change between releases. Always build your actual test plan from the release's documentation, adding tests for new features and removing tests for removed ones.

Work through each section in order. For every item, record confidence score + evidence.

## Testing Philosophy: The Mutation Testing Mindset

For EVERY positive test, add a negative counterpart:
- **"Container create returns 201"** → also verify: wrong name returns 400, missing auth returns 401, duplicate name returns 409
- **"Search returns results"** → also verify: different query returns different results, empty query returns error, unrelated query returns low scores
- **"File upload succeeds"** → also verify: the file actually appears in the list, the content is retrievable, deleting it makes it disappear

**The cardinal rule:** If a test would pass on a completely broken server that returns 200 for everything, it's a worthless test. Every test must verify specific response body content, not just status codes.

## Confidence Scoring

Replace simple pass/fail with confidence scores:

| Score | Meaning | When to Use |
|-------|---------|-------------|
| 1.0 | Verified with evidence, cross-validated | Response body matches expected, confirmed via separate query |
| 0.75 | Verified but not cross-validated | Response looks correct, but no independent confirmation |
| 0.5 | Ambiguous, needs human review | Status code is right but body is unexpected, or timing issue |
| 0.25 | Likely failing but inconclusive | Inconsistent behavior across retries |
| 0.0 | Definitively failed | Wrong status code, wrong data, crash, or security failure |

## Scoring System

| Category | Weight | Description |
|---|---|---|
| Critical Path | 25% | Core upload → search → results workflow |
| Security | 20% | Auth bypass, IDOR, injection, file upload security (see security-tests.md) |
| Data Integrity | 15% | Data consistency across surfaces |
| API/CLI Parity | 10% | Same operations work across all access surfaces |
| Setup & Install | 10% | Fresh deployment and CLI installation |
| Error Handling & Boundaries | 10% | Graceful failure on bad input (see boundary-tests.md) |
| Documentation Accuracy | 5% | Reality matches what's documented |
| Performance | 5% | Operations complete in reasonable time |

**Verdict thresholds:**
- SHIP IT: >= 85% overall AND 0 critical-path failures AND 0 security-critical failures AND 0 data-integrity failures
- SHIP WITH KNOWN ISSUES: >= 75% overall AND 0 critical-path failures AND 0 security-critical failures
- DO NOT SHIP: < 75% OR any critical-path failure OR any security-critical failure OR any data-loss scenario

---

## 1. Setup & Install (Weight: 10%)

### 1.1 Docker Deployment
- [ ] Docker zip downloads from GitHub release
- [ ] Zip extracts with expected contents (docker-compose.yml, .env.example, README)
- [ ] `docker compose up -d` starts all services
- [ ] PostgreSQL reaches healthy state
- [ ] MinIO reaches healthy state
- [ ] Web container starts without crash loops
- [ ] Web UI accessible at configured port
- [ ] **Security headers present** — immediately after health check, verify X-Content-Type-Options, X-Frame-Options, Strict-Transport-Security, Content-Security-Policy, Referrer-Policy headers on any API response
- [ ] **Server header** — verify "Server:" header doesn't leak technology details (e.g., "Kestrel")

### 1.1.1 First-Time Registration Flow (No Seeded Admin) — via Playwright
This tests the experience of a brand-new user with no seeded admin. Deploy WITHOUT `CONNAPSE_ADMIN_EMAIL`/`CONNAPSE_ADMIN_PASSWORD` env vars.

- [ ] Navigate to `/` → redirects to `/register` or setup page
- [ ] Registration form renders with email, password, confirm password fields
- [ ] **Fill and submit the registration form** (use Playwright fill + click)
- [ ] Registration succeeds — redirect to dashboard or login page
- [ ] First registered user has Admin role (verify via API or UI)
- [ ] Navigate to `/register` again — should be inaccessible (redirect to login, 404, or "setup complete" message)
- [ ] API access works with the self-registered account
- [ ] Tear down this instance before proceeding to seeded admin tests

**Why this matters:** Previous runs only checked "the register page loads" and declared victory. The full flow — register, verify admin role, verify lockdown — catches real bugs in the registration pipeline.

### 1.1.2 Seeded Admin Deployment
- [ ] First-time admin account creation works (via CONNAPSE_ADMIN_EMAIL/PASSWORD env vars)
- [ ] Admin can log in immediately after deployment

### 1.2 CLI Installation
- [ ] Native binary downloads from release
- [ ] Binary runs (`--help` produces output)
- [ ] `auth login` authenticates against the instance
- [ ] `auth whoami` shows correct user identity
- [ ] CLI version matches the release being tested

### 1.3 Documentation Accuracy — Setup
- [ ] README quick-start instructions work as written
- [ ] Environment variables documented match what's actually needed
- [ ] Port numbers in docs match docker-compose.yml defaults

---

## 2. Authentication & Authorization (Weight: part of Critical Path)

### 2.1 Auth Model Discovery
**First, probe which auth endpoints exist.** Connapse's primary auth is cookie-based Blazor. REST JWT endpoints may or may not be available.

- [ ] Probe `POST /api/v1/auth/token` — if 404, JWT auth is not available (cookie-only)
- [ ] If JWT available: returns valid JWT with correct email/password
  - **Mutation:** Verify response body contains `accessToken` and `refreshToken` fields
- [ ] If JWT available: returns 401 with wrong password
  - **Mutation:** Verify the error message does NOT reveal whether the email exists
- [ ] If JWT NOT available: document as "cookie-only auth" and skip JWT-specific tests
- [ ] PAT auth works via `X-Api-Key` header (this is the primary scriptable auth)
  - **Mutation:** Verify a completely random string returns 401
  - **Mutation:** Verify an empty `X-Api-Key` returns 401

### 2.2 Personal Access Tokens
- [ ] `POST /api/v1/auth/pats` creates a PAT
- [ ] PAT token value is returned only on creation
- [ ] `GET /api/v1/auth/pats` lists tokens (without revealing full token)
- [ ] PAT works as `X-Api-Key` header for API requests
- [ ] `DELETE /api/v1/auth/pats/{id}` revokes token
- [ ] Revoked PAT no longer authenticates

### 2.3 Role-Based Access
- [ ] Admin can access all endpoints
- [ ] Viewer cannot upload or delete files (returns 403)
- [ ] Agent key provides MCP-level access only

### 2.4 UI Auth
- [ ] Login page renders correctly
- [ ] Valid credentials → redirect to dashboard
- [ ] Invalid credentials → error message displayed
- [ ] Logout works and redirects to login

---

## 3. Container Management (Weight: part of Critical Path)

### 3.1 API
- [ ] `POST /api/containers` creates container with name + description
  - **Mutation:** After create, GET the container and verify name/description match what you sent
  - **Mutation:** Verify container count increased by exactly 1
- [ ] `GET /api/containers?skip=0&take=50` lists all containers (pagination REQUIRED)
  - **Mutation:** Verify the container you just created appears in the `items` array
  - **Mutation:** Verify response has `items`, `totalCount`, `hasMore` fields (paginated wrapper)
- [ ] `GET /api/containers/{id}` returns single container
  - **Mutation:** Verify a different container ID returns a different container (not cached)
- [ ] `GET /api/containers/{id}/stats` returns stats (documents, chunks, storage)
  - **Mutation:** Upload a file, verify stats changed (documentCount increased)
- [ ] `DELETE /api/containers/{id}` deletes empty container
  - **Mutation:** Verify GET for the deleted container now returns 404
- [ ] `DELETE /api/containers/{id}` fails with 400 if container has files

### 3.2 CLI
- [ ] `connapse container create <name>` works
- [ ] `connapse container list` shows containers
- [ ] `connapse container delete <name>` works on empty container
- [ ] Container created via API appears in CLI list

### 3.3 UI
- [ ] Container list page shows all containers
- [ ] Create container form works
- [ ] Container detail page loads with file browser

### 3.4 Container Settings
- [ ] `GET /api/containers/{id}/settings` returns settings (or nulls for defaults)
- [ ] `PUT /api/containers/{id}/settings` updates per-container overrides
- [ ] Settings UI page loads and allows changes

---

## 4. File Operations (Weight: part of Critical Path + Data Integrity)

### 4.1 Upload via API
- [ ] `POST /api/containers/{id}/files` accepts file upload (multipart/form-data)
- [ ] Upload a .md file — returns success with document ID
- [ ] Upload a .txt file — returns success
- [ ] Upload a .pdf file — returns success (if PDF parser is available)
- [ ] Upload to a specific path (folder) works
- [ ] File status transitions: Pending → Processing → Ready

### 4.2 Upload via CLI
- [ ] `connapse upload <file> --container <name>` works
- [ ] File uploaded via CLI appears in API file list

### 4.3 Upload via UI
- [ ] Upload form accepts file selection
- [ ] Upload progress is shown
- [ ] File appears in file browser after upload

### 4.4 File Management
- [ ] `GET /api/containers/{id}/files` lists files
- [ ] `GET /api/containers/{id}/files/{fileId}` returns file metadata with status
- [ ] `GET /api/containers/{id}/files/{fileId}/content` returns parsed text
- [ ] `GET /api/containers/{id}/files/{fileId}/content` returns 400 if file not ready
- [ ] `GET /api/containers/{id}/files/{fileId}/reindex-check` returns reindex status
- [ ] `DELETE /api/containers/{id}/files/{fileId}` removes file
- [ ] Folder creation via `POST /api/containers/{id}/folders` works
- [ ] Folder deletion cascades (removes contained files)

### 4.5 Bulk Operations
- [ ] `bulk_upload` — upload multiple files in one operation (API or MCP)
- [ ] `bulk_delete` — delete multiple files in one operation (API or MCP)
- [ ] Bulk operations report per-file success/failure
- [ ] `GET /api/batches/{id}/status` returns ingestion progress for a batch

---

## 5. Search (Weight: Critical Path — 30% of total)

This is the most important section. Connapse's core value is search quality.

### 5.1 Setup Test Data
Create and upload these test documents to a dedicated search-test container:

**doc-1.md**: "Microservices architecture uses circuit breakers to prevent cascading failures. The bulkhead pattern isolates components so that a failure in one service doesn't bring down the entire system."

**doc-2.md**: "PostgreSQL supports JSONB columns for semi-structured data. Use GIN indexes for efficient JSONB queries. The pgvector extension adds vector similarity search capabilities."

**doc-3.md**: "Docker Compose orchestrates multi-container applications. Use health checks to ensure dependencies are ready before starting dependent services. Named volumes persist data across container restarts."

**doc-4.md**: "OAuth2 authorization code flow with PKCE is the recommended approach for public clients. Never store access tokens in localStorage — use httpOnly cookies or in-memory storage."

Wait for all files to reach "Ready" status before testing search.

### 5.2 Semantic Search
> **For deeper search testing, see `references/search-golden-dataset.md`** which replaces this basic 4-doc set with a 12-doc golden dataset and 16 queries with IR metrics.

- [ ] Query: "preventing service failures" → should find doc-1 (circuit breakers)
  - **Mutation:** "preventing cookie expiry" should NOT rank doc-1 highly (different topic)
- [ ] Query: "database vector search" → should find doc-2 (pgvector)
  - **Mutation:** Verify a different query returns different results (system isn't returning static data)
- [ ] Query: "container orchestration" → should find doc-3 (Docker Compose)
- [ ] Query: "secure token storage" → should find doc-4 (OAuth2/PKCE)
- [ ] Results include scores between 0 and 1
  - **Mutation:** Verify scores vary across results (not all 0.0 or all 1.0)
- [ ] Results include file metadata (fileName, chunkIndex)
  - **Mutation:** Verify fileName matches the actual uploaded file name

### 5.3 Keyword Search
- [ ] Query: "circuit breaker" → exact match in doc-1
- [ ] Query: "pgvector" → exact match in doc-2
- [ ] Query: "PKCE" → exact match in doc-4
- [ ] No results for a term not in any document

### 5.4 Hybrid Search
- [ ] Hybrid returns results that combine semantic + keyword relevance
- [ ] Hybrid query: "Docker health" → should find doc-3 with high relevance

### 5.5 Search Parameters
- [ ] `mode=Semantic` returns only semantic results
- [ ] `mode=Keyword` returns only keyword results
- [ ] `mode=Hybrid` (default) returns combined results
- [ ] `topK` parameter limits result count
- [ ] `minScore` parameter filters low-relevance results
- [ ] `path` parameter restricts search to folder subtree

### 5.6 Cross-Surface Search
- [ ] Same query returns consistent results via API, CLI, and MCP
- [ ] Search via UI returns results and displays them correctly

---

## 5.5 MCP Tool Testing (Weight: part of API/CLI Parity)

Test each MCP tool directly. If connected to the test instance via MCP, use the tools. Otherwise, test via the MCP REST endpoint (`POST /mcp` with agent API key).

### 5.5.1 Container Tools
- [ ] `container_create` creates a container
- [ ] `container_list` returns containers with document counts
- [ ] `container_stats` returns stats (documents, chunks, storage, embedding models)
- [ ] `container_delete` deletes an empty container

### 5.5.2 File Tools
- [ ] `upload_file` uploads a file (text content)
- [ ] `upload_file` uploads a file (base64 binary content)
- [ ] `list_files` lists files at root and at a subfolder path
- [ ] `get_document` retrieves full parsed text by document ID
- [ ] `get_document` retrieves full parsed text by virtual path
- [ ] `delete_file` removes a file

### 5.5.3 Bulk Tools
- [ ] `bulk_upload` uploads multiple files in one call
- [ ] `bulk_upload` reports partial failures per file
- [ ] `bulk_delete` deletes multiple files
- [ ] `bulk_delete` reports failures for non-existent files

### 5.5.4 Search Tool
- [ ] `search_knowledge` returns results with Hybrid mode (default)
- [ ] `search_knowledge` respects `mode`, `topK`, `minScore`, `path` parameters
- [ ] `search_knowledge` accepts container by name (not just ID)

---

## 5.6 CLI Advanced Commands

### 5.6.1 PAT Management
- [ ] `connapse auth pat create "Test Token"` creates a PAT
- [ ] `connapse auth pat list` shows PATs
- [ ] `connapse auth pat revoke <id>` revokes a PAT

### 5.6.2 Reindex
- [ ] `connapse reindex --container <name>` triggers reindex
- [ ] `connapse reindex --container <name> --force` forces full reindex

---

## 6. Agent Management (Weight: part of API/CLI Parity)

### 6.1 Agent CRUD
- [ ] `POST /api/v1/agents` creates an agent
- [ ] `GET /api/v1/agents` lists agents
- [ ] `GET /api/v1/agents/{id}` returns single agent
- [ ] `PUT /api/v1/agents/{id}/status` enables/disables agent
- [ ] `DELETE /api/v1/agents/{id}` deletes agent

### 6.2 Agent API Keys
- [ ] `POST /api/v1/agents/{id}/keys` creates API key
- [ ] Key token shown only on creation
- [ ] `GET /api/v1/agents/{id}/keys` lists keys (prefix only)
- [ ] `DELETE /api/v1/agents/{agentId}/keys/{keyId}` revokes key
- [ ] Agent key works for MCP endpoint authentication
- [ ] Disabled agent's key returns 401

### 6.3 Agent UI
- [ ] Agent management page lists agents
- [ ] Create agent form works
- [ ] API key generation and display works in UI

---

## 7. Settings & Configuration (Weight: part of Documentation Accuracy)

### 7.1 Global Settings API
For each category (embedding, chunking, search, llm, upload):
- [ ] `GET /api/settings/{category}` returns current settings
- [ ] `PUT /api/settings/{category}` updates settings
- [ ] Changes take effect without restart (live reload)

### 7.2 Settings UI
- [ ] Settings page renders all categories
- [ ] Each category shows current values
- [ ] Save button persists changes
- [ ] Connection test buttons work (for providers that support it)

### 7.3 Embedding Configuration
- [ ] Can view current embedding provider and model
- [ ] `GET /api/settings/embedding-models` shows models with vector counts
- [ ] Reindex endpoint works: `POST /api/settings/reindex`
- [ ] Reindex status endpoint: `GET /api/settings/reindex/status`

### 7.4 Connection Testing
- [ ] `POST /api/settings/test-connection` with Embedding category returns success/failure
- [ ] `POST /api/containers/test-connection` validates connector config before creation
- [ ] Connection test returns meaningful error on failure (not just 500)

---

## 8. Error Handling (Weight: 10%)

### 8.1 API Error Responses
- [ ] Invalid JSON body → 400 with clear error message
- [ ] Missing required field → 400 with field-specific error
- [ ] Non-existent resource → 404
- [ ] Unauthorized request → 401
- [ ] Forbidden action → 403
- [ ] Errors follow RFC 7807 Problem Details format

### 8.2 Write Guards
- [ ] S3 connector containers block uploads (400 write_denied)
- [ ] S3 connector containers block deletes (400 write_denied)
- [ ] AzureBlob connector containers block writes

### 8.3 Edge Cases
- [ ] Upload empty file — handled gracefully
- [ ] Upload very large file name (255+ chars) — handled
- [ ] Create container with duplicate name — returns error
- [ ] Search with empty query — returns error or empty results
- [ ] Delete non-empty container — returns 400 with clear message

---

## 9. Performance (Weight: 5%)

Track response times for these operations:

- [ ] Auth token request: < 2 seconds
- [ ] Container list: < 1 second
- [ ] File upload (1MB): < 10 seconds to start processing
- [ ] Search query: < 3 seconds
- [ ] Ingestion pipeline (upload to searchable): < 60 seconds for a small .md file
- [ ] UI page load (any page): < 5 seconds

---

## 10. Cross-Surface Consistency (Weight: Data Integrity — 20%)

These tests verify that data is consistent regardless of which surface created or reads it:

- [ ] Container created via API → visible in CLI `container list` and UI
- [ ] File uploaded via CLI → searchable via API and visible in UI
- [ ] Search via API, CLI, and UI returns same results for same query
- [ ] File deleted via API → gone from CLI and UI
- [ ] Agent created via API → visible in admin UI
- [ ] Settings changed via API → reflected in Settings UI

---

## 11. Cloud Features (Conditional — ask human)

These require real cloud credentials. Skip if unavailable, don't count against score.

### 11.1 AWS SSO
- [ ] AWS SSO settings configurable in admin
- [ ] Device auth flow initiates
- [ ] Identity linking works

### 11.2 Azure AD
- [ ] Azure AD settings configurable in admin
- [ ] OAuth2+PKCE flow initiates
- [ ] Identity linking works

### 11.3 S3 Connector
- [ ] Create container with S3 connector
- [ ] Background sync pulls files from bucket
- [ ] Search works on synced files

### 11.4 Azure Blob Connector
- [ ] Create container with AzureBlob connector
- [ ] Background sync pulls files
- [ ] Search works on synced files

---

## 12. Documentation Accuracy Audit (Weight: 10%)

For each documentation file, verify key claims:

### README.md
- [ ] Quick start instructions work
- [ ] Feature list matches available features
- [ ] Architecture diagram matches actual components
- [ ] All linked docs files exist

### docs/api.md
- [ ] All documented endpoints exist and respond
- [ ] Request/response formats match documentation
- [ ] Auth requirements match documentation
- [ ] Error codes match documentation

### docs/deployment.md
- [ ] Docker deployment instructions work
- [ ] Environment variables are correct and complete
- [ ] Port numbers and service names are accurate

### Release Notes
- [ ] New features listed actually work
- [ ] Breaking changes (if any) are documented
- [ ] Version numbers are consistent across release, Docker image, CLI

---

## 13. Documentation Quality (Advisory — does not block release)

Evaluate each doc file holistically. This is about the experience of reading the docs, not just whether they're technically accurate.

### README.md
- [ ] Clear value proposition in first 3 sentences
- [ ] Quick start actually gets someone from zero to working in < 5 minutes
- [ ] Feature list is scannable and not misleading
- [ ] No broken links or missing images
- [ ] Badges are current and accurate

### docs/api.md
- [ ] Examples are copy-pasteable (correct curl syntax, valid JSON)
- [ ] Auth requirements are clear for each endpoint
- [ ] Error responses are documented, not just happy paths
- [ ] Organized logically (auth → resources → search → settings)

### docs/deployment.md
- [ ] Covers common gotchas (env vars, port conflicts, first-run behavior)
- [ ] Production vs development setup is clear
- [ ] SSL/HTTPS guidance is present or noted as TODO

### docs/connectors.md
- [ ] Each connector type has setup instructions
- [ ] Write guard behavior is explained clearly
- [ ] Cloud connector prerequisites are listed

### Overall Documentation
- [ ] Consistent formatting across all files
- [ ] No outdated version references
- [ ] Cross-references between docs work
- [ ] A newcomer could set up and use the product from docs alone

---

## Additional Test Suites (Separate Reference Files)

The following test suites are critical for release validation and are documented in separate files for detail:

- **`references/security-tests.md`** — 74 security test cases (auth bypass, IDOR, injection, file upload, CORS, rate limiting, MCP). Weight: 20% of total score. Any CRITICAL failure = DO NOT SHIP.
- **`references/boundary-tests.md`** — 100+ boundary condition tests (strings, pagination, file uploads, concurrent ops, adversarial inputs). Weight: contributes to Error Handling & Boundaries category.
- **`references/search-golden-dataset.md`** — 12 purpose-built test documents + 16 golden queries with IR metrics (Precision@3, MRR, NDCG). Replaces the 4-doc test set above for thorough search validation.
