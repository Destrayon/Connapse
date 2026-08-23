# How should Connapse ingest files from local and remote filesystems?

**Date:** 2026-08-23
**Status:** Reviewed (adversarial pass applied — see *Corrections after review*)
**Built on:** no prior corpus material — the Connapse MCP server was not connected to this session, so no corpus pre-check ran.

## Executive summary

**Connapse should stop trying to read filesystems directly and instead read S3-compatible object storage — but this is real work, not a free win.** The current Filesystem connector requires the server process to see a path on its own disk, which is false in Docker and false again on any hosted deployment. S3-compatible endpoints are the only transport surveyed with all four properties Connapse needs at once: authentication with no stored long-lived secret, a complete paginated listing that is an *authoritative* snapshot (so absence genuinely means deleted), pure outbound HTTPS from a container, and scale (a 1,000-key page cap puts 100k objects at ~100 requests).

**The recommendation rests on those correctness properties, not on implementation cost.** An earlier draft of this report claimed Connapse "already has the connector" and the change "costs almost no new code." That was false, and verifying it is what produced this version: `S3ConnectorConfig` exposes only `BucketName`, `Region`, `Prefix` and `RoleArn` — **no endpoint field** — and `S3Connector.CreateS3Client` constructs `new AmazonS3Client(region)` without ever setting `ServiceURL` or `ForcePathStyle`. **Connapse's S3 connector can only talk to real AWS S3 today.** The LocalStack integration test passes only because `LocalStackFixture` sets a *process-wide* `AWS_ENDPOINT_URL_S3` environment variable, which cannot vary per connection and would redirect every S3 connection in the process. Phase 1 is therefore a genuine feature: per-connection endpoint and path-style support, plus web-identity STS.

**What this does not solve, stated plainly: none of the recommended transports reaches a laptop.** S3, SFTP and Graph all require the server to reach the source. A machine behind NAT with only outbound connectivity — which is the original complaint about Docker isolation, generalised — needs a tunnel, a publicly reachable endpoint, or an agent. Running MinIO in front of a folder does not change the direction of the connection. See *What this recommendation does not solve*.

**The most consequential finding is not about transport.** The maintainer proposed letting editors create sources inside admin-created connections. That must not ship until the allowlists are deny-by-default. Connapse *indexes content and makes it searchable*, so an editor choosing a scope inside somebody else's credential converts "the admin holds a broad credential" into "an editor can grep everything that credential can reach." Snowflake — the closest analogue — makes its allowlist a **required** parameter for exactly this reason.

## Research brief

**Question:** What mechanism(s) should Connapse use to ingest files from filesystems the server cannot directly access — local, networked, and remote — given that an admin owns the trust boundary and no long-lived secrets may be stored?

**Sub-questions:**
1. Server-reachable pull protocols — SFTP/SSH, SMB/CIFS, WebDAV, NFS, S3-compatible.
2. Push-based agents for machines the server can never reach.
3. Existing tools — rclone, Syncthing, cloud drive APIs, purpose-built ingestion tools, .NET abstraction libraries.
4. Trust and delegation — does the connection/source split hold if editors may create sources? Plus safe delete semantics as a cross-cutting concern.

**Out of scope:** S3 and Azure Blob as already-shipped *cloud* connectors; any design requiring stored cloud credentials; Connapse as an OIDC provider; making sources browsable; ingestion pipeline internals. The browser File System Access API was dropped during elicitation because it produces a copy Connapse owns — a container, not a mirror — and therefore answers a different question. It is out of scope rather than rejected on merit.

**Success criteria:** one recommended primary mechanism with a phased path, its security model spelled out, and an explicit rejected-options list with reasons.

## Findings by sub-question

### Sub-question 1 — Server-reachable pull protocols

**Claim: of SFTP, SMB, WebDAV, NFS and S3-compatible, S3-compatible has the best property set for a read-only mirror; NFS is disqualified outright for containerised deployment.**

