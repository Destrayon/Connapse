# Azure Blob Storage provider — per-object permissions, mirroring the AWS design

**Status:** Design — drafted in brainstorming, pending spec review
**Date:** 2026-09-05
**Milestone:** v0.4.0

## Problem & goal

Connapse has an unverified, half-built Azure Blob Storage integration (connector,
connection tester, an `AzureIdentityProvider` on the uncalled legacy scope path, an
Azure-AD OAuth "connect" flow). None of it is proven and it does **not** deliver
per-user search-permission filtering. We are **removing it entirely** and rebuilding
Azure as a faithful mirror of the AWS provider work (epic #436) — adapted to Azure's
fundamentally different authorization model.

Three hard requirements from the product owner:

1. **User identity link** on the integrations page: an Entra (Azure AD) login that
   proves the signed-in Connapse user *is* a specific Azure principal. The link must be
   **permanent (never expires)** and **user-revocable**.
2. **Connapse's own application credential** must be able to read a user's permissions
   **per object** in Azure Blob Storage.
3. Connapse's app credential must **auto-inherit** the ambient identity when running in
   an Azure compute context, and be **explicitly configured** when not.

Plus the standing AWS-provider invariants this must preserve: Connapse **only reads**
permissions the admin authored in the cloud (never writes grants), and **fails closed**
on any uncertainty.

## The governing principle (applies to every connector, not just Azure)

> **Universal Per-Object Permission Invariant.** For every source — present and future —
> Connapse evaluates access at the granularity of a **single object**. The underlying
> technology's native permission granularity is only an *input* to that evaluation, never
> a substitute for it. When a technology can only express coarser grants (S3 bucket-level,
> Azure flat-blob container-level), those grants **fan out** to every object they cover, so
> the evaluation unit is always the object. Connapse's per-object decision must reach
> **parity** with what the source technology itself would decide for that object — never
> broader (no over-grant) — and when Connapse cannot determine that decision with
> confidence, it **denies** (fail closed).

This is a guarantee about the *result*, not the mechanism. AWS realizes it by testing each
document's `ResourceUri` against the searcher's grant prefixes at query time; Azure realizes it
by resolving the searcher's RBAC scopes and live-evaluating each candidate's ACL at query time
(§C). Both are per-object; neither is ever coarser than the object in effect. No future design
may ship a filter whose finest granularity is the bucket/container and call it done.

## Why Azure is not a mechanical copy of AWS

The AWS design leans on one thing Azure does not have: a clean **"what may this grantee
read?"** lookup (S3 Access Grants' `ListAccessGrants`). AWS resolves *user → allowed URI
prefixes* cheaply at query time with a 60-second cache.

Azure has **no equivalent API**. To learn a user's per-object blob access, Connapse's own
identity must gather and combine three separate authorization sources itself:

- **Azure RBAC role assignments** (read via ARM `Microsoft.Authorization/roleAssignments`) —
  finest scope is a **container**; cannot express anything below it on its own.
- **Azure ABAC conditions** riding on a role assignment — *can* encode a path prefix
  (`blobs:path StringStartsWith 'foo/'`), read free off the assignment, but arbitrary
  condition expressions are only reliably parseable for a canonical hand-authored template.
- **ADLS Gen2 POSIX ACLs** — the only source of true **per-file** permissions, available
  **only on hierarchical-namespace (HNS) storage accounts**, and with **no** "paths this
  user can read" call: Connapse must walk the directory tree and read each path's ACL, then
  compute effective access (named-user / named-group entries, the mask, and traverse-`X` on
  every parent directory).

All three are **grant-only / union** semantics — none of them can *deny* — so an unparseable
ABAC condition or an unreadable ACL can be safely treated as "no additional grant" and failed
closed without risking a false allow. **Azure deny assignments are the exception and a separate,
higher-priority input:** a deny assignment overrides any grant, so the effective decision is
`grants − denies` (see §C step 2). An implementation that reads only the three grant sources
and ignores deny assignments would authorize access Azure itself denies — the resolver MUST
subtract denies before granting.

Two ways to enforce this were considered. Microsoft's own first-party analog — Azure AI
Search's "ingest ADLS Gen2 permission metadata" feature — captures each document's allowed
principals **at ingest time** and filters at query time; it scales and preserves recall, but
goes **stale** the moment an ACL or role assignment changes (Azure emits no ACL-change event to
invalidate on), which breaks the "Connapse and Azure don't diverge" goal. We instead enforce
**live at query time** (§C): the source of truth is Azure at the moment of search, so permission
edits take effect on the next search. The cost is query-time latency and a recall limit for
narrow ACL-only access — the deliberate trade for freshness, detailed in §C.

## What we remove first (teardown)

Full manifest lives in the brainstorming research; summary of the blast radius:

- **Delete entirely (~11 files):** `AzureBlobConnector`, `AzureBlobConnectorConfig`,
  `AzureBlobConnectionTester`, `AzureAdConnectionTester`, `AzureIdentityProvider`,
  `AzureAdSettings`, `AzureAdSettingsTab.razor`, `docs/azure-identity-setup.md`, and the
  Azure integration tests (`AzureBlobConnectorIntegrationTests`, `TestableAzureBlobConnector`,
  `AzuriteFixture`, `AzureIdentityProviderTests`).
- **Edit, don't delete (~38 files):** prune the `AzureBlob`/`Azure` members from the shared
  enums (`ConnectorType`, `ConnectionProvider`, `CloudProvider`), the `ConnectorFactory`
  switch arm, `ResourceUri.ForAzureBlob`, the generically-named-but-Azure-only
  `CloudIdentityService` OAuth methods / `UserCloudIdentityEntity` / `PostgresCloudIdentityStore`
  / `ICloudIdentityProvider` scaffolding, `CloudIdentityEndpoints` Azure routes, the four UI
  pages (`Connections`, `Sources`, `Providers`, `ProfileIntegrations`), the two form models,
  `ProviderSetupReader`, DI wiring, `DatabaseSettingsProvider` category map, and config files.
- **NuGet:** remove `Azure.Storage.Blobs`, `Azure.Identity` (Storage), `Testcontainers.Azurite`
  (Integration.Tests). The rebuild re-adds a superset. **Do not** touch `Azure.AI.OpenAI` or
  the `Microsoft.IdentityModel.*` / JWT packages.
- **Migration:** one new down-migration dropping `user_cloud_identities` (+ snapshot regen),
  *unless* the rebuild reuses that table shape (decided in the identity-link phase).

**Trap:** Azure *OpenAI* / AI Foundry providers share the word "Azure" but are unrelated
LLM/embedding integrations — they stay.

## Component design

### A. Connapse's Azure application identity — `ConnapseAzureCredentials`

Mirror of [`ConnapseAwsCredentials`](../../../src/Connapse.Storage/CloudScope/ConnapseAwsCredentials.cs)
(`ProviderKey = "azure"`). Exposes an Azure `TokenCredential` consumed by every Azure SDK
client (Blob, ARM authorization, Graph). **No shared cross-cloud credential interface** — the
AWS and Azure credentials are each their SDK's native base type with no polymorphic caller;
the genuine shared seam is the credential *store* below.

Resolution order (explicit `ChainedTokenCredential`, **not** `DefaultAzureCredential` — MS
best-practice warns DAC's silent fall-through is a fail-open drift):

1. **Stored, configured credential** (present when not in Azure, or when the admin overrides):
   from `IProviderCredentialStore` / `ProviderCredentialEntity`, DataProtection-decrypted.
   Kinds, preferred first:
   - **Certificate** — `ClientCertificateCredential` (service principal + X.509; private key
     encrypted at rest, mirroring the AWS stored private material). *Default portable option.*
   - **Federated** — `ClientAssertionCredential` (workload identity federation; no stored
     secret). *Advanced tier.*
   - **Secret** — `ClientSecretCredential`. *Discouraged last resort, flagged in UI.*
2. **Ambient managed identity** — `ManagedIdentityCredential` (system-assigned, or
   user-assigned via configured client id). This is requirement #3's zero-config happy path.
3. **Fail closed** — if neither yields a token, deny. The chain has exactly these two
   deterministic entries; no developer-tool credentials in production.

Token refresh is handled by the Azure SDK clients (they honor `AccessToken.RefreshOn`), so —
unlike the AWS design — **no 5-minute refresh timer is needed** for client calls. Register as
a singleton alongside the AWS credential. Reuses `ProviderCredentialEntity` with Azure-shaped
columns (kind, tenantId, clientId, optional user-assigned MI clientId, encrypted material).

**Roles Connapse's identity needs** (documented for the admin; Connapse consumes, never grants):
`Reader` or a custom `Microsoft.Authorization/roleAssignments/read` (+ `roleDefinitions/read`)
for RBAC data; `Storage Blob Data Reader` for blob data **and** ACL reads (confirmed
sufficient — Data Owner is only needed to *write* ACLs); Graph app permissions
`User.Read.All` + `GroupMember.Read.All` (admin-consented).

### B. Entra user identity link (integrations page)

Mirror of the AWS SAML → `AwsIdentityLinkStore` link, but using **OIDC** (Entra is
OIDC-native; SAML would force every customer tenant through gallery publishing).

- **Sign-in:** OIDC auth-code + PKCE via `Microsoft.Identity.Web` (`AddMicrosoftIdentityWebApp`).
  A one-time proof of identity — we do **not** request `offline_access`, keep no token, and
  need no token cache.
- **Permanent key:** capture and store **`oid` (object id) + `tid` (tenant id)** only
  (`ClaimsPrincipal.GetObjectId()` / `.GetTenantId()`). Both are documented immutable and
  non-reusable; two GUIDs never expire. Never key on `sub` (per-app, not portable to Graph),
  `email`/`upn`/`preferred_username` (mutable), or `uid`/`utid` (home-tenant ids a resource-
  tenant Graph call can't resolve — matters for B2B guests). Store the pair as `{tid}:{oid}`.
- **Revocation:** app-side = delete the row (nothing exists in Entra to revoke). Entra-side
  deprovisioning is detected **at query time** (§C), not stored.
- **Storage:** an Azure identity-link table mirroring
  [`UserAwsIdentityLinkEntity`](../../../src/Connapse.Identity/Data/Entities/UserAwsIdentityLinkEntity.cs)
  (Connapse user id, oid, tid, display name, timestamps) with a link service + reader
  interface, rather than resurrecting the generic `UserCloudIdentityEntity`. Decide during
  implementation whether to rename/repurpose the dropped `user_cloud_identities` table.

### C. Per-object permission engine — query-time live evaluation (the core)

**Decision:** permissions are evaluated **live at query time**, never captured at ingest. The
source of truth is always Azure at the moment of search, so an edit to an ACL or role
assignment is reflected on the **next search** (within a short cache TTL) — Connapse and Azure
do not drift. There is **no stored per-document permission surface** and no permission
re-crawl (a real simplification vs. the ingestion-time model).

The whole design hinges on one research result: **with warm caches, steady-state per-query cost
to Azure control-plane APIs is ~0**, and the naive "read every blob's ACL and all its ancestors
on every search" is avoided at every layer. The per-query pipeline:

**Step 1 — resolve the searcher's identity (cached ~5 min, keyed by oid).** In one Graph
`$batch` (≤20 subrequests): (a) the **deprovisioning gate** `GET /users/{oid}?$select=id,accountEnabled`
— 404 or `accountEnabled==false` → deny everything, fail closed; (b) transitive **security-group
oids** via `POST /users/{oid}/getMemberGroups {securityEnabledOnly:true}` (one call, transitive,
returns GUIDs only, up to 11 000). Identity set **P** = {oid} ∪ {group oids}. We must resolve
groups via Graph, never the token's `groups` claim — Entra drops that claim past ~200 groups.

**Step 2 — resolve the searcher's effective RBAC scopes (cached ~5 min).** One ARM call:
`GET .../subscriptions/{sub}/providers/Microsoft.Authorization/roleAssignments?$filter=atScope() and assignedTo('{oid}')&api-version=2022-04-01`.
The `assignedTo` filter is **transitive over the user's groups**, so this single call returns
every Storage-Blob-Data role assignment reaching the user — **no per-group ARM fan-out**. Match
each assignment's role-definition against the three hard-coded built-in GUIDs (Reader
`2a2b9908…`, Contributor `ba92f5b4…`, Owner `b7e6dc6d…`) — no role-definition lookups. In
parallel, one **deny-assignment** call with the same filter (`.../denyAssignments`): compute
**effective = grants − denies**, because **deny wins over grant** in Azure. Translate each
surviving assignment to its `azblob://account/container[/prefix]` scope, applying any ABAC
`condition` (2022-04-01 returns it): path/container condition → narrow to a prefix predicate;
**blob-index-tag** condition (flat-account-only, per-blob) → cannot short-circuit, so index blob
tags in our store or exclude that grant; unparseable condition → drop it (fail closed). Result:
the searcher's **RBAC-readable scope set** (prefixes) + any tag-conditioned residue.

**Step 3 — flat (non-HNS) accounts are fully solved by Step 2.** No ACLs exist; RBAC is the only
per-identity model and the container is its finest scope. "Can read one blob in container C ⇒ can
read every blob in C." The RBAC scope set *is* the answer — push it into SQL as prefix predicates
(§D stage 1), zero per-document work.

**Step 4 — HNS / ADLS Gen2 accounts add the ACL layer, in two phases:**

- **Phase A — readable-subtree prefixes for recall (cheap, cached).** Walk **directories only**
  (orders of magnitude fewer than files), top-down from the container root, descending a child
  *only while* P holds traverse-`X` on the chain (traverse is lost → stop, everything below is
  unreachable). Mark each directory where P has `R-X` as a **readable-prefix root**. Drive the
  walk from a single recursive `GetPaths(recursive, directories)` — it returns owner/group/mode
  **octal bits** in bulk (5 000/page), enough to decide the owner/owning-group/other positions
  without a per-directory call; fall back to `GetAccessControl` on a directory **only** when a
  **named-user/named-group** entry could change the answer for P. Emit the readable-prefix roots
  and push them into SQL (§D stage 1) alongside the RBAC prefixes — this is what preserves recall
  without a per-file walk. Cache directory ACLs and traverse decisions (short TTL).
- **Phase B — per-hit verification for exact parity (bounded).** ACL inheritance in ADLS is
  **copy-at-create, not live**, so a file's own access ACL can diverge from its directory chain.
  For the small set of hits actually returned, verify each file's **own** access ACL for the
  read bit against P (self-compute the documented POSIX first-match algorithm: owner bypasses
  mask; named user/group capped by mask; any one group granting suffices), reusing the cached
  ancestor-traverse decision from Phase A. Mode-bit short-circuit first; call `GetAccessControl`
  on the file **only** when a named entry could be decisive. This catches a locked-down file
  (Phase A would have let it through) → **no over-grant**.

**App role for this stays `Storage Blob Data Reader`** — RBAC supersedes ACLs for Connapse's own
identity, so it can read any path's ACL data to evaluate against the *user's* P. (An alternative
"`suoid` user-delegation-SAS probe" would push POSIX evaluation server-side into Azure, one
request per hit — but it requires elevating Connapse to **Storage Blob Data Owner**
(`runAsSuperUser`), which grants write/delete and violates our least-privilege/read-only stance,
**and** whether it evaluates group membership is undocumented. Kept as a flagged optional fast-path,
not the baseline — see open questions.)

**Caching & throttling.** Cache layers with short TTLs (freshness = TTL): user→group-set (~5 min),
effective RBAC scope set (~5 min), directory-ACL decisions (~30–120 s), readable-subtree prefixes
(~short). Warm-cache per-query control-plane cost ≈ 0; cold ≈ 2–4 calls. Data-plane ACL reads on a
Gen2 miss use **bounded parallelism** (a `SemaphoreSlim`/`Parallel.ForEachAsync` degree ~16–64),
SDK exponential-backoff retries honoring `503`/`500`/`Retry-After`, well under the 20 000 req/s
account ceiling; watch hot-partition risk on deeply-nested shared directories.

**Recall caveat (Case C).** Phase A's directory walk cannot see a file whose *own* ACL is **more**
permissive than its directory chain (a bespoke per-file grant) — such a file is readable but never
retrieved. This is inherent (no per-user "paths I can read" API in Azure). It **cannot occur** when
permissions are managed on directories with files inheriting — Microsoft's own recommended pattern
— so we document that as the supported convention and treat broader-per-file grants as a bounded,
known recall limit to validate against real data before GA. Parity (no over-grant) is never at risk
— Phase B verifies every returned hit.

### D. Enforcement seam — two-stage, AWS + Azure coexistence

The existing seam in [`HybridSearchService`](../../../src/Connapse.Search/Hybrid/HybridSearchService.cs)
resolves a single [`ISearchScopeResolver`](../../../src/Connapse.Core/Models/SearchScopes.cs)
into a SQL prefix `LIKE` filter (`SearchScopes.ToLikePattern`) applied in the stores. Azure's
fine-grained ACL parity check cannot be a SQL predicate, so enforcement is **two-stage**, routing
each document by the scheme of its `ResourceUri` (`s3://` vs `azblob://`):

- **Stage 1 — in-store SQL prefix filter (recall-preserving).** AWS keeps its current behavior
  unchanged. Azure contributes its **RBAC-readable prefixes** (step 2) **and** its **Gen2
  readable-subtree prefixes** (step 4 Phase A) as the same kind of prefix `LIKE` filter. A
  composite resolver produces every cloud's prefix set, each guarded by its own cloud's
  fail-closed outcome; the store predicate is "matches an AWS prefix **OR** an Azure prefix." For
  flat accounts this stage is exact and complete.
- **Stage 2 — post-retrieval live verify (Gen2 only).** For `azblob://` candidates on HNS accounts
  that were admitted by a *Phase-A subtree* prefix (not by an exact RBAC scope), run Phase B's
  per-hit ACL verification and drop any that fail. RBAC-admitted and AWS candidates need no stage 2.
  Over-fetch stage 1 to backfill trimmed hits.

So the "composite resolver" is: a scope resolver feeding stage 1 (AWS prefixes + Azure RBAC/subtree
prefixes) **plus** a new Azure Phase-B verification service for stage 2. Both plug into the single
search pipeline; AWS's path is untouched. This is the trickiest integration point and gets its own
design pass in the plan.

### E. Connector / source data plane

Rebuild [`AzureBlobConnector`](../../../src/Connapse.Storage/Connectors/) (`IConnector`,
read-only) on `ConnapseAzureCredentials`, conforming to the connector/source split (epic
#348): connection holds account + credential reference; source holds container + prefix +
patterns. Re-mint `ResourceUri.ForAzureBlob` (`azblob://account/container/path`). New
connection tester on the same credential. Re-add the `AzureBlob` enum values (keeping the
value-matching convention with `ConnectorType`/`ConnectionProvider`). Integration tests
against Azurite (noting Azurite can't authenticate `DefaultAzureCredential` — test the config
recombination path around that limitation, as the S3 path does with MinIO).

### F. Settings, DI, UI, setup guidance

- **Settings:** Azure equivalents of `IdentityCenterSettings` (tenant id, app/client id,
  Graph config) and reuse of `PermissionEnforcementSettings` (the enforcement latch is
  cloud-agnostic). New `DatabaseSettingsProvider` category prefixes.
- **DI:** register `ConnapseAzureCredentials` (singleton), the Azure resolver + the composite,
  Graph/ARM readers, the Azure identity-link store/service, and the rebuilt connector/tester.
- **UI:** a `Providers.razor` Azure setup surface mirroring the AWS step-cards (app identity /
  Entra app registration + admin consent / storage roles), a per-user "connect my Entra
  identity" card on `ProfileIntegrations.razor`, and connection/source forms.
- **Setup generation:** mirror the AWS script/policy generators — emit the admin-consent URL,
  the exact Graph permission list, the RBAC role assignments to create, and (for cert/federated
  credentials) the app-registration steps. Connapse generates copy-paste setup; it never
  mutates the customer's Azure authorization.

## Decomposition (each phase is its own spec → plan → implementation cycle)

1. **Teardown** — remove the existing Azure Blob + Azure AD code (manifest above). Green build,
   AWS untouched, Azure OpenAI untouched.
2. **Azure app identity + data-plane connector** — `ConnapseAzureCredentials`, rebuilt
   connector/tester/`ResourceUri`, enum values, settings, DI, Azurite integration tests.
   Unblocks everything; independently shippable (ingest Azure blobs, no per-user filtering yet).
3. **Entra user identity link** — OIDC sign-in, oid/tid storage, revocation, link store/service,
   integrations-page card.
4. **Per-object permission engine** — the hardest, last, all query-time (no ingestion capture,
   no new persistence surface): searcher identity via one Graph `$batch` (deprovisioning gate +
   `getMemberGroups`, cached); the RBAC scope resolver (one ARM `assignedTo` call, transitive over
   groups, minus deny assignments, hard-coded role GUIDs, cached) with ABAC parsing; the flat
   short-circuit; the Gen2 Phase A directory walk (bulk `GetPaths` mode bits → readable-subtree
   prefixes, directory-ACL cache) and Phase B per-hit POSIX verification; the composite resolver +
   two-stage seam (stage-1 prefix pushdown, stage-2 verify) with bounded-parallel throttled ACL
   reads and over-fetch/backfill; and the fail-closed test matrix cloned from
   `AwsSearchScopeResolverTests`.

## Open questions / flagged uncertainties (resolve before/inside the relevant phase)

- **Deny assignments:** confirm whether the target environment uses deployment stacks / Blueprints
  (the only sources of Azure-managed deny assignments) and whether any deny covers blob *data*
  reads. Baseline resolves `denyAssignments` in parallel and computes `effective = grants − denies`;
  if we instead assume-none, that assumption must be detected-and-alerted, not silently trusted
  (deny-wins, so ignoring one fails **open**).
- **`suoid` probe (optional fast-path):** two blockers before it could replace self-computed Phase
  B — (1) it needs Connapse elevated to **Storage Blob Data Owner** (`runAsSuperUser`), which
  breaks least-privilege/read-only; (2) whether Storage evaluates the probed oid's *transitive
  group* membership is undocumented — **must be tested empirically** (a user with read solely via a
  group ACL entry). Not baseline; revisit only if per-hit self-compute proves too costly.
- **Composite resolver semantics:** exact combine rule when a document is reachable under one
  cloud's grant but the other cloud errors — must stay fail-closed per cloud without one
  cloud's failure denying the other cloud's legitimately-granted documents.
- **Case C recall (Gen2):** validate against real data that the "manage permissions on directories,
  files inherit" convention holds so Phase A's subtree prefixes don't miss bespoke broader-per-file
  grants; if bespoke per-file grants are common, revisit (e.g. an opt-in per-user readable-path
  cache) before GA. Parity is unaffected either way.
- **ABAC blob-index-tag conditions (flat accounts):** grants gated on a per-blob tag can't be
  reduced to a prefix — decide between indexing blob tags in our store (to evaluate the predicate
  in SQL) or excluding tag-conditioned grants (fail closed, under-grant).
- **Query latency budget & cache TTLs:** the group-set/RBAC caches (~5 min) and directory-ACL
  caches (~30–120 s) each trade freshness against per-query cost; measure under load and confirm
  the revocation-propagation delay (= TTL) is acceptable.
- **`checkAccess`:** whether ARM `Microsoft.Authorization/checkAccess` can evaluate a per-blob ABAC
  path condition is undocumented and assessed unlikely — not relied upon; a fallback only.
- **`user_cloud_identities` table** — drop-and-recreate vs repurpose for the Azure link.

## Non-goals

- ABAC arbitrary-expression parsing beyond the canonical prefix template (unparseable → fail
  closed).
- The `suoid` server-side probe as baseline (needs Data Owner + unverified group behavior — kept
  as a flagged optional fast-path only).
- Any write to customer Azure authorization (grants/roles/ACLs). Connapse reads only. (Self-computed
  Phase B honors the full POSIX algorithm — owner/owning-group/named/other + mask — for parity;
  reading ACL data needs only Reader.)
- Touching the AWS provider's mechanism (it already satisfies the per-object invariant).
