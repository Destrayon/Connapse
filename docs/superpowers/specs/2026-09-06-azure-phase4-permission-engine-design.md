# Azure Phase 4 — Per-object query-time permission engine (Azure search filtering)

**Status:** Design — pending spec review
**Date:** 2026-09-06
**Milestone:** v0.4.0 · Issue #479 · Epic #475
**Parent design:** `docs/superpowers/specs/2026-09-05-azure-blob-provider-design.md` (§C engine, §D
enforcement seam). This phase spec refines those sections into implementable components,
interfaces, and a PR-sized decomposition; where they differ, this document governs Phase 4.

## Goal

Filter Azure Blob search results so a searcher sees only the blobs they may **currently** read,
evaluated **live at query time** at parity with Azure's own authorization, failing closed. No
ingestion-time permission capture, no new stored permission surface. The AWS/S3 path is untouched
except to wrap it in a composite that both clouds share.

This is the epic's last and hardest phase. It consumes the Entra identity link from Phase 3
(`IAzureIdentityLinkReader` → `AzureIdentityRef(oid, tid)`) and the app identity from Phase 2
(`ConnapseAzureCredentials`). When it lands, `epic/azure-blob-provider` merges to `main`.

## Governing principle

Every connector evaluates **per object**, at **parity** with the technology it fronts, and **fails
closed** — never coarser than the object, never an over-grant. See the project invariant
(`per-object-permission-principle`). Enforcement must be both **sound** and **complete**, and the
distinction (settled with the user 2026-09-06) is the spine of this design:

1. **Sound — never shows a file the user can't read.** Absolute. Every returned hit is either
   admitted by an *exact* scope (RBAC container / flat) or passes **live forward-verification** of
   the object's own ACL/tags. Anything uncertain fails closed.
2. **Complete — the permission layer never drops a file the user *can* read.** The only thing that
   decides whether a readable file *surfaces* is relevance ranking, which is out of scope for
   permissions. The mechanism: **an exclusion filter may only ever *over*-approximate the user's
   readable set (exact for RBAC/flat; container-level or none for Gen2 ACL), never *under*.** So no
   approximation ever excludes a readable file — precision comes from live verify, not from
   narrowing retrieval. This dissolves the old "Case C" gap: a read granted on a *file* inside a
   folder the user can traverse but not read as a whole is retrieved by relevance and verified like
   any other hit — it is never excluded by the folder-level approximation.

Corollaries: under-grant (hiding a readable file) is a defect, not an acceptable shortcut — an ABAC
tag grant we can't reduce to a prefix is verified live per hit, never dropped. We **read ACLs/tags
live and never store them** (no ingest-time capture: it can't be kept fresh and would add a
forbidden permission surface — Microsoft's own product takes that route and carries the staleness;
see `azure-permission-trimming-precedent`). The **folder walk is an accelerator, not a gate** — it
lets a hit already known readable skip verification; it never excludes.

## Global constraints

- **Live only.** No ingestion-time permission capture; no new persistence surface. Freshness =
  cache TTL.
- **Fail closed everywhere.** Any missing link, disabled account, fetch error, unparseable
  condition, or deny assignment → deny (for the affected cloud), never `Unrestricted`.
- **Reuse the provider-agnostic enforcement half.** `ISearchScopeResolver` → `SearchScopes` →
  `GrantMatch` → the SQL prefix filter in `HybridSearchService`/`PgVectorStore`/
  `KeywordSearchService` are already generic. Azure contributes a new resolver and a new
  post-retrieval verifier; it does not reinvent enforcement.
- **AWS behavior unchanged.** `AwsSearchScopeResolver` and its S3 path keep their exact current
  behavior; they are wrapped, not modified.
- **App identity is `Storage Blob Data Reader`** — read-only. RBAC supersedes ACLs for Connapse's
  own identity, so it can read any path's ACL data to evaluate the *user's* access. Never elevate
  to Data Owner (rules out the `suoid` probe as baseline).
- **Never trust the token `groups` claim** (Entra drops it past ~200 groups) — resolve groups via
  Graph.
- **Connapse reads only** — never writes a grant, role, or ACL (`connapse-writes-grants`).
- Cert-based Graph/ARM auth via `ConnapseAzureCredentials`; no client secrets.

## Decomposition (each sub-issue is its own plan → SDD cycle, stacked on `epic/azure-blob-provider`)

