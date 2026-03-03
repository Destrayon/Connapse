# Project Metadata Reference

Quick reference for valid labels, milestones, and project board details.

## Repository
- Owner: Destrayon
- Repo: Connapse
- Project Board: #3 "Connapse Roadmap"

## Labels

### Type Labels (exactly one per issue)
- `type: bug`
- `type: feature`
- `type: enhancement`
- `type: docs`
- `type: refactor`
- `type: test`
- `type: infrastructure`

### Area Labels (one or more per issue)
- `area: core`
- `area: web-ui`
- `area: api`
- `area: cli`
- `area: mcp`
- `area: ingestion`
- `area: search`
- `area: database`
- `area: storage`
- `area: identity`
- `area: agents`

### Workflow Labels
- `needs-triage` (always add)
- `good first issue`
- `help wanted`
- `blocked`
- `breaking-change`
- `security`
- `dependencies`

### Priority Labels (use as project board field, not issue label)
- P0-Critical
- P1-High
- P2-Medium
- P3-Low

### Size Labels (auto-applied by PR size workflow, not manually set on issues)
- size/XS, size/S, size/M, size/L, size/XL

## Milestones
- `v0.3.1` — Polish, tech debt, CI improvements
- `v0.4.0` — Cross-container search, live events, local connector ACLs
- `v0.5.0` — New connectors (GitHub, Notion, Slack) and embedding providers

## Architecture Areas (for implementation notes)
```
src/
├── Connapse.Web/          # Blazor WebApp (UI + API endpoints)
├── Connapse.Core/         # Domain models, interfaces, shared logic
├── Connapse.Identity/     # Auth: Identity, PAT, JWT, RBAC, cloud identity
├── Connapse.Ingestion/    # Document parsing, chunking, embedding pipeline
├── Connapse.Search/       # Vector search, hybrid search, reranking
├── Connapse.Agents/       # Agent orchestration, tool definitions, memory
├── Connapse.Storage/      # Vector DB, document store, connectors, cloud providers
└── Connapse.CLI/          # Command-line interface
```

## Branch Naming Convention
- `feature/<issue-number>-short-description`
- `fix/<issue-number>-short-description`
- `refactor/<issue-number>-short-description`
- `docs/<issue-number>-short-description`
- `chore/<issue-number>-short-description`