**S3-compatible endpoints** (MinIO, Ceph, Garage, Backblaze B2, Wasabi) via first-party `AWSSDK.S3`. Change detection is unusually cheap because `ListObjectsV2` returns key, ETag, size and LastModified *in the listing itself* — no per-file stat call. Delete detection is the strongest of the five: a paginated listing that ran to `IsTruncated: false` is an authoritative snapshot, so absence genuinely means deleted. Contrast SFTP or SMB, where a half-failed directory walk is indistinguishable from a mass deletion. **Two caveats the raw comparison hides:** a bucket policy that narrows `ListBucket`, credentials expiring mid-pagination, or versioned buckets with delete markers can all produce a *shorter* listing rather than an error — so "complete listing" must mean "pagination confirmed complete *and* no error at any page," and even then the delete-safety layer below is required. And ETag is not an MD5 for multipart or SSE-KMS objects.

On authentication, MinIO supports `AssumeRoleWithWebIdentity`, exchanging an OIDC JWT for credentials expiring in roughly an hour (primary: MinIO STS docs). **This has an unstated prerequisite worth surfacing: it requires an OIDC issuer, and Connapse is deliberately not one.** The JWT must come from the operator's own identity provider — Keycloak, Entra ID, Auth0 — configured against MinIO. That is an operator prerequisite, not something Connapse supplies. Where no IdP exists, this degrades to a stored access key, which the constraints forbid; the honest fallback is a short-lived key injected as a container secret.

**SFTP** is the viable second. Authentication is the cleanest of any protocol here: an SSH private key mounted as a Docker or Kubernetes secret file, loaded by SSH.NET from a stream, never touching the database. It is fully user-space over outbound TCP/22 — no mounts, no capabilities. The weaknesses are real but tolerable: the protocol has *no* change notification whatsoever, so `SSH_FXP_READDIR` plus mtime/size comparison is the only option, and round-trip cost dominates at scale — 100k files means tens of thousands of round-trips, minutes per scan. Acceptable nightly, painful at a 15-minute poll. Elasticsearch's FSCrawler ssh provider does exactly this and exposes `remove_deleted` (default true), warning that with `index_folders: false` it cannot detect removed folders at all (primary: FSCrawler docs).

**The SSH.NET advisory in Connapse's dependency tree resolves benignly.** `GHSA-q939-rpr3-3284` is CVE-2026-48798, CVSS 7.1 High: a path traversal in `ScpClient.Download()`'s recursive mode where a malicious server returns filenames containing `../`. It affects ≤ 2025.1.0 and is patched in 2026.0.0. It is in the **SCP** client, not SFTP, so an SFTP-only connector never touches the vulnerable code path. (Verified independently against this repository: SSH.NET arrives only transitively via Testcontainers, in the integration test project, and no Connapse code calls SCP.)

**SMB/CIFS** is possible but compromised on the constraint that matters most. SMBLibrary 1.5.7.1 is user-mode — a genuine win, since no kernel mount means no `CAP_SYS_ADMIN` — but it supports **NTLM only**; Kerberos/SPNEGO remains an open issue, not shipped. That forces storing a username and password, directly violating the no-long-lived-secret rule unless scoped to a dedicated read-only service account. It is also LGPL-3.0-or-later, warranting a licence review. SMB2 CHANGE_NOTIFY exists in the protocol and Nextcloud uses it, but Nextcloud's documentation states it "only works reliably on Windows SMB servers due to limitations of linux based SMB servers," and they still fall back to a cron `files:scan`.

