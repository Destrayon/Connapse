# Phase 4 — Sources tab, Connections settings, Home simplification

Closes #352. Part of epic #348. Follows Phase 3 (#351, merged).

## Why this phase is the one that matters

Phases 1–3 built the machinery: schema, ownership, sync engine, REST surface, MCP contract,
and the security work around all of it. None of it changed what a user sees. All three
problems the epic exists to fix are **presentation** problems:

1. Rendering an S3 bucket as a browsable folder tree implies ownership Connapse does not have.
2. The file tree exposes every synced object to any Connapse user, regardless of what they
   could see in the source system. Browsing is a far wider surface than search results, which
   already pass through `CloudScopeService`.
3. Synced buckets compete with real managed containers in one list.

Until the file browser stops serving external connectors, all three persist exactly as they
did before the epic started.

**Done when:** no external connector is reachable through the file browser.

## Task 4a — Connections settings tab

**Goal:** an administrator can register and test a connection without touching the database.

New `ConnectionsSettingsTab.razor`, added to the `Settings.razor` tab host. It differs from
every existing tab in kind: the others edit one settings record through `IOptionsMonitor` and
`OnSave`, while this one is entity CRUD over `IConnectionStore`. Follow the visual pattern,
not the data pattern.

Per provider: Filesystem takes a root; S3 takes region and an optional role ARN; Azure Blob
takes a storage account and an optional managed-identity client id. All three take an optional
`allowedLocations` list.

Three constraints that are not negotiable, each with a reason recorded elsewhere:

- **`allowedRoot` is selected, not typed.** When `Sources:Security:AllowedFilesystemRoots` is
  configured, render a dropdown of those roots — the API's job is to *select among* configured
  roots, never to invent one. When the allowlist is empty (the one-release grace window),
  fall back to a text field with a visible warning that it will become deny-by-default.
- **No credential field at all.** The plan originally called for a write-only secret box.
  Dropped on review: `GetSecretAsync` has no caller anywhere outside its own store, so no
  connector reads a connection secret, and offering the field would only invite storing the
  cloud credential this project refuses to hold. Existing rows that carry one are surfaced in
  the list as a legacy secret rather than hidden, so an operator can see it is still there.
- **Deletion explains itself.** `IConnectionStore.DeleteAsync` refuses while sources still
  reference the connection. `Connection.SourceCount` is already on the model — say "3 sources
  use this connection" rather than surfacing a raw exception. That count is a snapshot from the
  last load, so it is a better message rather than a substitute for the store's own refusal: a
  source added since then still has to be caught and reported the same way.

Reuse `S3ConnectionTester` and `AzureBlobConnectionTester` unchanged for a Test button — with
one wrinkle the issue did not anticipate. **Both testers require a bucket or blob container**
(`S3ConnectionTester` fails with "Missing BucketName", the Azure one with "Missing
ContainerName"), and that name belongs to a *source*, not a connection. A connection alone
cannot be tested by them as written.

Resolved without touching the testers: the Test button takes a probe target. When the
connection declares `allowedLocations`, the first entry is offered as the default, since it is
by definition a location this connection is meant to reach. Otherwise a small unsaved field
asks for one, labelled per provider — a **bucket** for S3, a **container** for Azure Blob. The
probe target is never persisted; it exists only to give the tester the argument it needs.
Filesystem connections have no Test button at all: there is no remote to reach, and the root is
already constrained by the allowlist.

**Done when:** connections can be created, edited, tested, and deleted from Settings; the
filesystem root is a selection when an allowlist exists; no secret value is ever rendered.

## Task 4b — Sources tab

**Goal:** an operator can see what each source is doing, without seeing what is inside it.

New `Sources.razor` page listing name, connection, a human summary of the scope, last sync
time, status, document count, and the last error. Actions: sync now, edit scope,
enable/disable, delete. Mutations are admin-only, matching `/api/sources`.

**No file listing, and no route that could become one.** A source is a searchable scope;
content is reachable only through search, which already enforces per-user cloud scope. This is
the single constraint the whole phase exists to establish, so it needs a test, not just
intent.

Accepted residual risk, agreed during design: an aggregate document count is still a weak
signal about a source's contents. Judged an acceptable trade for the tab being useful at all.

**Done when:** the tab shows sync state for every source and offers the four actions, while
rendering no document name, no path, and no file listing. The aggregate document count is
permitted — it is the accepted residual risk above, not an oversight.

## Task 4c — Home simplification and search facet

**Goal:** creating a container means managed storage, and the file browser serves only that.

- The create-container modal drops the connector picker, the S3/Azure/Filesystem config
  fields, and its Test Connection button. `ContainersEndpoints` already rejects every
  non-managed `ConnectorType`, so the form is offering choices the API refuses.
- `FileBrowser.razor` serves managed containers only, so its read-only branching goes.
- **Remove the Filesystem `AllowUpload` / `AllowDelete` / `AllowCreateFolder` flags** and their
  guard branches — deferred here from Phase 3a because they touch 10+ sites. The rule becomes
  uniform: if Connapse does not own the bytes, you do not mutate them through Connapse.
- Decide whether `FileBrowserChangeNotifier` gets a publisher or is deleted. It has had none
  since the watcher was removed in #364.
- `Search.razor` gains an owner-kind badge on results and a source facet for scoping queries.

**Done when:** no external connector is reachable through the file browser, and the write flags
are gone — both asserted by test rather than by inspection.

The test that matters: seed a source and a managed container, then drive the file-browser route
for each. The container must list its files; the source must be refused, not merely return an
empty listing. An empty result would pass today and silently become a real listing the first
time someone widens the lookup, which is exactly the regression this phase exists to prevent.

## Out of scope

Phase 5 (#353) removes the compatibility shims and drops the connector columns — it must land
in v0.4.0, but not here. The documentation debt (ten references to deleted components across
the published docs) is tracked separately.
