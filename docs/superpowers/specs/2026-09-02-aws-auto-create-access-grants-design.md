# Auto-create S3 Access Grants with the Connapse role

> **Superseded 2026-09-04 (#463).** Reversed: Connapse no longer creates, tags or deletes access
> grants. In use, the only grant this design could author was the whole configured group on the whole
> bucket, which gave every member every object. Administrators author grants in the S3 console; the
> reader is unchanged. Kept for the record of what was tried and why.

**Status:** Design — approved in brainstorming, pending spec review
**Date:** 2026-09-02
**Builds on:** the Roles Anywhere runtime identity (PRs #454–#457) and the
per-user permissions feature (S3 Access Grants + Identity Center SAML sign-in).

## Problem

A connection's buckets are searchable per-user only once a directory group has
an S3 Access Grant covering them. Today Connapse never creates that grant: when
a connection has ungranted buckets, [Connections.razor](../../../src/Connapse.Web/Components/Pages/Connections.razor)
builds a CloudShell script (`GrantCommand`, ~line 1268) from
[`AccessGrantScript`](../../../src/Connapse.Core/Utilities/AccessGrantScript.cs)
and the admin pastes it into AWS CloudShell to run with their own credentials.

That was a deliberate constraint: *Connapse reads grants but never writes them,
because a grant is an access-control decision and Connapse's identity has no
permission to make it.* The constraint is stated in
[`AccessGrantScript`](../../../src/Connapse.Core/Utilities/AccessGrantScript.cs),
[`AccessGrantsSetup`](../../../src/Connapse.Core/Utilities/AccessGrantsSetup.cs),
and [`IAccessGrantsReader`](../../../src/Connapse.Core/Interfaces/IAccessGrantsReader.cs).

Now that Connapse has its own real AWS identity (Roles Anywhere / ambient role),
the constraint is being **reversed by an explicit product decision**: Connapse's
role should create the grants directly, so the admin never leaves the connection
page. The owner's words: "I don't want to be limited by read only, it doesn't
make sense for this project."

## The reversal, stated plainly

This gives Connapse's **standing runtime identity** the authority to create S3
Access Grants. The security consequence, so an admin meets it on the setup page
rather than in IAM:

- Today a compromise of Connapse's role/private key lets an attacker *read* what
  Connapse can read (already structural — a RAG system ingests with no user
  present). It cannot grant a directory group access to a bucket; that authority
  lives with a human admin, so AWS Access Grants + CloudTrail remain an
  authorization record Connapse cannot fabricate.
- After this change, a compromise can also *create* grants — Connapse's identity
  becomes a writer of access-control decisions. This is accepted deliberately.

Consequence for the code: `S3SetupPolicy` is **no longer read-only**, and its
self-description must stop claiming it is (see §2).

## Scope

**In:** automate the per-group grants surfaced on the connector page — the thing
`AccessGrantScript` generates.

**Out:** the one-time infra setup (`AccessGrantsSetup`: the Access Grants
instance, `s3://` location, and location role). That step creates IAM roles and
an Access Grants instance, which would need `iam:CreateRole` / `iam:PutRolePolicy`
on Connapse's *runtime* identity — a far larger, more dangerous authority than
grant creation, for something done exactly once. It stays admin-run via the
existing CloudFormation. The infra is the prerequisite that registers the
`s3://` location grants attach to; the writer discovers that location and fails
with a clear "run the Access Grants setup step first" when it is absent — the
same message the script prints today.

## §1 Grant writer (Core interface + Storage impl)

Mirror the existing read path (`IAccessGrantsReader` → `S3AccessGrantsReader`).

**`IAccessGrantsWriter`** (new, in `Connapse.Core`, beside `IAccessGrantsReader`):

```csharp
/// <summary>The result of creating grants for one grantee against one region.</summary>
public record GrantWriteResult(
    IReadOnlyList<string> Created,       // locations a grant was created for
    IReadOnlyList<string> AlreadyGranted,// locations already covered (skipped)
    IReadOnlyList<GrantWriteFailure> Failed); // locations that could not be granted

public record GrantWriteFailure(string Location, string Reason);

public interface IAccessGrantsWriter
{
    /// <summary>
    /// Creates one READ grant per location for the grantee, against the Access
    /// Grants instance in <paramref name="region"/>. Idempotent: existing grants
    /// are read first and skipped. Never creates WRITE or READWRITE.
    /// </summary>
    Task<GrantWriteResult> GrantReadAsync(
        AccessGrantee grantee, string region,
        IReadOnlyList<string> locations, CancellationToken ct = default);
}
```

**`S3AccessGrantsWriter`** (new, in `Connapse.Storage/CloudScope`), constructed
exactly like `S3AccessGrantsReader` — `ConnapseAwsCredentials` +
`IOptionsMonitor<IdentityCenterSettings>`. Behaviour mirrors the shell logic in
`AccessGrantScript.GenerateScript`, so the two stay semantically identical:

1. Guard: `IsConfigured` false → return an empty result (nothing to do).
2. Resolve account id via STS (reuse the reader's cached-per-process pattern).
3. Find the `s3://` location: `ListAccessGrantsLocationsAsync`, take the entry
   whose `LocationScope == "s3://"`. None → every location fails with
   "No s3:// location is registered — run the Access Grants setup step first."
4. Read existing grants for the grantee once (`ListAccessGrantsAsync` with the
   grantee filter) and collect their `GrantScope`s.
5. For each requested location, normalise to the script's subprefix form:
   `SanitiseLocation(location)` then append `/*` → `bucket/prefix/*`. Compare
   against existing scopes as `s3://bucket/prefix/*`; a match → `AlreadyGranted`.
6. Otherwise `CreateAccessGrantAsync` with
   `AccessGrantsLocationId = <found>`,
   `AccessGrantsLocationConfiguration.S3SubPrefix = "bucket/prefix/*"`,
   `Permission = READ`,
   `Grantee = { GranteeType = DIRECTORY_GROUP|DIRECTORY_USER, GranteeIdentifier = id }`.
   Success → `Created`. A `Conflict`/`already exists` error → `AlreadyGranted`
   (the read-then-create race backstop, same as the script). Any other error →
   `Failed` with the AWS reason; keep going so one bad bucket does not hide the
   rest.

The subprefix form must match the reader/coverage side exactly (`GrantScope`
parsing and `GrantCoverageReporter`), or a grant the writer just created would
still read as ungranted. Reuse `AccessGrantScript.SanitiseLocation` (already
public and already the single source of that shape) rather than re-deriving it.

**DI:** register `IAccessGrantsWriter` → `S3AccessGrantsWriter` in
`Connapse.Storage`'s `ServiceCollectionExtensions`, next to the reader.

## §2 Policy + preflight

Connapse's role must actually be allowed to do this, and an admin must be able to
see when it is not.

- **`S3SetupPolicy`.** Add a statement (or extend `ConnapseReadGrants`) granting
  `s3:CreateAccessGrant` and `s3:ListAccessGrantsLocations` on the same
  `arn:aws:s3:*:<account>:access-grants/*` resource as the existing
  `s3:ListAccessGrants`. `s3:ListAccessGrants` is already present.
- **Stop claiming read-only.** `ManagedIdentitySummary` currently reads
  "…It cannot write, delete, or change anything." That becomes false. Rewrite it
  to state that the identity can create S3 Access Grants (grant directory groups
  read access to buckets) in addition to reading, and adjust the read-only
  framing in the `ForManagedIdentity` doc-comment. This is the surface where the
  admin meets the new authority — it must be honest.
- **Preflight required-actions set** (the design's green/yellow/red probe in
  [`ProviderSetupReader`](../../../src/Connapse.Web/Services/ProviderSetupReader.cs)):
  add `s3:CreateAccessGrant` (and `s3:ListAccessGrantsLocations`) to the declared
  set so a role missing them renders **yellow** with the exact action named,
  rather than the admin discovering it only when a grant button fails. The set is
  data by design ("adding write actions later extends the set without a
  redesign"), so this is the extension it anticipated.

## §3 UI

On the connection (Connections.razor), the current "copy this command" affordance
built from `GrantCommand` becomes a **"Grant access" button**:

- Shown under the same condition as today's command: a grant group is configured
  (`HasGrantGroup`) and the connection has a coverage warning
  (`coverage.HasWarning`), in a region that is not a `RegionMismatch`.
- Click → call `IAccessGrantsWriter.GrantReadAsync(grantee, form.Region,
  coverage.Ungranted, isGroup: true)` with the configured `GrantGroupId`. Use the
  **connection's** region, not the directory's — a grant is created against the
  Access Grants instance in the bucket's region (the same reason the script does).
- On success, re-run the coverage check in place so the warning clears without a
  page reload — reuse `RefreshUngrantedAsync` / `GrantCoverage.CheckAsync`.
  Surface a short result line: created N, already granted M.
- **Graceful degradation.** If the writer returns failures whose reason is
  `AccessDenied` (a bring-your-own narrow role, or the policy not yet updated),
  fall back to showing the existing `AccessGrantScript` CloudShell command as the
  escape hatch — the same try-direct-then-degrade pattern
  [`ProviderResetAction`](../../../src/Connapse.Web/Components/Providers/ProviderResetAction.razor)
  uses for trust-anchor deletion. `AccessGrantScript` is therefore **kept**, no
  longer the default path.
- The region-mismatch guard (`RegionMismatch`) and its message are unchanged: a
  grant that AWS would reject is still not offered, button or script.

## §4 Safety invariants (must hold)

- **READ only.** Never create WRITE or READWRITE, matching
  `AccessGrantRecord.PermitsRead` — a write-only grant read back as readable is a
  disclosure the per-user layer already guards against.
- **Idempotent / re-runnable.** Existing grants are skipped; a second click
  creates nothing and reports what is already there.
- **Fail-closed on partial error.** One bucket's failure does not abort the rest,
  and the UI reports exactly which failed and why — never a silent partial.
- **No new grantee shapes.** Group grants only from the UI (as today); the writer
  accepts `isGroup` so a user grant remains expressible, but the page grants the
  one configured group.
- **Nothing bypasses the region rule.** The writer is only ever called with the
  connection's own region.

## §5 Testing

- **Unit — `S3AccessGrantsWriter`** (mock `AmazonS3ControlClient` at the SDK
  boundary, as the reader's tests do): location discovered vs. missing; existing
  grant skipped; new grant created with the exact `S3SubPrefix`/`READ`/grantee;
  `Conflict` mapped to already-granted; other error collected as a failure while
  the loop continues; empty when `IsConfigured` is false.
- **Unit — `S3SetupPolicy`:** the policy now includes `s3:CreateAccessGrant` and
  `s3:ListAccessGrantsLocations` on the access-grants resource; the summary no
  longer asserts read-only. (Extend the existing `S3SetupPolicyTests`.)
- **Unit — preflight mapping:** a role missing `s3:CreateAccessGrant` maps to
  yellow naming that action.
- **Integration:** grant creation against a stubbed S3 Control (no live AWS in
  CI, matching existing fixtures); the coverage warning clears after a successful
  write.

## §6 Delivery

One focused change — new interface + service + DI, a policy/preflight edit, and
the UI swap — within the project's PR-size limit, so a single PR against its own
issue (issue-first rule). Branch `feature/<issue>-auto-create-access-grants`.
`AccessGrantScript` and its tests are retained as the fallback.
