# Design: delete guard and SFTP ingestion

**Date:** 2026-08-23
**Status:** Approved for planning
**Research:** `docs/research/filesystem-ingestion-2026-08-23.md`

## Problem

Two problems, one of which is live today.

**The delete guard.** `SourceSyncService.SyncViaListAndDiffAsync` computes the set of indexed paths absent from a remote listing and deletes every one of them, with no check that the listing was trustworthy:

```csharp
var remotePaths = remote.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
var vanished = indexedPaths.Where(p => !remotePaths.Contains(p)).ToList();
int deleted = await DeleteByPathsAsync(source, vanished, context, sp, ct);
```

If a listing returns empty but successful, every document for that source is deleted. S3 propagates exceptions mid-pagination, which helps, but a narrowed bucket policy returning `200 OK` with zero keys does not throw — and neither does a filesystem directory that is temporarily unmounted or empty. This affects the S3, Azure and Filesystem sources that already ship.

**Filesystem ingestion is unreachable in the deployments people actually use.** The Filesystem connector requires the server process to see a path on its own disk. In Docker the container filesystem is not the user's; on a hosted deployment there is no shared disk. Bind-mounting host paths is both a security concern and poor UX.

## Decisions taken during brainstorming

| Decision | Choice | Reason |
|---|---|---|
| Unreachable machines | Out of scope — operator's problem, documented not coded | Deliberate: Connapse should not hand-hold network reachability |
| Transport | SFTP | Most common shape of "files on a server somewhere" |
| SSH key storage | Encrypted in the database via DataProtection | The mechanism already exists; the no-stored-**cloud**-credentials rule is about cloud IAM, not an SSH key for your own box |
| Host key | Trust on first use, pinned thereafter | Catches the realistic attack — interposition later — without demanding a fingerprint before you can start |
| Delete safety | Threshold abort only. No tombstones, no generation counter | For a mirror, a wrong deletion is recoverable by re-sync; what needs preventing is the catastrophic case, and a false positive costs re-embedding rather than data |
| Threshold | Fixed: withhold when vanished exceeds **both** 10 documents and 10% of the source's index | A percentage alone fires constantly on small sources; an absolute alone is meaningless on large ones |
| Override | Per-source admin button, one shot. Not configurable | Deliberate YAGNI: a config ceiling gets loosened for one event and never tightened again |

## Scope

**In:** the delete guard on the list-and-diff path; an SFTP connection provider and connector; SFTP-specific path confinement; the connection form gaining a credential field for SFTP only; documentation for reaching a local machine over SFTP.

