# README MCP Listing Optimization — Design Spec

**Date**: 2026-03-15
**Issues**: #258, #259, #260, #261
**Milestone**: v0.3.3 (MCP listing optimization)
**Scope**: README.md additions only — no code changes

## Goal

Improve Connapse's discoverability, trust signals, and time-to-first-use on MCP listing platforms (Glama, Smithery, MCP Registry) by adding four sections to README.md.

## Approach

Single branch, single PR, four issues. All content lives in README.md (not separate docs) because Glama and Smithery scrape README directly. Estimated ~80 lines added — well under the 300-line PR limit.

## Sections

### 1. Example Prompts (#258)

**Placement**: Collapsible `<details>` block after the "Write guards" note (line 219) and before the `---` divider (line 221). Keeps all MCP content together.

**Summary text**: `<summary><strong>Example prompts</strong> — what to ask your agent</summary>`

**Content**: 8 natural-language prompts covering the main tool categories:

- Container creation: *"Create a container called 'project-research' for my architecture notes"*
- Upload: *"Upload all the PDFs in my downloads folder to the project-research container"*
- Search: *"Search my project-research container for information about rate limiting strategies"*
- Browse: *"List all files in the /notes/ folder of my project-research container"*
- Retrieve: *"Get the full text of distributed-systems-notes.md from project-research"*
- Update: *"Delete meeting-2026-03-14.md from project-research and upload this updated version"*
- Bulk ops: *"Delete all files in the /drafts/ folder of project-research"*
- Stats: *"How many documents and chunks are in my project-research container?"*

**Acceptance criteria coverage**:
- [x] 5-10 example prompts covering main tool categories (CRUD, search, bulk ops) — 8 prompts
- [x] Prompts are realistic and demonstrate the value proposition
- [x] Section exists in README

### 2. Troubleshooting (#259)

**Placement**: Collapsible `<details>` block after the Example Prompts block, still before the `---` divider and `## Features`. This keeps it inside the MCP section as a sub-block, not a top-level section.

**Summary text**: `<summary><strong>Troubleshooting</strong></summary>`

**Content**: 5 entries, each as bold question + concise answer:

1. **Connection refused on localhost:5001** — Docker not running or port conflict. Check `docker compose ps` and `docker compose logs web`.
2. **401 Unauthorized / API key not working** — Verify the key in Settings > Agent API Keys. Keys are shown once at creation.
3. **Tools not appearing in Claude** — Restart your MCP client after config changes. Verify endpoint with `curl http://localhost:5001/mcp`.
4. **Uploads failing or timing out** — Check file type is in the allowlist. Max file size depends on server config.
5. **Search returns no results** — Documents need time to embed after upload. Check container stats for embedding progress.

**Acceptance criteria coverage**:
- [x] Covers 3-5 most common setup issues — 5 entries
- [x] Uses collapsible `<details>`
- [x] Covers: connection refused, 401 unauthorized, tools not appearing

### 3. S3/Azure Keywords (#260)

**Placement**: In-place edits to existing text. Three locations:

1. **Features bullet** (line 228): `"S3 (IAM auth), or Azure Blob (managed identity)"` → `"Amazon S3 (IAM auth), or Azure Blob Storage (managed identity)"`
2. **Architecture diagram** (line 293): `"S3  │  Azure Blob"` → `"Amazon S3  │  Azure Blob Storage"` — widen the box borders to maintain alignment
3. **Key Technologies connectors** (line 322): `"S3, Azure Blob"` → `"Amazon S3, Azure Blob Storage"`

Also scan for any other occurrences of bare "S3" or "Azure Blob" that should use full product names. Instances inside technical context (config field names, connector type enums) keep their short form.

**Acceptance criteria coverage**:
- [x] README explicitly mentions "Amazon S3" and "Azure Blob Storage" in a detectable format
- [x] If `glama.json` supports integration declarations — check and add if applicable

### 4. FAQ (#261)

**Placement**: New `## FAQ` section after the `---` divider on line 389 and before `## Contributing` (line 391).

**Content**: 5 entries as bold Q + inline A:

1. **Does Connapse require internet access?** — No. Use Ollama for fully offline embeddings and search.
2. **How many documents can it handle?** — Thousands per container. Built on PostgreSQL + pgvector.
3. **Which MCP clients work with Connapse?** — Any client supporting Streamable HTTP transport — Claude Desktop, Claude Code, VS Code, Cursor, and others.
4. **Is my data private?** — Fully self-hosted. With Ollama, nothing leaves your machine. Cloud providers (OpenAI, Azure) are optional.
5. **What embedding providers are supported?** — Ollama (local), OpenAI, and Azure OpenAI. Switch at runtime without re-deploying.

**Acceptance criteria coverage**:
- [x] 4-6 FAQ entries — 5 entries
- [x] Answers are concise (1-2 sentences each)
- [x] Covers: internet requirement, document scale, client compatibility, data privacy

## Out of Scope

- #254 (zero-setup trial) — infrastructure work, not docs
- #255 (VS Code install button) — requires extension registry research
- #257 (competitive positioning) — requires competitor feature verification
- New documentation pages — everything stays in README for listing platform scraping