| # | Sub-issue | Delivers | End state |
|---|---|---|---|
| **4a** | Searcher identity resolution (Graph) | `IAzureDirectoryReader`: one Graph `$batch` → deprovisioning gate + transitive `getMemberGroups` → identity set **P**; cached ~5 min; fail-closed. | Library + tests |
| **4b** | RBAC scope resolver (ARM) + ABAC | `IAzureRbacReader`: `roleAssignments?assignedTo` ∥ `denyAssignments`, `effective = grants − denies`, 3 hard-coded role GUIDs, ABAC path→prefix parse (tag conditions flagged as residue). Cached ~5 min. | Library + tests |
| **4c** | Flat resolver + composite + per-cloud enforcement | `AzureSearchScopeResolver : ISearchScopeResolver` (composes 4a+4b, `azblob://` normalizer); `CompositeSearchScopeResolver` (AWS ∪ Azure, per-cloud fail-closed, per-scheme unrestricted); generalize the enforcement latch beyond SAML. **Flat (non-HNS) accounts filter end-to-end.** | Flat correct e2e |
| **4d** | Gen2 ancestor-traverse resolver + POSIX ACL engine | Pure, unit-testable POSIX first-match evaluator (owner/named/group/other + mask) and a lazily-cached ancestor-traverse resolver (mode bits via `GetPaths`/properties; `GetAccessControl` only for named-entry tie-breaks). No skip-verify from folders, no exclusion. | Library + tests |
| **4e** | Gen2 live per-hit verify seam | New post-retrieval `ISearchResultVerifier`; retrieve Gen2 by relevance over a safe over-approximation, then live-verify **every** non-RBAC hit with 4d — Gen2 file ACL (HNS) and blob-tag (flat ABAC). Over-fetch/backfill until page full; bounded parallelism. Completes soundness **and** completeness. | **epic → main** |

4a/4b are pure fail-closed libraries. 4c makes flat storage correct end-to-end (the common case).
4d is the pure Gen2 evaluation engine; 4e wires it into the pipeline as the live per-hit verifier.
Only an exact RBAC role skips verification — folder access never does — so the epic gates on 4e's
verify being in place before Azure filtering reaches `main`.

## Component design

### A. Searcher identity resolution — Graph (4a)

**`IAzureDirectoryReader`** (Core):
```
Task<AzureIdentitySet> ResolveAsync(AzureIdentityRef link, CancellationToken ct);
```
`AzureIdentitySet` (Core record): `{ bool Enabled, IReadOnlyList<string> PrincipalOids, IdentityOutcome Outcome }`.
`PrincipalOids` = {user oid} ∪ {transitive security-group oids}. `Outcome` ∈
{`Resolved`, `Deprovisioned`, `Failed`}. Failure/deprovisioned → `Enabled=false`, empty oids.

**`GraphDirectoryReader`** (Storage) — one Graph `$batch` (≤20 subrequests) on
`ConnapseAzureCredentials`, scope `https://graph.microsoft.com/.default`:
- **Deprovisioning gate:** `GET /users/{oid}?$select=id,accountEnabled` — 404 or
  `accountEnabled==false` → `Deprovisioned`, deny everything.
- **Groups:** `POST /users/{oid}/getMemberGroups {securityEnabledOnly:true}` — one transitive
  call, GUIDs only.
- Cache the resolved set ~5 min keyed by oid (`IMemoryCache`, key `azure-identity:{oid}`); cache
  only confident answers (`Resolved`/`Deprovisioned`), never `Failed`. Mirror
  `AwsSearchScopeResolver`'s caching discipline.

### B. RBAC scope resolver — ARM + ABAC (4b)

**`IAzureRbacReader`** (Core):
```
Task<AzureRbacScopes> ResolveAsync(string primaryOid, CancellationToken ct);
```
`AzureRbacScopes` (Core record): `{ IReadOnlyList<AzureScope> ReadablePrefixes,
IReadOnlyList<AzureTagCondition> TagConditioned, RbacOutcome Outcome }`.

**`ArmRbacReader`** (Storage) — one ARM call plus a parallel deny call, api-version `2022-04-01`,
on `ConnapseAzureCredentials` (scope `https://management.azure.com/.default`), at the
**subscription scope** (`AzureProviderSettings.SubscriptionId`, added this phase; absent → `Failed`):
- `GET /subscriptions/{sub}/providers/Microsoft.Authorization/roleAssignments?api-version=2022-04-01&$filter=assignedTo('{oid}')`
  — **without** `atScope()`. Corrects the parent design: the docs state `atScope()` lists "only the
  specified scope, **not including the role assignments at subscopes**", so it would miss
  account- and container-scoped grants (the common case). A plain subscription-scope list includes
  assignments at the scope, its ancestors (inherited), **and** its descendants (RG / account /
  container). `assignedTo` is **transitive over the user's groups**, so this single call returns
  every role assignment reaching the user (no per-group fan-out).
