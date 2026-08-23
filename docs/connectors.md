# Connections, Sources, and Containers

> Part of [Connapse](https://github.com/Destrayon/Connapse) — open-source AI knowledge management platform.

Connapse keeps two kinds of storage apart, because they answer to different owners.

| | **Container** | **Source** |
|---|---|---|
| Whose data | Connapse's own | Somebody else's |
| Browsable | Yes — full file tree | No |
| Writable | Yes — upload, delete, create folders | Never |
| Searchable | Yes | Yes |
| Created by | Any editor | Administrators only |
| Backed by | Managed storage | A **connection** to S3, Azure Blob, or a filesystem |

A **connection** is the third piece: an administrator registers one credential and endpoint, and any number of sources point at scopes within it. One connection to an AWS account, many sources naming different buckets.

This split replaced a single `Container` type carrying a `connectorType` column. If you are looking for that column, or for `ContainerWriteGuard`, or for per-container upload permission flags, they were removed in [#348](https://github.com/Destrayon/Connapse/issues/348) — see [Why the split](#why-the-split) at the end.

---

## Containers

A container is Connapse's own storage, provided through an `IManagedStorageProvider` abstraction — MinIO by default, overridable per deployment. There is nothing to configure per container; the backing store is configured globally under **Settings > Storage**.

```http
POST /api/containers
{ "name": "my-knowledge-base", "description": "optional" }
```

That is the whole request. A container has no connector to choose, because managed storage is what a container *is*.

Deleting a container deletes its stored objects, so it must be empty first.

---

## Connections

A connection holds **how to authenticate** and **what the credential is allowed to reach**. It never holds a bucket name — that is the source's job.

Connections are created and edited in **Settings > Connections**, by administrators, in the UI only. There is no REST route for them, and that is deliberate: see [Why connections are UI-only](#why-connections-are-ui-only).

### No stored cloud credentials

There is no secret field on a connection form, because Connapse does not accept pasted cloud keys.

- **S3** authenticates through the AWS default credential chain — an instance profile, an IRSA role, or an SSO session. `roleArn` optionally names a role to assume on top of that.
- **Azure Blob** authenticates through `DefaultAzureCredential` — a managed identity, a workload identity, or a developer sign-in. `managedIdentityClientId` optionally selects a specific user-assigned identity.
- **Filesystem** has no credential at all; it runs as whatever account the server runs as.

The consequence worth internalising: **rotating credentials is an operation you perform in AWS or Azure, and Connapse needs no involvement.** There is nothing stored here to rotate.

### Configuration by provider

**S3**

```json
{
  "region": "us-east-1",
  "roleArn": "arn:aws:iam::123456789012:role/ConnapseReader",
  "allowedLocations": ["company-knowledge", "shared-docs/public/"]
}
```

**Azure Blob**

```json
{
  "storageAccountName": "companydata",
  "managedIdentityClientId": "00000000-0000-0000-0000-000000000000",
  "allowedLocations": ["knowledge", "archive/2026/"]
}
```

**Filesystem**

```json
{
  "allowedRoot": "/srv/knowledge"
}
```

### The two allowlists

`allowedLocations` and `allowedRoot` bound what any source using the connection may be pointed at. They exist because **IAM cannot make this distinction on its own**: every source sharing a connection presents the same principal to AWS or Azure, so as far as the cloud provider is concerned they are indistinguishable. The allowlist is the only place that difference can be expressed.

A location may name a whole container (`company-knowledge`) or a container and prefix (`shared-docs/public/`). A prefix entry permits only sources at or below it.

**Currently permissive when empty.** A connection declaring no allowlist warns once per scope and is accepted, because [#350](https://github.com/Destrayon/Connapse/issues/350) backfilled existing containers into connections and none of them declare one. This becomes deny-by-default before connections are ever creatable programmatically. A connection declaring entries that are *all blank* is denied, not treated as unconfigured.

Deployments can bound `allowedRoot` further with `Sources:Security:AllowedFilesystemRoots`. When configured, the Connections tab renders the root as a dropdown rather than a free-text field.

```json
{
  "Sources": {
    "Security": {
      "AllowedFilesystemRoots": ["/srv/knowledge", "/mnt/shared"]
    }
  }
}
```

The two checks are independent and both are needed: an allowlist does not stop a symlink, and link resolution does not stop `allowedRoot: "/"`.

### Filesystem confinement

A filesystem source's `subPath` is resolved beneath its connection's `allowedRoot`, and the result is verified to still be inside it — resolving links on **every path segment**, not just the leaf.

This is deliberate and was a real vulnerability ([#365](https://github.com/Destrayon/Connapse/issues/365)). `Path.GetFullPath` is purely lexical: it never touches the filesystem, so a junction or symlink sitting *inside* the allowed root passed the old prefix check and the connector then walked straight through it to the target. On Windows, creating a junction needs no elevation.

Two limits an operator has to handle outside Connapse, because no path check can reach them:

- **Bind mounts and hard links** are invisible to link resolution — the resolved path genuinely *is* inside the root.
- **Time-of-check/time-of-use.** A path verified as inside the root can be replaced before it is opened; the fix needs an anchored open with per-platform interop, which .NET has no cross-platform primitive for.

Run the server under a low-privilege account, and keep configuration and the DataProtection key ring outside every configured root.

---

## Sources

A source names **what to index** inside a connection.

```http
POST /api/sources
{
  "name": "company-docs",
  "connectionId": "…",
  "scopeJson": "{\"bucketName\":\"company-knowledge\",\"prefix\":\"docs/\"}"
}
```

Scope keys by provider:

| Provider | Keys |
|---|---|
| S3 | `bucketName`, `prefix` |
| Azure Blob | `containerName`, `prefix` |
| Filesystem | `subPath`, `includePatterns`, `excludePatterns` |

### API

| Method | Route | Role |
|---|---|---|
| GET | `/api/sources` | Viewer |
| GET | `/api/sources/{id}` | Viewer |
| POST | `/api/sources` | **Admin** |
| PATCH | `/api/sources/{id}` | **Admin** |
| DELETE | `/api/sources/{id}` | **Admin** |
| POST | `/api/sources/{id}/sync` | **Admin** |

Reads are viewer-level; every mutation is administrator-only, because a source chooses what external data gets indexed.

**The response never contains the scope.** `scopeJson` names buckets, prefixes, and filesystem subpaths, and `syncCursor` is an opaque provider continuation token. Returning either would turn a read route into reconnaissance. `lastSyncError` is administrator-only for the same reason — a provider's failure text routinely echoes what failed, as in `Access Denied for bucket payroll-data`.

Deleting a source removes its indexed documents. The external data is untouched.

### Sync

`SourceSyncService` polls every enabled source and reconciles it against its remote. It replaced `ConnectorWatcherService`, which enumerated *containers* — after external storage moved into `sources`, that service matched nothing and syncing had silently stopped.

- **Default interval** 5 minutes, overridable per source with `syncIntervalSeconds`.
- **Change detection** compares the remote's last-modified time and size, stored on the document so a restart does not re-ingest everything.
- **One cycle at a time per source.** A source already syncing is skipped rather than queued, so a slow remote cannot stack cycles.
- **Cursors advance by compare-and-swap**, so a cycle that raced with a configuration change cannot overwrite the newer state.

`POST /api/sources/{id}/sync` runs one cycle immediately instead of waiting for the next poll.

Containers have their own `POST /api/containers/{id}/sync`, which reconciles managed storage against the document table. It exists because objects can land in the bucket out of band; it does not talk to any external system.

### Deletions are guarded

A sync reconciles by absence: anything indexed but missing from the remote listing is treated as deleted. That inference is only as good as the listing — and a listing can come back empty *and successful*, from a narrowed bucket policy returning `200 OK` with no keys, or a directory that is temporarily unmounted.

So a reconcile that would delete more than **both 10 documents and 10% of what the source has indexed** applies its additions and **withholds the deletions**, recording how many. The Sources page shows the count to administrators with an "Apply deletions" button.

Two details worth knowing:

- **Additions still apply.** A source that trips the guard keeps ingesting, because a safety check that stops a source working is an outage.
- **Approving re-runs the sync**, it does not replay the earlier list. If the remote recovered in the meantime, nothing is deleted.
- **Approving is a ceiling, not a licence.** It authorises up to the number you were shown. If the remote degraded further between reading the count and approving it, the larger set is withheld again and needs fresh approval — so a worsening outage cannot have the whole index applied on the strength of a smaller approval.

The threshold is fixed and not configurable. Small sources are never blocked — losing five of five files applies immediately, since re-ingesting them is cheap.

The guard bounds a single reconcile, not the source's history. A listing that persistently returns just under the threshold — say 9% missing every cycle — never trips it, and the index erodes a little on every sync.

---

## Why the split

Three problems, all of them presentation problems:

1. **Rendering an S3 bucket as a browsable folder tree implied ownership Connapse does not have.** A file tree with an upload button says "this is yours."
2. **The tree exposed every synced object to any Connapse user**, regardless of what they could see in the source system — a far wider surface than search results.
3. **Synced buckets competed with real containers in one list**, so "where does this file live?" had no consistent answer.

Write capability is now a **type guarantee** rather than a runtime check: only the managed-storage connector implements `IWritableConnector`, so a source is *incapable* of being written to. `ContainerWriteGuard` and the per-container permission flags were deleted because there is no longer a runtime decision for them to make.

## Why connections are UI-only

Sources have admin-scoped REST routes so infrastructure-as-code still works. Connections do not, and the asymmetry is intentional.

A connection is the credential boundary. Creating one programmatically means an API call can introduce a new principal for Connapse to authenticate as — and today the allowlists that would bound it are permissive when empty. Until they are deny-by-default, an unconstrained root is only reachable by an administrator sitting in the admin UI, which is a materially smaller surface than a token.

A source, by contrast, can only ever name a scope *within* a connection an administrator already approved.

---

## Related

- [Architecture](architecture.md) — where these types live in the layer graph
- [API reference](api.md) — full request and response shapes
- [MCP tools](mcp-tools.md) — how sources and containers appear to agents
