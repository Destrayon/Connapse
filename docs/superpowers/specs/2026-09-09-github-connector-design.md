# GitHub connector design

**Date:** 2026-09-09 (revised 2026-09-10: scoped to public repositories only; GitHub App, private repos, and permission filtering deferred to a later epic)
**Status:** Approved; scoped to public repositories (GitHub App deferred)
**Epic:** Destrayon/Connapse#508
**Research:** `docs/research/github-connector-implementation-2026-09-09.md`, `docs/research/next-connectors-rag-shape-graphrag-2026-09-09.md`
**Milestone target:** v0.5.0 (new connectors)

## Purpose

Add a GitHub connector that indexes a **public** repository's **markdown docs** and its **issues and pull requests** for search. It is the next connector after AWS and Azure because it is the strongest demand signal among the developers who wire Connapse's MCP server into coding agents, and because its content shapes (prose docs and discussion records) exercise Connapse's chunkers differently from the cloud-blob sources shipped so far. Repository **code is out of scope**; code retrieval is better served by a graph of symbols and is deferred to the GraphRAG phase. **Discussions** are out of scope for this epic.

**This epic is public repositories only, read unauthenticated.** There is no GitHub App, no private-repository support, no per-user identity linking, and no query-time permission filtering in this epic. Public content is world-readable, so there is nothing to permission-check. Authentication and private repositories are designed in "Deferred to a later epic" below so the work is not lost, but they are not built now.

This design is grounded in the two research reports above. Where a claim here is not yet settled by GitHub's documentation, it is called out in "Open items."

## Governing principles (carried from existing work)

1. **Connectors are read-only repository access.** A connector is read access to one backing store. The GitHub connector does not introduce a new connector abstraction; it implements the existing read interface and emits both file-shaped (docs) and record-shaped (issues, pull requests) content through the existing ingestion pipeline.
2. **No stored long-lived credentials.** This epic stores no GitHub credential at all, because it reads only public repositories unauthenticated. The later private-repo epic will use a GitHub App (short-lived installation tokens, encrypted key on host), never a pasted personal access token, matching AWS and Azure.
3. **Per-object permission parity, fail closed** (applies in full only to the later private-repo epic). GitHub's native read permission is repository-level, so the per-object decision for every object in a repo is "may this user read this repository," satisfied by fan-out. In this public-only epic the decision is trivially ALLOW for everyone, because the content is public.

## Scope

**In scope (this epic):** connection-less public-repository sources; markdown docs sync; issues and pull requests sync with comment assembly; a record chunker; graph edges stored as metadata for a later GraphRAG phase; public-to-private detection that withholds content when a repo stops being public; GitHub.com (Enterprise Server deferred with auth).

**Out of scope (this epic):** GitHub App authentication; private and internal repositories; per-user identity linking; query-time permission filtering; repository code; discussions; webhooks (polling is the default); GitLab and Gitea; writing anything back to GitHub.

## Source model: two sources per repository

A GitHub **source** represents one repository and one **content kind**. A repository is represented by up to two sources:

- **Docs source** — file-shaped. Chunker: DocumentAware. Cursor: last-synced commit SHA. Synced by git clone (no API rate cap).
- **Issues-and-pull-requests source** — record-shaped. Chunker: the new record chunker. Cursor: last-seen `updated_at` timestamp (with tie dedup). Synced through the issues endpoint, which returns both issues and pull requests.

Rationale: keeping the two as separate searchable sources lets an MCP client or user target "the repo's docs" or "its issues and pull requests" as distinct things, which matches how developers think about them and how a coding agent would choose. It also keeps each source to one simple cursor and one chunker, avoiding a composite cursor or per-document chunker selection within a source. An "add GitHub repository" wizard creates both sources at once so the split costs no extra clicks.

The source `ScopeJson` carries: `owner`, `repo`, the stable numeric `repoId`, `kind` (`Docs` | `IssuesAndPullRequests`), and kind-specific filters (doc path globs defaulting to markdown; issue label/state filters).

## Connection model: connection-less, public only

`Source.ConnectionId` becomes **nullable**, and the connector provider is identified on the source. In this epic every GitHub source is **connection-less**: pinned to `github.com`, read unauthenticated, and re-verified public on every sync. This is the zero-setup path: paste a public repo URL and search.

This nullability is the enabling change that also lets the later epic attach a public source to a GitHub App connection for rate relief, and lets future connection-less sources (web crawl, public package docs) exist at all, which the current non-nullable `ConnectionId` cannot express.

### Interaction with the connector-source-split epic (#348)

- `documents.owner_id` is a generated `COALESCE(container_id, source_id)` and does not involve `ConnectionId`; making `ConnectionId` nullable does not touch that invariant.
- Source creation stays **admin-only over REST** (the decision from the programmatic-source-configuration safety review). A public-repo source adds content every user can search, so it remains admin-gated.
- `IConnectorFactory.Create(Source, Connection, secret)` gains an overload (or a nullable `Connection` parameter) that builds a connector from a connection-less source, resolving to the public GitHub provider pinned to github.com.