- In parallel, `.../denyAssignments?...&$filter=assignedTo('{oid}')`. **effective = grants − denies**
  (deny wins; a missing/failed deny call → `Failed`, not "assume none" — ignoring a deny fails open).
  Conservative & sound: any deny assignment whose scope covers (is equal to or an ancestor of) a
  grant's scope and whose (data)actions include the blob read action removes/trims that grant; when
  a deny's applicability is uncertain, drop the overlapping grant (under-grant, never over-grant).
- Match each assignment's `roleDefinitionId` (last GUID segment) against three hard-coded built-in
  GUIDs: Reader `2a2b9908-6ea1-4ae2-8e65-a410df84e7d1`, Contributor
  `ba92f5b4-2d11-453d-a403-e96b0029c9fe`, Owner `b7e6dc6d-f1e8-4753-8033-0f276bb0955b` (only these
  grant blob **data** read).
- Translate each surviving assignment's `scope` to an `azblob://` prefix:
  `.../storageAccounts/{acct}/blobServices/default/containers/{c}` → `azblob://{acct}/{c}/`;
  `.../storageAccounts/{acct}` → `azblob://{acct}/`; a broader scope (RG / subscription /
  management group) covers every account under it → `azblob://` (matches all accounts). Then apply
  its ABAC `condition`:
  - **path condition** (`@Resource[...blobs:path] StringLike/StringStartsWith '<p>'`) → narrow to a
    prefix (`azblob://{acct}/{c}/<p>`), stripping a trailing `*`.
  - **container-name condition** (`@Resource[...containers:name] StringEquals '<c>'`) → narrow the
    account/broader scope to that container.
  - **blob-index-tag condition** (`@Resource[...blobs/tags:<key>...] ...`) → cannot reduce to a
    prefix; emit an `AzureTagCondition` (scope + the tag predicate) for **live per-hit
    verification** (§E), never excluded.
  - **unparseable condition** → drop that one assignment (fail closed for that grant; the rest still
    count).
- Cache ~5 min keyed by oid (`azure-rbac:{oid}`), confident answers only.

### C. Flat resolver + composite + per-cloud enforcement (4c)

**`AzureSearchScopeResolver : ISearchScopeResolver`** (Storage): given `userId`, resolve the link
(`IAzureIdentityLinkReader`), the identity set (4a), and the RBAC scopes (4b); for a **flat
account** the RBAC prefix set *is* the answer ("read one blob in container C ⇒ read all of C").
Normalize each `azblob://` scope to a `GrantMatch` (exact for a whole-container/blob grant,
prefix otherwise) and return `SearchScopes.Of(matches)`. Per-user cache (`azure-scopes:{userId}`,
~60 s, confident answers only), fail-closed throughout — mirror `AwsSearchScopeResolver` exactly.
Tag-conditioned grants contribute their broad scope as a retrieval over-approximation and are
verified live per hit (§E), never excluded.

**`CompositeSearchScopeResolver : ISearchScopeResolver`** (Search or Core): registered as *the*
`ISearchScopeResolver`; `AwsSearchScopeResolver` and `AzureSearchScopeResolver` become its inner
resolvers. Combine rule (per-cloud fail-closed, per-scheme):
- Each cloud governs its own URI scheme (`s3://`, `azblob://`); non-cloud docs (`resource_uri
  IS NULL`) are always visible.
- A cloud whose identity provider is **configured** (enforcing) contributes its per-user scopes,
  fail-closed empty on error/no-link — so its scheme's docs are hidden unless a scope admits them.
- A cloud whose provider is **not configured** (not enforcing) leaves its scheme **unrestricted**
  (that content is ungoverned in this deployment) — matching how AWS already behaves when SAML is
  unconfigured.
- Global `Unrestricted` only when **no** cloud is enforcing (single-user/dev mode).
- One cloud's `Failed`/error never denies the other cloud's legitimately-granted docs, and one
  cloud's `Unrestricted` never leaks to the other cloud's scheme.

This requires making the scope→SQL plumbing **per-scheme aware**: the composite yields, per
scheme, either "unconstrained" or "matches one of this scheme's prefixes." `PgVectorStore` and
`KeywordSearchService` filter builders gain a per-scheme predicate; the existing AWS-only shape is
the single-scheme case of it. `PermissionEnforcementSettings`/`EnforcementMigration` (today gated
on `SamlSignInSettings.IsConfigured`) generalize to "enforcing if *any* cloud provider
configured," with an Azure enforcement latch mirroring `SamlEnforcementLatch`. **This is the
trickiest integration point; its exact predicate table and combine matrix are finalized in the 4c
plan with tests.**

