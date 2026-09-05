# Orphaned S3 Access Grant cleanup

> **Superseded 2026-09-04 (#463).** Reversed: Connapse no longer creates, tags or deletes access
> grants. In use, the only grant this design could author was the whole configured group on the whole
> bucket, which gave every member every object. Administrators author grants in the S3 console; the
> reader is unchanged. Kept for the record of what was tried and why.

**Status:** Design — brainstorming decisions captured, pending spec review
**Date:** 2026-09-03
**Builds on:** the grant-creation feature (`IAccessGrantsWriter` / `S3AccessGrantsWriter`,
`GrantPlanner`, the "Connapse writes grants now" reversal — see
[2026-09-02-aws-auto-create-access-grants-design.md](2026-09-02-aws-auto-create-access-grants-design.md))
and the per-user permissions feature (S3 Access Grants + Identity Center SAML).

## Problem

Connapse now creates S3 Access Grants that authorize the configured directory group to see a
bucket's documents in per-user search. Nothing removes them. When a bucket is dropped from a
connection's allowed-locations (Connapse stops indexing it), or a connection is deleted, or the
admin changes the grant group, the grant lingers in AWS as **dangling authorization** — pointing at
content Connapse no longer serves, or held by a group nobody configured any more. AWS has no native
expiry or GC for grants; the application must reconcile them itself.

Note the boundary being cleaned: the grant is **not** owned by the connection. Removing a bucket
stops indexing (app-level, via `ConnectorFactory`'s allowlist) — it does not touch Connapse's
account-wide IAM read, and it does not touch the grant. This feature reconciles the *grants*.

## Decisions (from brainstorming)

1. **Delete via a periodic reconcile sweep, plus an explicit admin "Clean up orphaned grants"
   button.** The sweep is self-healing (a missed edit still gets reconciled next tick); the button
   gives immediacy. Rejected: delete-on-connection-edit — it fires once, so any missed event leaves
   a permanent orphan, and it couples an access-control delete to an unrelated save.
2. **Enforce immediately** — the first run deletes, no dry-run phase. Because there is no observation
   window, the safety rails in §7 are mandatory, not optional: provenance-tag gating, a fail-closed
   abort on an incomplete connection picture, a bulk-delete circuit breaker, and loud audit logging.
3. **Include previous-group cleanup** — grants held by a directory group that is no longer the
   configured one are orphaned too (§5).
4. **Leave pre-tagging grants alone.** Grants created before tagging ships — including those the
   create feature has already made — are untagged, so cleanup treats them as admin-authored and
   never deletes them. Documented as needing manual removal if unwanted. No "adopt existing" path.

## Provenance: the mechanism that makes deletion safe

The load-bearing find: **`CreateAccessGrantRequest` accepts `Tags`.** Connapse stamps every grant it
creates, and cleanup deletes **only grants it can prove are its own**. Without this, a grant's scope
and grantee cannot distinguish a Connapse grant from an admin's hand-made one over the same
bucket/group, and auto-delete would be unsafe.

Tag set applied at creation:
- `connapse:managed = true` — the provenance marker; the necessary condition for deletion.
- `connapse:instance = <instance fingerprint>` — which Connapse deployment created it (§6).

**Read-back caveat (AWS):** `ListAccessGrants` returns the grant's id, grantee, scope, and ARN but
**not** its tags. Provenance is confirmed per-candidate via `ListTagsForResource(ARN)` after the
candidate set is already narrowed (right group + orphaned scope), so it is a handful of calls, not a
scan. (The Resource Groups Tagging API is the bulk pre-filter if a deployment ever holds tens of
thousands of grants.) Provenance is only as strong as the tag: an admin who strips it makes the
grant look admin-authored, which fails **safe** — it is skipped, never deleted.

## §1 Tag on create (prerequisite — ship first)

Add the tag set above to the `CreateAccessGrantRequest` built in
`S3AccessGrantsWriter.CreateAsync`. Small, safe, and the enabler for everything below — worth
shipping ahead of the reconciler so grants created in the interim are already GC-able. Requires
`s3:TagResource`-at-create (covered by `s3:CreateAccessGrant` with the `Tags` parameter; confirm the
role can pass tags). The instance fingerprint comes from the same per-instance identity the Roles
Anywhere setup already establishes.

## §2 Reader enhancement — surface the grant id and grantee

Deletion is impossible today: `AccessGrantRecord` carries scope / object-flag / application-ARN /
permission, and `S3AccessGrantsReader` discards the AWS `AccessGrantId` and `Grantee`. Add a fuller
read:

- New record `AccessGrantDetail(string AccessGrantId, string AccessGrantArn, AccessGrantee Grantee,
  string GrantScope, string? Permission, string AccessGrantsLocationId)`.
- New `IAccessGrantsReader.ListAllAsync(string region, ct)` returning `IReadOnlyList<AccessGrantDetail>`
  — every grant in the instance, grantee-blind (the reconciler needs all groups, to catch the
  previous group). Mirrors `ListAllScopesAsync`'s pattern but keeps the id, ARN, and grantee.

`ListForGranteeAsync` / `ListAllScopesAsync` are unchanged (the search path and coverage reporter
still use them).

## §3 Delete path — writer method + policy

- `IAccessGrantsWriter.RevokeAsync(string region, IReadOnlyList<string> grantIds, ct)` →
  `GrantRevokeResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> NotFound,
  IReadOnlyList<GrantWriteFailure> Failed, bool AccessDenied)`. Implemented in `S3AccessGrantsWriter`,
  reusing its account resolution, per-region client construction, and `IsAccessDenied`. `DeleteAccessGrant`
  is **one grant per call** (no batch) and **not idempotent** — a missing id throws `NotFoundException`,
  caught and folded into `NotFound` so a concurrent delete never fails the run.
- Add `s3:DeleteAccessGrant` to `S3SetupPolicy`'s `ConnapseManageGrants` statement (on the same
  `access-grants` resource) and to `AccessGrantScript.RequiredPermissions` if the CloudShell fallback
  ever needs to mirror it. Another write action on the runtime identity — the same knowingly-accepted
  tradeoff as create, updated in `ManagedIdentitySummary`.

## §4 The reconciler — sweep + button

A new `IGrantReconciliationJobs` in `src/Connapse.Background/Jobs/`, following the existing Hangfire
recurring-sweep pattern (`SummaryJobs.SweepStaleContainersAsync`): registered `AddScoped` in
`AddConnapseHangfire`, wired as a recurring job in `Program.cs` alongside the summary sweep,
`[AutomaticRetry(Attempts = 0)]` (a failed tick just waits for the next). The admin button calls the
same reconcile method directly.

**Per-tick algorithm:**
1. Guard: no configured grant group (`!SamlSignInSettings.HasGrantGroup`) → still run, but only
   previous-group cleanup is possible (§5); if there is also no way to know any managed grant is
   wanted, do nothing.
2. Build the **union of allowed-locations across every S3 connection**:
   `IConnectionStore.ListAsync(0, int.MaxValue)` filtered to `Provider == S3`, each parsed with
   `StorageLocationPolicy.ReadAllowedLocations` (the enforcement-grade parser — not the Razor form
   path). **Fail-closed:** if the list read throws, or any connection's config will not parse, the
   union is incomplete → **abort the tick, delete nothing** (an incomplete union makes still-needed
   grants look orphaned). Mirrors `AwsSearchScopeResolver`'s "any part fails → deny the whole thing".
3. For each region in `IAwsGrantRegions.ListAsync`, read all grants (`ListAllAsync`). A region whose
   read fails is skipped for deletion that tick (we simply do not delete what we cannot fully see);
   the union is global, so skipping a region does not risk a wrong delete.
4. A grant is a **deletion candidate** when all hold:
   - its grantee is a directory **group**, and either the **configured** group (orphaned-scope case)
     or a **non-configured** group (previous-group case, §5);
   - its scope overlaps **no** location in the union — reuse `GrantCoverage.Overlaps` /
     `GrantCoverage.Normalise` (the same boundary-aware matcher the coverage reporter uses in the
     create direction), not `GrantScope.Parse`;
   - `ListTagsForResource(ARN)` confirms `connapse:managed = true` **and** the instance tag per §6.
5. **Circuit breaker:** if the candidate set is an implausibly large share of all managed grants in a
   region (e.g. ≥ a configured threshold, default "all of them / more than N"), abort and log an
   alert rather than delete — the signature of a union that came back wrongly empty. This substitutes
   for the dry-run window the "enforce immediately" choice removed.
6. `RevokeAsync` the survivors, region by region. Log every deletion at audit level with the grant id,
   scope, and grantee (sanitised via `LogSanitizer`).

## §5 Previous-group cleanup

When the admin changes `GrantGroupId`, only the current id is stored — the old group's grants can be
found only by reading grantees back from AWS. `ListAllAsync` returns the grantee, so the reconciler
already sees them. A grant held by a **group other than the configured one**, tagged
`connapse:managed` (+ instance, §6), whose scope no connection covers, is a previous-group orphan and
is deleted. A grant held by another group but whose scope **is** still covered is left alone (the
admin may have re-pointed deliberately); only orphaned-scope previous-group grants are removed.

## §6 Multi-instance consideration

A grant is account-wide; two Connapse deployments can share one AWS account and grant group. Instance
A must not delete a grant that instance B's connections still justify — A's union only sees A's
connections. The `connapse:instance` tag scopes this: **each instance deletes only grants tagged with
its own instance fingerprint.** Consequence: a grant instance A created but no longer needs, that
instance B also relies on, is still A's to delete — but B would re-offer/re-create it on its next
coverage check, so the steady state self-corrects. For the common single-instance-per-account case
this is a no-op. Documented limitation: cross-instance shared grants are reconciled per-creator, not
globally.

## §7 Safety invariants (mandatory — no dry-run buffer)

- **Provenance-gated:** never delete a grant without a confirmed `connapse:managed` (+ instance) tag.
  Untagged / differently-tagged / admin-authored → never touched.
- **Fail-closed on incomplete input:** any failure enumerating connections, or any unparseable
  connection config, aborts the whole tick. Deletion requires a *complete* union.
- **Circuit breaker:** refuse a tick whose candidate set exceeds the plausibility threshold.
- **Idempotent-safe deletes:** `NotFoundException` folds into `NotFound`, never fails the run.
- **Group grants only, READ semantics irrelevant to deletion** — but only ever delete DIRECTORY_GROUP
  grants Connapse tagged; never IAM or directory-user grants.
- **Loud audit logging** of every deletion; this is an access-control change and must be traceable.

## §8 Testing

- **Unit — orphan predicate:** extend `GrantCoverage` (or a sibling `Orphaned(grantScopes,
  allowedLocations)`) with cases: scope covered by one of several connections is **not** orphaned;
  boundary case (`logs` vs `logs-archive`); object vs prefix scope; empty union.
- **Unit — candidate selection:** given a set of `AccessGrantDetail` + a union + a configured group,
  the correct grants are selected (configured-group orphan, previous-group orphan, admin grant
  skipped-by-tag, still-covered grant skipped, wrong-instance grant skipped), and the circuit breaker
  trips at threshold.
- **Unit — `S3SetupPolicy`:** `s3:DeleteAccessGrant` present; summary updated.
- **Unit — reader projection:** `ListAllAsync` surfaces id + grantee + ARN + location.
- **Integration:** reconcile against a stubbed S3 Control (list → tags → delete), asserting a
  connection-enumeration failure deletes nothing.
- AWS-touching writer/reader delete paths verified manually/live per codebase precedent (no bUnit /
  no client-boundary unit tests, matching the reader).

## §9 Delivery / build order

Its own epic, staged so each PR is independently useful and testable:

1. **Tag on create** (§1) — smallest, safe, ships first so interim grants are GC-able. Own issue/PR.
2. **Reader detail + delete path + policy** (§2, §3) — the plumbing, no automatic behaviour yet;
   exercised by the admin button.
3. **Reconciler** (§4, §5, §6) — the sweep, the button, the safety rails (§7).

Each PR references its own GitHub issue per the issue-first rule.
