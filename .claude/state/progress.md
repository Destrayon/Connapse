# Progress

Current status and recent work. Update at end of each session.

---

## Current Status (2026-02-28) — v0.3.0 Session A complete

**Branch:** `feature/0.3.0` | **Last shipped:** v0.2.2

### v0.3.0 Plan — ready to implement

Full plan at [docs/v0.3.0-plan.md](../../docs/v0.3.0-plan.md). Key decisions in [decisions.md](decisions.md).

| Session | Focus | Status |
|---------|-------|--------|
| A | IConnector abstraction + schema migration + IContainerSettingsResolver | **COMPLETE** |
| B | Filesystem connector (FileSystemWatcher) + InMemory connector | Pending |
| C | S3 connector (DefaultAWSCredentials / IAM) | Pending |
| D | AzureBlob connector (DefaultAzureCredential) | Pending |
| E | Cloud RBAC — AWS OIDC federation + Azure OAuth2 identity linking | Pending |
| F | OpenAI + Azure OpenAI embedding providers; ILlmProvider formalization | Pending |
| G | Agentic search (SearchMode.Agentic, iterative LLM-driven retrieval) | Pending |
| H | Testing + docs | Pending |

---

## Shipped Versions

| Version | Key feature | Sessions |
|---------|-------------|----------|
| v0.2.2 | CLI self-update (`connapse update`, passive notification, Windows bat swap) | 19 |
| v0.2.0 | Security & auth: Identity project, cookie+PAT+JWT, RBAC, audit logging, agent entities, CLI auth, 256 tests | 8–18 |
| v0.1.0 | Container file browser, hybrid search, ingestion pipeline, MCP server | 1–7 |

**Test baseline (v0.2.2):** 256 tests across 12 projects, all passing.

---

## Known Issues

See [issues.md](issues.md).