### D. Gen2 ancestor-traverse resolver (verify support) (4d)

Reading a Gen2 file requires traverse-`X` on **every** ancestor directory **and** read-`R` on the
**file's own** ACL. ADLS ACLs are **copy-at-create, not live-inherited**, so a file's own ACL can be
tighter (or looser) than its directory chain — which means **folder access never proves file
access**, in either direction. Two consequences fix the model:
- **No skip-verify from folders.** Only an *exact RBAC role* (which supersedes ACLs entirely) lets a
  hit skip per-object verification. A readable folder does **not** — a locked-down file beneath it
  must still be dropped, so every Gen2 hit not covered by RBAC is verified in §E.
- **No exclude from folders.** An approximation never excludes (governing principle #2), so the
  folder structure is not a retrieval filter either.

4d therefore provides only the **ancestor-traverse decisions** that §E's per-file verify needs:
given a candidate file, resolve whether P holds `X` on each ancestor directory. Resolve **lazily
per candidate** and cache by directory (mode bits from a targeted `GetPaths`/properties read;
`GetAccessControl` on a directory only when a named entry could be decisive), so candidates sharing
ancestors share the work. An optional bulk pre-warm — one recursive directories-only
`GetPaths(recursive:true)` (owner/group/mode octal bits, 5 000/page) — can populate the ancestor
cache up front for accounts where it pays off, but it is a performance option, never required for
correctness. Cache traverse decisions ~30–120 s.

### E. Gen2 live per-hit verify seam (4e)

For HNS accounts, Gen2 content is admitted by **relevance over a safe over-approximation** and
decided by **live forward-verification** — never excluded by the folder-walk approximation
(governing principle #2). This is what makes enforcement *complete*: the only thing that limits
which readable files appear is relevance, never the permission layer.

**Retrieval.** Gen2 candidates are retrieved by relevance, filtered only by an over-approximation
that can never drop a readable file: exact RBAC/flat scopes where they apply, otherwise the
container(s) the user has any foothold in (or unfiltered across HNS containers). The folder
structure is never used to narrow retrieval.

**Verify seam.** Add **`ISearchResultVerifier`** (Core): given the retrieved hits plus the resolved
Azure context (exact RBAC scopes, tag conditions, identity set P, and the 4d ancestor-traverse
resolver), return the subset that passes live per-object verification. Register a **no-op default**
(AWS-only
deployments). `HybridSearchService` calls it after retrieval, **over-fetching and backfilling**
until the page is full or candidates are exhausted (so a batch full of non-readable hits never ends
the results early), with **bounded parallelism** (`SemaphoreSlim`/`Parallel.ForEachAsync`, degree
~16–64) and SDK exponential-backoff retries honoring `503`/`Retry-After`.

Routing per `azblob://` hit:
- Covered by an **exact RBAC scope** → **pass**, no per-hit call (RBAC supersedes ACLs).
- **HNS**, otherwise → **verify the file's own ACL:** self-compute the documented POSIX first-match
  on the file's **own** access ACL against P (owner bypasses mask; named user/group capped by mask;
  any one group granting suffices) **and** confirm P holds traverse-`X` on every ancestor (4d,
  cached). Mode-bit short-circuit first; `GetAccessControl` only when a named entry could be
  decisive. Fail → drop. This one check both surfaces a bespoke per-file grant ("Case C") *and* drops
  a locked-down file beneath a readable folder — folder access is never trusted.
- **Flat**, under a **tag condition** → **tag verify:** evaluate the blob's index tags against the
  condition (`GetBlobTags` per hit, or bulk `FindBlobsByTags` for the query's candidates). Fail → drop.
- AWS/`s3://` and non-cloud hits → pass untouched (never enter the Azure verifier).

Fail → drop and backfill. Cache per-file ACL/tag decisions for the query's lifetime; short
cross-query TTL optional. A deployment that wants to trade cost for even tighter retrieval can opt a
Gen2 account into pure retrieve-then-verify (no over-approximation narrowing at all) — completeness
is identical, only the candidate volume differs.

## Interfaces & seams

| Interface | Layer | New/Reused | Role |
|---|---|---|---|
| `ISearchScopeResolver`, `SearchScopes`, `GrantMatch`, `ScopeResolution.Guard` | Core | Reused | Provider-agnostic scope model (retrieval filter) |
| `IAzureIdentityLinkReader` → `AzureIdentityRef` | Core | Reused (Phase 3) | userId → oid/tid |
| `IAzureDirectoryReader` → `AzureIdentitySet` | Core | New (4a) | oid → identity set P + deprovision gate |
| `IAzureRbacReader` → `AzureRbacScopes` | Core | New (4b) | P → readable prefixes + tag residue |
| `AzureSearchScopeResolver` | Storage | New (4c) | Azure `ISearchScopeResolver` |
| `CompositeSearchScopeResolver` | Search/Core | New (4c) | AWS ∪ Azure, per-cloud/scheme |
| `ISearchResultVerifier` (+ no-op default) | Core | New (4e) | Post-retrieval live per-hit verify |
| Gen2 POSIX ACL engine + ancestor-traverse resolver | Storage | New (4d) | Per-file read/traverse decision |
| Gen2 file-ACL verifier, tag verifier | Storage | New (4e) | Per-hit parity (ACL + tags) |
| `PermissionEnforcementSettings` / `EnforcementMigration` | Core | Reused, generalized (4c) | Per-cloud enforcement latch |

## Caching & throttling

User→identity-set (~5 min), effective RBAC scopes (~5 min), directory-ACL/traverse (~30–120 s),
per-user composite scopes (~60 s, = revocation delay). Warm-cache per-query control-plane cost ≈ 0;
cold ≈ 2–4 calls. Only confident answers cached; failures/denials never cached. Data-plane ACL/tag
reads on a miss use bounded parallelism well under the 20 000 req/s account ceiling; watch
hot-partition risk on deeply-nested shared directories.

## Fail-closed matrix (cloned/extended from `AwsSearchScopeResolverTests`)

Deny (Azure scheme, never `Unrestricted`) on: no link; Graph 404 / `accountEnabled==false`;
Graph/ARM throws or times out; deny assignment covering a scope; unparseable ABAC condition;
enforcement configured-but-unusable. Distinguish **no-grants** (empty, valid) from **failure**
(fail closed) — both hide Azure docs but only one is cacheable. Never trust the token `groups`
claim. Per-cloud isolation: an Azure failure must not hide AWS-granted or non-cloud docs.

## Testing strategy

- **4a/4b/4c** — unit fail-closed matrices with faked Graph/ARM readers (mirror
  `AwsSearchScopeResolverTests`); composite combine matrix (per-cloud, per-scheme) as unit tests;
  flat end-to-end SQL filtering as an integration test mirroring `SearchScopeEnforcementTests`.
- **4d** — POSIX first-match matrix (owner bypasses mask; named user/group capped by mask; any one
  group suffices; "other"); ancestor-traverse resolution (lose `X` on the chain → unreadable),
  named-entry tie-break fallback, cache.
- **4e** — verifier drop/backfill; **completeness** (a bespoke per-file "Case C" grant beneath an
  unreadable folder is retrieved and returned) and **soundness** (a locked-down file beneath a
  readable folder is dropped); tag-verify; and that exact-RBAC and AWS/flat hits skip verification.
- Azurite integration where it can exercise the data plane; control-plane (Graph/ARM) covered by
  faked readers, as Azurite cannot authenticate AAD tokens.

## Resolved open questions (from the parent spec)

- **Deny assignments** — resolve `denyAssignments` in parallel always; `effective = grants − denies`.
  A missing deny call fails closed (ignoring a deny fails open). Not "assume none."
- **ABAC blob-index-tag grants** — evaluated **live per hit** (`GetBlobTags`/`FindBlobsByTags`),
  never excluded (under-grant), never captured at ingest.
- **Case C recall (Gen2)** — **dissolved.** Because the folder-walk approximation is only an
  accelerator and never an exclusion filter (§D/§E, governing principle #2), a bespoke per-file
  grant is retrieved by relevance and live-verified like any other hit — the permission layer never
  drops it. Not solved by any stored/ingest-time capture (no reverse Azure API exists; a stored
  booster can't be kept fresh and would add a forbidden permission surface). See
  `azure-permission-trimming-precedent`.
- **`suoid` probe** — non-goal (needs Data Owner; group behavior undocumented).
- **Composite semantics** — per-cloud fail-closed, per-scheme unrestricted (§C).
- **Cache TTLs** — group-set/RBAC ~5 min, directory-ACL ~30–120 s, composite ~60 s; tunable.
- **`checkAccess`** — not relied upon.
- **`user_cloud_identities` table** — resolved in Phase 3 (new `UserAzureIdentityLinkEntity`).

## Non-goals

- ABAC arbitrary-expression parsing beyond the canonical path/tag templates (unparseable → fail
  closed).
- The `suoid` server-side probe as baseline.
- Ingestion-time permission capture / any new stored permission surface.
- Any write to customer Azure authorization (grants/roles/ACLs).
- Touching the AWS provider's mechanism (it already satisfies the invariant; it is only wrapped).