## Rate limits (public, unauthenticated)

Unauthenticated GitHub REST is **60 requests/hour/IP**, shared by every connection-less source on the host. Mitigations:

- **Docs sync by git clone**, which has no API rate cap. Initial sync clones; incremental sync fetches and diffs against the stored commit SHA.
- **Issues/pull-requests sync** uses the REST API under the 60/hour budget. This is fine for small and medium repositories; a very large repository's first sync will be slow (hours). Accepted for this epic.
- **ETags do not help:** a spike on 2026-09-09 confirmed an unauthenticated 304 still decrements the 60/hour limit (remaining 56 → 55), so conditional requests conserve bandwidth but not quota.
- Attaching a GitHub App later (the deferred epic) lifts the limit to ≥5,000/hour and makes API-based doc sync and fast issue sync viable.

## Incremental sync

**Docs source.** Clone the repo; list files filtered to the doc path globs (default markdown). Incremental sync diffs the working tree against the stored commit SHA, which gives added, modified, removed, and renamed files directly (exact deletion detection). Cursor: head commit SHA.

**Issues-and-pull-requests source.** Page `GET /repos/{o}/{r}/issues?since=<ts>&state=all&sort=updated&direction=asc&per_page=100`, which returns both issues and pull requests (a pull request carries a `pull_request` field). Hydrate pull-request-only metadata (merge state, files, head/base) via the pulls API, and review comments via the since-capable review-comments endpoint. Cursor: last-seen `updated_at`, re-requested as `>=` with id-dedup to survive same-second ties. Hard deletes and transfers are invisible to a since-sweep, so a **periodic full re-list** (page all with `state=all`, diff the id set) reconciles deletions; docs need no such reconciliation because the clone diff reports removals.

A repository rename or transfer keeps the stable numeric `repoId`; sync keys on the id and follows GitHub's redirect, updating the stored `owner/repo`.

`SyncDelta.RequiresFullResync` (the existing mechanism) is the channel for any provider "start over" signal.

### Public-to-private safety re-check