**Out:** tombstones; a generation counter; a configurable threshold; any push/agent mechanism; per-connection S3 endpoints (deferred — see *Deferred work*); credential-source determinism (issue #383, sequenced after this).

**Two issues and at least two PRs, in order.** Part 1 is independently valuable and independently reviewable, and must merge before Part 2 begins — SFTP is list-and-diff, so it is exactly the transport where an unguarded reconcile does the most damage. Part 2 will likely split further under the repo's 300-line convention: the connector and confinement in one PR, the UI and connection-form changes in another.

## Part 1 — The delete guard

Ships first and alone. It fixes shipped behaviour, and SFTP would otherwise be the first transport built on an unsafe foundation.

### The rule

```csharp
internal static bool ShouldWithholdDeletions(int vanished, int indexed) =>
    vanished > 10 && vanished > indexed / 10;
```

Both terms are load-bearing. A 5-document source losing all 5 is not blocked — small sources are cheap to re-ingest and should be allowed to clean themselves up. A 100,000-document source losing 5,000 is not blocked either; losing 50,000 is.

### Where it applies

**The list-and-diff path only.** Delta deletions are *reported* by the provider rather than inferred from absence, so the failure mode the guard exists for does not occur there. More practically, withholding on the delta path would require not advancing the cursor — otherwise those deletions are lost permanently, since a provider will not report the same delta twice — which livelocks the source into reprocessing the same batch every cycle.

The delta path also has no production implementer today, and its 410-resync fallback lands in list-and-diff, which *is* guarded. The risk is covered without the complication.

### State

`SyncStatus` is **unchanged** at `Never/Running/Succeeded/Failed`. A sync that upserted 50 documents and declined to delete 400 genuinely succeeded; an enum cannot express both, and overloading the status is how "withheld" gets treated as failure and stops the source syncing entirely — the outage the guard exists to prevent.

Instead:
- `Source.WithheldDeletions` — `int?`, null when nothing is pending. **Requires a migration** adding `sources.withheld_deletions` (nullable integer).
- `SourceSyncResult.WithheldDeletions` — `int`.
- `SourceResponse.WithheldDeletions` — visible to any viewer. It is a count, not a path, so unlike `LastSyncError` it leaks nothing about the remote and does not need the admin-only treatment.

**Only the count is stored, never the paths.** The override re-runs the sync and recomputes the vanished set rather than replaying a stored list: if the mount returned in the meantime, the recomputed set is empty and nothing is deleted, where a stored list would delete files that are present again.

**Corrected after adversarial review — recomputing is not *strictly* safer, and the count is a ceiling.** An earlier draft of this spec claimed recomputing was safer than replaying, full stop. It is safer when the remote **recovered** and more dangerous when it **degraded further**: an administrator shown 40 could have 1,000 applied if the listing worsened between reading the number and pressing the button. So the stored count binds the approval — a recomputed set larger than what was approved is withheld again, and the flag is inert when nothing was withheld, so it cannot lift a guard that never tripped.

Bound by count rather than by path identity, deliberately. The administrator never sees which paths — the page shows a number and a source is never browsable — so the consent being given is "delete what is missing, about this many". Hashing the path set would invalidate approval on ordinary churn and would mean storing path-derived data this design keeps out of the database.

### Behaviour when the guard trips

Upserts **still apply**. Only deletions are held. A source that trips the guard must keep ingesting new content, or the safety mechanism becomes an outage.

This is a deliberate departure from Google Cloud Directory Sync, which aborts the entire run. Connapse is a mirror for search rather than a directory sync where a partial apply corrupts state, so applying additions while withholding deletions leaves the index strictly more correct than skipping both.

### Override

A flag on the existing admin-only `POST /api/sources/{id}/sync`, and a button on the Sources page shown only when `WithheldDeletions` is non-null and the viewer is an admin. Audited under its own action name so "an admin approved 400 deletions" is distinguishable from an ordinary sync.

### Testing

- Unit tests on `ShouldWithholdDeletions` at boundaries: 10/100, 11/100, 5,000/100,000, 50,000/100,000, 5/5, 0/0.
- **The regression test that matters:** an integration test seeding a source with documents, making its listing return empty, and asserting the documents are still present and `WithheldDeletions` reports the count. This must fail against current `main`.
- An integration test that the override applies the deletions.
- An integration test that upserts still apply on a cycle where deletions are withheld.

## Part 2 — SFTP

### Shape

`ConnectionProvider.Sftp = 5`.

**Connection `ConfigJson`:** `host`, `port` (default 22), `username`, `allowedRoot`, `hostKeyFingerprint` (populated on first connect).
**Connection `Secret`:** the private key, encrypted by the existing DataProtection machinery.
**Source `ScopeJson`:** `subPath`, `includePatterns`, `excludePatterns` — **identical to the Filesystem provider**, so `SourceForm`, `SourceScopePreflight` and `SourceScopeSummary` gain a provider case rather than a new branch of logic.

### The passphrase

An encrypted private key needs a passphrase, which is a second secret, and there is one `Secret` field. Store both as a small JSON object inside the existing encrypted blob: one protected value, one decrypt, no schema change.

### Host key verification

**SSH.NET raises `HostKeyReceived` with `CanTrust` defaulting to true** — not handling it is the insecure default, and a mistake here is silent. This must be handled explicitly.

First successful connect records the fingerprint on the connection. Every later connect compares and refuses on mismatch, surfacing as a sync failure naming both the expected and presented fingerprints. The recorded fingerprint is shown on the connection so it can be checked against `ssh-keyscan`. Clearing it re-arms trust-on-first-use, which is how a legitimate server rekey is handled.

### Path confinement — a new implementation, not a reuse

**`PathConfinement` cannot be reused.** It calls `Directory.Exists`, `File.Exists` and `FileSystemInfo.ResolveLinkTarget` — local filesystem I/O. Against a remote path those either return false or resolve something on the *server running Connapse*, silently degrading confinement to a lexical prefix check. That is precisely the bug #365 fixed for local paths, and it would be reintroduced remotely.

SFTP confinement uses the protocol's own `SSH_FXP_REALPATH` via `SftpClient.GetCanonicalPath`, so symlinks resolve **server-side** before the prefix comparison. Same shape as `PathConfinement`, different mechanism, its own tests.

### Listing completeness

The connector must distinguish "I walked everything" from "I walked what I could." A directory it cannot read must **fail the listing**, not silently return fewer files — a quietly-short listing is indistinguishable from a mass deletion to the reconcile, and the delete guard only bounds the damage rather than preventing it. No swallowing per-directory errors during the walk.

### Deliberately not built

- **No tree-walking connection test.** The test button opens a session, verifies the host key, and stats the root. Nothing more.
- **`SupportsLiveWatch = false`.** SFTP has no change notification. List-and-diff on the normal poll, and at 100k files that is minutes per scan. Documented as a known limit.

### The credential field returns, for SFTP only

[#371](https://github.com/Destrayon/Connapse/pull/371) removed the secret field from the connection form deliberately. It comes back **only when the selected provider is SFTP**. S3 and Azure must continue to have no secret field at all, or the epic's no-stored-cloud-credentials position is quietly reopened. The form is provider-driven the same way the source scope fields are.

### Testing

A Testcontainers SFTP server (`atmoz/sftp`) gives end-to-end coverage the way LocalStack does for S3:

- list and read against a real server;
- confinement refusal on a `subPath` containing `../`;
- confinement refusal on a **server-side symlink** pointing outside the root — the case a lexical check would pass;
- host-key pinning: accept on first connect, refuse after the server's key changes;
- a source whose remote directory becomes unreadable keeps its documents (ties Parts 1 and 2 together).

## Using SFTP to index a local machine

This is the supported answer to "Connapse in Docker cannot see my files," and it is documentation rather than code.

Enable an SSH server on the host — OpenSSH Server is an optional Windows feature, Remote Login on macOS, sshd on Linux — add a public key to `authorized_keys`, and point an SFTP connection at it. From a Docker Desktop container the host is `host.docker.internal:22`.

Three things the documentation must state, because each costs an hour otherwise:

1. **Windows OpenSSH SFTP presents paths as `/C:/Users/...`** — a leading slash before the drive letter. This affects how `allowedRoot` is written.
2. **`host.docker.internal` is Docker Desktop only.** A Linux host needs `--add-host=host.docker.internal:host-gateway`; a remote deployment needs the machine's real address, which returns to the reachability question that is the operator's to answer.
3. **It is list-and-diff on every poll.** Point it at a folder, not a whole drive.

The existing local Filesystem connector is unchanged and remains correct for same-host deployments.

## Deferred work

- **Per-connection S3 endpoints** (MinIO, Ceph, Garage). The S3 connector currently has no endpoint field and can only reach real AWS; the LocalStack test passes only via a process-wide `AWS_ENDPOINT_URL_S3`. Best delete-detection properties of any transport, and worth doing — just not chosen first.
- **Credential-source determinism** — issue #383.
- **Editor-created sources** — blocked on deny-by-default allowlists. With an empty allowlist, an editor choosing a scope inside an admin's credential is `iam:PassRole` on `Resource: "*"`, and because Connapse indexes content the result is a query interface over whatever that credential can reach.
- **Tombstones and a generation counter** — add only if re-ingestion cost proves painful. The generation counter can be added on top of the threshold without redoing it.
- **WebDAV** — the only surveyed protocol with a real change feed (RFC 6578 `sync-collection`). Excluded on .NET library support, not on merit. Revisit if a maintained client appears.

## Success criteria

1. A source whose remote listing collapses to empty keeps its documents, reports the withheld count, and continues to ingest new content.
2. An admin can approve the withheld deletions from the Sources page in one action.
3. An admin can add an SFTP connection and a source inside it entirely from the UI, and index files from a machine the server can reach — including their own, over `host.docker.internal`.
4. A `subPath` that escapes its `allowedRoot`, whether lexically or through a server-side symlink, is refused.
5. A changed host key stops the sync rather than being accepted silently.