**WebDAV wins one dimension outright and is the strongest candidate this report does *not* place in the phased plan.** RFC 6578 `sync-collection` is the only protocol surveyed with a real change feed: the server returns a `DAV:sync-token`, and subsequent REPORTs return only added, changed, and *deleted* member URLs — deletions become explicit events rather than inferred absence, which solves the hardest problem properly. Authentication is good too: bearer tokens or mTLS over HTTPS. **It is excluded on library support, not on protocol merit:** the available .NET clients (`WebDav.Client`, `WebDAVClient`) implement none of `sync-collection`, so the REPORT and token handling would be hand-rolled, and server support is optional so the fallback path (plain PROPFIND, which RFC 6578 §1 says "doesn't scale well") must be built anyway. That is two implementations for one transport. It is a defensible candidate for a later phase and should be reconsidered if a maintained .NET `sync-collection` client appears, or if Nextcloud/ownCloud become a common deployment target.

**NFS should be dropped.** Creating a mount in a non-initial user namespace is disallowed for NFS, requiring `CAP_SYS_ADMIN` or `--privileged` plus host kernel modules (primary: moby#16429, podman#20717). No mature user-space NFS client exists for .NET. It fails the library, authentication, and Docker dimensions simultaneously.

### Sub-question 2 — Push-based agents

**Claim: the architecture is well-understood and convergent, but the distribution cost makes it a second product rather than a feature. This leaves the unreachable-machine case genuinely unsolved — see *What this recommendation does not solve*.**

**Every production agent converges on the same three parts**: a local durable state store, a scanner, and an outbound-only shipper with acknowledged delivery. Filebeat keeps a *registry file* of per-file read offset plus a unique file identifier — explicitly because "the filename and path are not enough to identify a file" — flushes continuously, and resumes from the registry on restart, giving at-least-once and never exactly-once (primary: Elastic docs).

**Dropbox's sync-engine rewrite is the strongest cautionary tale, and its lesson is about the data model, not the transport.** The old engine's failure was that files "lacked a stable identifier that would be preserved across moves," so a move was represented as delete-plus-add; a transient hiccup where "a delete goes through but its corresponding add does not" makes the file vanish everywhere (primary: dropbox.tech). **Stable file IDs rather than paths are the load-bearing decision** for any mirror — and this applies to Connapse's *pull-based* sources just as much as to an agent, which makes it the most transferable finding in this section.

**Enrollment is a solved problem that needs no identity provider.** Elastic Fleet's pattern transfers directly: an admin mints an enrollment token bound to a policy, the agent presents it once, and the server returns a *distinct per-agent* communication API key. Revoking the enrollment token stops new enrollments but already-enrolled agents keep working — so revocation and unenrollment are separate operations, a distinction Tailscale's auth-key docs make just as explicitly. The stolen-laptop answer is therefore: short-lived enrollment token → long-lived per-agent credential → server-side per-agent kill switch. **RFC 8628 device authorization grant is a poor fit** and should be skipped: it explicitly treats devices as public clients that "cannot securely store credentials," and contains no guidance on device credential revocation at all.

**Nobody trusts the filesystem watcher.** All three platform APIs are documented-unreliable: Windows `FileSystemWatcher.InternalBufferSize` defaults to 8 KB and caps at 64 KB, and on overflow "loses track of changes in the directory"; macOS FSEvents coalesces *hierarchically* and can demand a full recursive rescan via `kFSEventStreamEventFlagMustScanSubDirs`; Linux inotify defaults to 8192 watches and fails outright with no fallback when exceeded. Syncthing's resolution is the one to copy — a full rescan hourly *even when the watcher is running*, every minute when it is not. **Treat the watcher as a latency optimisation over an authoritative periodic scan, never as the source of truth.** This finding also applies to the *existing* Filesystem connector, which is watcher-based.

**Distribution cost is the disqualifier.** Native AOT supports the three platforms but each target requires a native toolchain *on that OS*, so a three-platform CI matrix is mandatory; the build image also sets the glibc floor. Signing is a recurring tax: since June 2023 the CA/Browser Forum requires OV code-signing keys on FIPS 140 Level 2 hardware. macOS requires a Developer ID certificate, hardened runtime, notarization and stapling. Auto-update is its own subsystem — Elastic's minimum viable shape is a separate watcher process supervising the new version for ten minutes with automatic rollback. *(One frequently-cited additional cost — that the EV certificate SmartScreen bypass was removed in 2024, leaving no way to avoid cold-start warnings per release — rests on a search summary rather than a Microsoft primary document and should be verified before it informs a decision. The rejection stands on the toolchain and notarization costs without it.)* Note the existing `connapse-cli` distribution route (`dotnet tool install -g`) does not help, since it requires the .NET SDK on the target — fine for developers, wrong for a daemon on a file server.

### Sub-question 3 — Existing tools

**Claim: rclone is MIT rather than MPL, which materially improves its viability; Microsoft Graph is the only cloud-drive API that satisfies the no-stored-credentials rule.**

**rclone's licence was mis-assumed in the brief.** Its `COPYING` is the **MIT License**, not MPL (primary: rclone repo). Bundling the binary or linking `librclone` therefore carries only attribution obligations. Any earlier reasoning that rejected rclone on licence grounds should be revisited.

**`rclone serve s3` is the cheapest breadth play, with a significant caveat.** Pointing Connapse's S3 connector at an rclone S3 endpoint would reach 70+ backends for no new *connector* code — though note it still requires the per-connection endpoint support that Phase 1 adds, since that capability does not exist today. It supports S3v4 auth via `--auth-key` and TLS via `--cert`/`--key`. But the documentation marks it **Experimental**, it is unversioned, and **only mtime is persisted** — other metadata is memory-only. Betting read-only ingestion on an experimental server is the tradeoff to weigh. Credentials need not be stored: rclone accepts configuration entirely via environment variables or inline connection strings, `--config ""` keeps config in memory only, and the azureblob backend supports `env_auth`/`use_msi` for managed identity. rclone has **no change feed** — `sync` is a full listing diff every run.

**Microsoft Graph is the strongest single finding in this sub-question.** `driveItem: delta` returns deleted items with an explicit `deleted` facet and hands back an `@odata.deltaLink` for incremental polling; the documentation states delta is the *only* way to guarantee a complete local representation. Because `Files.Read.All` is an **application** permission, a managed identity's service principal can hold it — client credentials, no secret, no refresh token, **no stored cloud credential**. This is the only cloud-drive candidate satisfying the hard constraint cleanly, and it matches the maintainer's existing Azure managed-identity posture. Caveat: Graph permissions are additive, so granting `Files.Read.All` alongside a narrower `Sites.Selected` defeats the narrower scope.

**Google Drive** has solid delete detection (`changes.list` with `includeRemoved=true`) but a worse credential story — keyless only when running on GCP with an attached service account, otherwise a JSON key file. **Dropbox is not viable under the constraints**: long-lived tokens are deprecated, offline access requires storing a refresh token, and there is no app-only or service-account path.

**Syncthing is the right auth model attached to the wrong problem shape.** Device IDs are certificate fingerprints, so no central identity provider is needed — genuinely aligned with "Connapse is not an OIDC provider" — and `receiveonly` folders exist and behave correctly. But it requires a peer on every source machine, which is an agent by another name, and it is a bidirectional sync engine coerced into read-only ingestion. MPL-2.0, actively maintained. *(Worth noting against the agent rejection: adopting Syncthing is the cheapest way to get agent-shaped reach without building or signing a binary, since the user installs a third-party tool that is already signed and maintained. It remains unrecommended, but it is the strongest fallback if the unreachable-machine case becomes a priority.)*

**The industry answer for the push case is unglamorous: a watched drop folder.** Paperless-ngx consumes from a directory and *moves files out* into its own structure — a drop model, not a mirror, so there is no source-side delete to detect. For the mirror case, FSCrawler's periodic full crawl (`update_rate` default 15m, `remove_deleted` default true) validates list-and-diff as the norm.

**No .NET abstraction library closes the gap.** FluentStorage (MIT, actively maintained) covers S3, Azure Blob/Files/Data Lake, GCP, FTP and SFTP behind one interface — roughly 8–10 backends against rclone's 70+, and no cloud-drive support at all, which is precisely where Connapse's gap is. **No .NET library provides change-feed abstraction; delete detection remains something Connapse must write.**

### Sub-question 4 — Trust, delegation, and safe deletion

**Claim: deny-by-default allowlists are a prerequisite for editor-created sources, not an improvement to schedule afterwards.**

**Snowflake is a near-exact analogue and it made the allowlist mandatory.** `CREATE INTEGRATION` is an account-level privilege held only by ACCOUNTADMIN by default; creating a stage inside one needs only `CREATE STAGE` plus `USAGE` on the integration — a delegable, lower-privileged grant, exactly the shape proposed for Connapse. Crucially `STORAGE_ALLOWED_LOCATIONS` is a **required** parameter — not optional, not empty-means-all — and Snowflake enforces that a stage's URL falls inside it. `STORAGE_BLOCKED_LOCATIONS` exists specifically for the wildcard case (primary: Snowflake docs).

**AWS has a formal name for the risk: `iam:PassRole`, and the confused-deputy problem.** PassRole exists solely to stop a low-privileged principal attaching a high-privileged credential to a resource it controls; AWS guidance is to restrict it to explicit role ARNs and never wildcards. Mapping onto Connapse: **the connection is the passed role, `allowedLocations` is the ARN restriction, and a permissive-when-empty allowlist is `iam:PassRole` on `Resource: "*"`.**

**The dominant attack is exfiltration-into-search, and it is worse than plain read access.** Connapse indexes content and makes it searchable. An editor pointing a source at `/etc`, at a payroll prefix, or at the connection's own credential store converts "the admin holds a broad credential" into "an editor can grep everything that credential can reach." The credential itself never leaks — the *data* does, and search is a lower-friction exfiltration channel than file listing. Three further attacks: an **existence oracle**, since S3 returns 403 versus 404 depending on whether the caller holds `s3:ListBucket`, letting differential error text enumerate buckets the admin's credential can see; **resource exhaustion** via egress charges, embedding spend and index bloat; and **blast-radius laundering**, where an audit trail recording only "connection X read bucket Y" loses which editor chose the scope.

**Counter-evidence from systems that declined to solve this** reinforces the point. Kubernetes documents that anyone who can create a Pod in a namespace can read any Secret in it, and prescribes namespace separation rather than in-boundary delegation. Airflow simply declares DAG authors trusted. HCP Terraform states outright that it cannot prevent a malicious module from exfiltrating sensitive variables. Grafana takes the opposite approach — provisioned datasources are `editable: false`. CVE-2023-40610 shows the escalation shape concretely in Superset.

**Verdict on delegation:** warn-when-empty is tolerable *while sources remain admin-only*, because the admin already holds the credential and no privilege boundary is crossed. The moment an editor can pick the scope, an empty allowlist is unrestricted PassRole. The minimum gate before shipping editor-created sources: allowlist required and non-empty, validated at write time against a canonicalized path, plus a scope preview and per-creator audit.

**On safe deletion, five named safeguards from production systems:**

| Safeguard | System | Specifics |
|---|---|---|
| Absolute/percentage delete cap | rclone `--max-delete` | Fatal error, stops the operation. Explicitly framed as protection against "a wrong or inaccessible sync source." |
| Whole-run abort | Google Cloud Directory Sync | Limits of 0–100% or absolute; on breach **GCDS syncs nothing at all** — deletes and non-deletes alike. |
| On-by-default threshold | Microsoft Entra Connect Sync | "Prevent accidental deletes" on by default at **500 deletes per export cycle**; on trip the export stops *before deleting any object* and mails an admin. |
| Health marker before trusting a listing | Syncthing `.stfolder` | A marker directory must be present; if missing the folder is treated as *unhealthy* rather than empty. Cheapest defence against the catastrophic case. |
| Atomic swap instead of in-place reconcile | Algolia `replaceAllObjects` | Build into a temp index then `moveIndex`; on error the live index is untouched. Cost: record count temporarily doubles. |

**Filebeat's own documentation warns about this exact failure mode**: `clean_removed` "can cause complete file re-reading if shared drives temporarily disconnect," and Elastic advises disabling it in unstable network environments — absence of a file is explicitly treated as untrustworthy evidence.

**Delta APIs shrink the problem but do not remove it.** Microsoft Graph marks deletions with `@removed`, so an empty page means "no changes," never "everything is gone." But delta tokens have **no published TTL** and can be invalidated at any time; the service then returns **HTTP 410 Gone** with `resyncRequired`, forcing full re-enumeration — described in the docs as a normal recovery scenario, not an edge case. The dangerous list-and-diff path therefore still exists; it is merely rarer, and fires on a trigger nobody sees coming.

## Recommendation

**Phase 1 — Per-connection S3 endpoints, with delete safety built in from the start.** Add `ServiceUrl` and `ForcePathStyle` to `S3ConnectorConfig` and thread them through `ConnectorFactory` and the connection form; add `AssumeRoleWithWebIdentity` alongside the existing `AssumeRole`. Replace the LocalStack fixture's process-wide `AWS_ENDPOINT_URL_S3` with per-connection configuration, which is also what makes the test honest. Ship the delete-safety layer in the same phase rather than after it, because this phase already deletes: require pagination confirmed complete with no page error before any reconcile; apply a percentage-or-absolute threshold that **aborts the whole run** GCDS-style rather than partially applying; route deletes to tombstones hidden from search immediately and purged after N days; stamp each successful crawl with a generation counter so "not seen in generation N" is distinguishable from "generation N never completed."

Then document MinIO (or any S3-compatible server) as the supported way to expose a filesystem, and **mark the local-disk Filesystem connector as single-host-only** rather than deleting it — deleting it would strip a working feature from existing single-host users for no gain, and it remains the correct choice when Connapse runs on the same host as the data.

**Phase 2 — SFTP**, with the key mounted as a secret, for genuine remote-disk cases where standing up object storage is not reasonable. Take the SSH.NET 2026.0.0 bump regardless of whether this ships.

**Phase 3 — Microsoft Graph** for OneDrive and SharePoint, using application permissions via managed identity.

**Deferred pending deny-by-default allowlists:** editor-created sources.

**Rejected:** NFS (needs `CAP_SYS_ADMIN`, no .NET client); Dropbox (requires a stored refresh token); a push agent (distribution and signing cost is a second product). **Excluded but defensible, revisit on trigger:** WebDAV (best change feed of any protocol; excluded only on .NET library support); Syncthing (right auth model, wrong shape, but the cheapest route to agent-like reach without shipping a binary); rclone (MIT, viable, gated on accepting an Experimental server on the ingestion path); SMB (gated on LGPL acceptance and a stored NTLM password).

## What this recommendation does not solve

**No transport recommended here reaches a machine the server cannot connect to.** S3, SFTP and Graph are all server-initiated pulls. A laptop or workstation behind NAT, with only outbound connectivity, remains unreachable — and since the question originated in Docker isolation, it is worth being explicit that **running MinIO in front of a folder does not change the direction of the connection.** It solves the *container cannot see the host disk* problem on a single machine; it does not solve *the server is somewhere else entirely*.

For that case the honest options are, in ascending cost: a **tunnel or reverse proxy** exposing an endpoint the server can reach (Tailscale, Cloudflare Tunnel, an SSH reverse tunnel), which moves the problem to network configuration the operator already understands; **Syncthing in `receiveonly` mode** on the server paired with a send-only peer on the machine, which gets agent-shaped reach using a third-party binary the user installs and Connapse never signs; or **building the agent**, whose cost this report argues against but does not argue is unjustifiable if that case becomes the priority.

A fourth non-answer worth naming: **bulk upload**. If the data does not need to stay in sync, uploading a folder into a container is already supported and is not a worse answer merely because it is unglamorous.

## Corrections after review

An adversarial review of the first draft found a load-bearing factual error, since verified directly against this repository and corrected above:

- **The claim that S3-compatible endpoints "already work" was false.** `S3ConnectorConfig` has no endpoint field; `S3Connector.CreateS3Client` never sets `ServiceURL` or `ForcePathStyle`; `AssumeRoleWithWebIdentity` is not implemented (only `AssumeRoleAsync`, which needs base credentials); and the LocalStack test passes only via a process-wide environment variable that cannot vary per connection. The recommendation survived, but its justification changed from *cheapest* to *most correct*, and Phase 1 grew from documentation into a feature.
- **The original tiebreaker was self-serving.** The first draft resolved a disagreement between two investigations by preferring the option "requiring no new connector code" — a criterion that favours the writer, and which rested on the error above. The tiebreak is now made on correctness properties.
- **The unreachable-machine case was silently unanswered.** Sub-question 2 asked about machines the server can never reach; the draft rejected the agent and then never acknowledged that every remaining option requires reachability. *What this recommendation does not solve* was added.
- **Delete safety was mis-phased**, scheduled after a phase that already deletes. It is now inside Phase 1.
- **WebDAV vanished** — praised as solving the hardest problem, then absent from both the plan and the rejected list. It is now explicitly excluded on library support, with a revisit trigger.

## Conflicts and uncertainties

**Two investigations disagreed on the first move, and the disagreement is real rather than one of emphasis.** The protocol investigation concluded "ship S3-compatible first"; the tools investigation concluded "adopt Microsoft Graph directly, use rclone for breadth." Both are defensible. This report resolves it toward S3 because it serves filesystem-shaped data — which is what the question asked about — while Graph serves cloud-drive-shaped data, a different population. That is a judgement call, and the first draft's stated reason for it (implementation cost) turned out to be factually wrong, which is worth weighing when deciding how much confidence to place in the resolution.

**`rclone serve s3` is simultaneously the cheapest breadth option and the least stable.** Reusing one connector for 70+ backends is genuinely attractive; the server is documented as Experimental, unversioned, and persists only mtime. Unresolved.

**S3's "authoritative listing" property is weaker than a one-line comparison suggests.** Narrowed `ListBucket` permissions, mid-pagination credential expiry, and versioned-bucket delete markers can each shorten a listing without raising an error. This is why the delete-safety layer is inside Phase 1 rather than after it.

**`AssumeRoleWithWebIdentity` requires an OIDC issuer that Connapse deliberately is not.** The keyless-auth claim therefore depends on the operator already running an IdP. Where none exists, this degrades to a short-lived key injected as a secret — still better than a stored long-lived key, but not the clean story the summary implies.

**Two source-quality caveats.** No published formal postmortem of a *search index* wiped by an empty-listing reconcile was found; the evidence comes from file-sync and directory-sync systems, so the transfer is sound by analogy but not directly attested. And the Nextcloud/ownCloud incidents are bug reports rather than formal RCAs.

## Gaps — what we did not find

- **SMBLibrary CHANGE_NOTIFY client support could not be confirmed.** The protocol has it; whether this library exposes it is unverified.
- **SMB behaviour at 100k files** — no source found.
- **SSH.NET ssh-agent support** — absence inferred from the library surface, not documented.
- **rclone `mount` Docker/FUSE requirements** were not verified against primary docs.
- **The 2024 removal of the EV SmartScreen bypass** rests on a search summary, not a Microsoft primary document.
- **Delta token TTL for Microsoft Graph is genuinely unpublished**, so resync frequency cannot be planned for — only handled.
- **No formal postmortem of an index-wipe** as noted above.
- **LlamaIndex reader licensing and semantics** were not verified.
- **Cost and latency of tunnelling options** (Tailscale, Cloudflare Tunnel) for the unreachable-machine case were not investigated; that recommendation is directional rather than evidenced.

## Source quality assessment

The synthesis rests predominantly on **primary sources**: protocol specifications and RFCs (RFC 6578, RFC 8628), vendor documentation (AWS IAM, MinIO STS, Microsoft Graph, Snowflake, Elastic, Apple FSEvents, Microsoft Learn), licence files read directly from source repositories, the GitHub Advisory Database, and project issue trackers. The strongest claims — Snowflake's mandatory allowlist, Graph's `@removed` facet and 410 resync behaviour, the platform watcher limits, the Native AOT toolchain constraints — are all primary.

**The claims about Connapse's own code are the highest-confidence in the report**, having been verified by reading the source directly rather than inferred: `S3ConnectorConfig.cs`, `S3Connector.CreateS3Client`, and `LocalStackFixture.InitializeAsync`.

**Secondary sources** carry the confused-deputy framing and some engineering-blog analysis. **The weakest links are listed in Gaps**; none is load-bearing except the SmartScreen claim, and the agent rejection stands without it.

## Sources

**Primary — specifications and standards**
RFC 6578 (WebDAV sync-collection) · RFC 8628 (OAuth Device Authorization Grant) · draft-ietf-secsh-filexfer · CA/Browser Forum code-signing baseline requirements

**Primary — vendor and project documentation**
AWS S3 API and IAM PassRole documentation · MinIO STS `AssumeRoleWithWebIdentity` · Microsoft Graph `driveItem: delta` and delta-query overview · Microsoft Entra Connect Sync deletion threshold · Microsoft Learn `FileSystemWatcher.InternalBufferSize` and Native AOT deployment · Apple FSEvents Programming Guide · Snowflake `CREATE STORAGE INTEGRATION` · Elastic Filebeat, Fleet enrollment tokens, elastic-agent upgrades · Datadog Agent architecture · Syncthing FAQ and folder types · rclone (`COPYING`, `sync`, `serve s3`, `rc`, `azureblob`, librclone README) · restic `design.rst` · Paperless-ngx configuration · FSCrawler local-fs documentation · Google Drive `manage-changes` · Dropbox OAuth guide · Grafana provisioning · Kubernetes Secrets concepts · Airflow security model · HCP Terraform security model · Tailscale auth keys · Apple notarization · Algolia `replaceAllObjects` · Google Cloud Directory Sync deletion limits

**Primary — advisories and trackers**
GHSA-q939-rpr3-3284 / CVE-2026-48798 · CVE-2023-40610 (Superset) · moby#16429 · podman#20717 · nextcloud/desktop#9280, #7831, #8831 · owncloud/client#6322, #2687, #10253 · rclone#959 · SMBLibrary#137 · SSH.NET#100

**Primary — this repository (read directly)**
`src/Connapse.Storage/Connectors/S3ConnectorConfig.cs` · `src/Connapse.Storage/Connectors/S3Connector.cs` · `tests/Connapse.Integration.Tests/LocalStackFixture.cs`

**Primary — repositories and licences**
rclone `COPYING` (MIT) · Syncthing `LICENSE` (MPL-2.0) · SMBLibrary (LGPL-3.0-or-later) · FluentStorage (MIT) · Unstructured (Apache-2.0)

**Secondary — engineering analysis**
Dropbox "Rewriting the heart of our sync engine" · sra.io "An Overview of Deputies in AWS" · Atlassian April 2022 post-incident review · DigiCert 2023 key-storage change notice · kdgregory on S3 403/404 differential responses