A public repo can be made private after indexing. On every sync, confirm the repo is still public: an unauthenticated `GET /repos/{o}/{r}` returns 404 once it is private (inferred from GitHub's privacy behavior; confirm during the docs-sync phase). On a 404, stop syncing the source and **withhold** its already-indexed documents from search results (do not hard-delete) until an admin re-establishes access, which in the later epic means attaching an App connection. The existing source-level withholding mechanism is the precedent.

## Record assembly and rendering

Each issue or pull request becomes one document. The assembled markdown is:

```
# {number}: {title}
Author: {login} · State: {state} · Labels: {...} · Created: {date} · Closed/Merged: {date}
Closes: #{...}  Parent: #{...}  Files: {...}   (edges, when present)

{body}

--- Comment by {login} ({date}) ---
{comment body}
...
```

Comments **are included** by default (configurable), which is the deliberate difference from Onyx/LlamaIndex/Langchain and the point of the chunker experiment. The content is already GitHub-Flavored Markdown, so no structural rendering is needed; Markdig is used only as an optional local normalizer, and GitHub's HTML-render endpoint is avoided because it costs an API call per body.

**Virtual path:** `/issues/{number}`, `/pulls/{number}` (docs keep their repo-relative path). The number is stable across title edits; the real `html_url` is stored in metadata as the citation link.

**Graph edges stored as metadata** (for the later GraphRAG phase, resolved across sources then): `closes` / `closed_by` (`closingIssuesReferences` via GraphQL; issue `closed_by`), `cross_referenced` (timeline), `connected`/`disconnected` (timeline), sub-issue `parent`/`children` (REST, GA 2025), `files` touched (`pulls/{n}/files`), labels, milestone. Edges needing GraphQL or timeline calls are captured unauthenticated within the 60/hour budget; if that proves too tight for a large repo, edge capture can be deferred per-record and backfilled once an App raises the limit.

## Record chunker

A new `ChunkingStrategy.Record` member and an `IChunkingStrategy` implementation. Behavior: **one record = one document, emitted as-is with its field header; split only when it exceeds the token budget.** Oversized records split into per-comment child chunks, each prefixed with the parent title, number, and field header so every child is self-contained. No boundary-detection sophistication, because most records fit one chunk and the research found no ablation favoring clever ticket boundaries. Set as the default chunker on issues-and-pull-requests sources via the per-source chunking override; docs sources keep DocumentAware.

## Components and files (indicative)

- `Connapse.Core`: `Source.ConnectionId` nullable; provider on source; `ChunkingStrategy.Record`; a record/content-kind DTO if one is warranted; `IConnectorFactory` overload for connection-less sources.
- `Connapse.Storage`: `GitHubConnector : ISyncCursorConnector` (branches on source kind; unauthenticated github.com); `GitHubConnectorConfig`; a GraphQL/timeline client for edge capture; migration making `connection_id` nullable.
- `Connapse.Ingestion`: `RecordChunker`.
- `Connapse.Web`: GitHub provider admin page (step cards per the provider-page convention, but no App-setup card in this epic); the "add repository" wizard creating both sources from a public repo URL.

No changes to `Connapse.Identity` in this epic (identity linking and the permission resolver are deferred).

## Testing

- **Unit:** record assembly and rendering; cursor dedup across same-second ties; clone-diff deletion detection; public-to-private withholding behavior; doc path-glob filtering.
- **Integration:** DI wiring for the new connector; the nullable-connection factory path (a connection-less source builds a public connector); a full-host-start test proving the `connection_id` migration and wiring (a clean container start alone would not prove the DI path, per prior guidance).
- Tests tagged `Unit` / `Integration` per the repo convention.

## Open items

1. **Public-to-private 404 behavior.** The safety re-check assumes an unauthenticated `GET /repos/{o}/{r}` returns 404 once a repo goes private. Inferred from GitHub's general privacy behavior; confirm during the docs-sync phase.
2. **GraphQL unauthenticated budget.** Edge capture uses GraphQL/timeline calls under the 60/hour unauthenticated cap; confirm this is workable for a medium repo during the record-sync phase, and fall back to deferred edge backfill if not.

Resolved by spike on 2026-09-09: an unauthenticated 304 **does** decrement the 60/hour public rate limit, so ETags do not conserve quota for credential-less public polling; public docs therefore sync by clone.

## Decomposition (to be detailed by the writing-plans step)

Phased, stacked PRs under the repo size limit, an epic issue filed first:

1. Model change: nullable `Source.ConnectionId`, provider-on-source, factory overload, migration.
2. Connector skeleton + docs source via git clone (unauthenticated public).
3. Issues-and-pull-requests source: sync, record assembly, edges as metadata.
4. Record chunker.
5. Admin provider page + add-repository wizard.

## Deferred to a later epic: private repositories, GitHub App, and permission filtering

Not built now, preserved so the design is not lost. When the App arrives:

- **Auth:** a customer registers their own GitHub App via the manifest flow (`POST /app-manifests/{code}/conversions` returns the App id, client id/secret, webhook secret, and PEM private key, stored encrypted; admin pastes nothing). Connapse signs a ≤10-minute RS256 JWT and exchanges it for a one-hour installation token per sync/check. Manual fallback for air-gapped installs. Works on GitHub Enterprise Server (base URL `/api/v3`, `/api/graphql`). Octokit.NET handles installation tokens and a custom base address but not JWT signing; GraphQL is hand-rolled to avoid the beta package.
- **Connection model:** a GitHub App connection; private and internal repos require it; a public source may attach to it for the ≥5,000/hour limit (an installation token reads public repos too).
- **Identity linking:** each user links GitHub via the App's user-to-server OAuth; Connapse stores only the mapping (numeric `id` + current `login`) and discards the token; SAML `externalIdentities` offered as an auto-link convenience.
- **Query-time permission filter (fail closed):** group hits by distinct private-source repo; resolve id→current login; `GET /repos/{o}/{r}` for visibility and app-visibility; decide per the table below; ~5-minute per-(user,repo) cache; public-source hits bypass entirely.

  | Visibility | User link state | Permission-check result | Decision |
  |---|---|---|---|
  | public | any (incl. anonymous) | not consulted | ALLOW |
  | private | unlinked | not consulted | DENY |
  | private | linked | read / write / admin | ALLOW |
  | private | linked | none | DENY |
  | private | linked | 404, App can see repo | DENY |
  | private | linked | 404, App cannot see repo | DENY + flag coverage gap |
  | internal | unlinked | not consulted | DENY |
  | internal | linked, enterprise member | any (incl. none) | ALLOW |
  | internal | linked, not enterprise member | read or higher | ALLOW |
  | internal | linked, not enterprise member | none / 404 | DENY |
  | any | any | visibility call itself fails | DENY |

  The per-repo access check is `GET /repos/{owner}/{repo}/collaborators/{username}/permission`, which aggregates repo, team, org base, and enterprise grants. Internal repos need a separate enterprise-membership check because the endpoint may report `none` for a non-collaborator member. A 404 meaning "the App cannot see the repo" is indeterminate → fail closed + coverage-gap flag.
- **Unverified open item for that epic:** GitHub's docs are internally inconsistent about whether the collaborator-permission endpoint needs only `metadata:read` or actually push access when called with an installation token. Must be verified against a real App installation before the permission resolver is built; if push is required, the read-only App posture changes.
