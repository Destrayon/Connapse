# Phase 3c — `/api/sources` endpoints and the MCP `kind` discriminator

Part of #351 (Phase 3 of epic #348). Follows 3a (#361) and 3b (#364).

## Where this picks up

Sources exist, sync themselves, and own documents. Nothing outside the sync engine can see them:
there is no `SourcesEndpoints`, no `MapSourcesEndpoints` registration, and no `kind` field
anywhere in `McpTools`. An operator can only reach a source through the database.

## The contract this phase implements

From the design spec:

> Sources are listed alongside containers in the MCP and REST contracts. `container_list` and
> `search_knowledge` continue to accept any owner id, with a `kind` field in the response
> distinguishing managed from source.

and, in the same breath, the limit on that:

> Listing a source as a searchable *scope* — its name, kind, and summary — is not the same as
> enumerating the documents inside it. No REST or MCP endpoint returns a file listing for a
> source, and `list_files` remains container-only.

Those two together are the whole phase. The first is what stops this being a breaking change for
existing agents and the CLI; the second is the permissions leak the epic exists to close. A change
that satisfies one and not the other is a failure, so the tests have to pin both directions.

## Task 1 — `/api/sources`

**Goal:** an operator can manage sources over REST without touching the database.

`GET /api/sources`, `GET /api/sources/{id}`, `POST /api/sources`, `PATCH /api/sources/{id}`,
`DELETE /api/sources/{id}`, and `POST /api/sources/{id}/sync` to force a cycle.

Deliberately absent: anything returning documents or paths. No `/api/sources/{id}/documents`, no
`/api/sources/{id}/browse`.

**Responses use dedicated DTOs, never the `Source` record.** `Source` carries `ScopeJson`, which
names buckets, prefixes, and filesystem subpaths — infrastructure detail a viewer has no reason
to receive, and the kind of thing that turns a read route into reconnaissance. The DTO exposes
id, name, description, connection id, enabled, and sync state. Tests assert the *absence* of
`ScopeJson` rather than the presence of the wanted fields, because a later refactor that starts
serializing the record directly would otherwise pass silently.

Authorization is **not** copied from the container endpoints. Reads take `RequireViewer`;
mutations take `RequireAdmin`, not `RequireEditor`. Creating a source chooses what external data
gets indexed and made searchable, bounded only by whatever the connection's credential can reach
— an administrative act. Airbyte reaches the same conclusion by splitting source-editor from
destination-editor rather than treating them as one grant.

Creation and update validate that the named connection exists, because a source pointing at a
missing connection is skipped silently by the sync service.

**Done when:** all six routes exist, are registered in `Program.cs`, and integration tests cover
each — including that a source id passed to a container document route is rejected.

## Task 2 — `kind` on the MCP surface

**Goal:** an agent discovers sources and can search them, without gaining a way to enumerate them.

- `container_list` lists containers and sources together, each carrying `kind: managed | source`.
- `search_knowledge` resolves a source id or name as well as a container's.
- `container_describe` accepts a source, reporting its scope and sync state.

The id resolver is the crux. `ResolveContainerIdAsync` today checks only `IContainerStore`, which
is exactly why `list_files` rejects a source id already. Adding source resolution to *that* method
would silently open `list_files` too. A second resolver is needed instead, used only by the tools
that may accept either kind.

**Done when:** an agent can list and search a source, `list_files` and every mutating tool still
refuse one, and tests assert both.

## Task 3 — Prove the boundary holds

**Goal:** the no-enumeration rule is enforced by tests, not by convention.

One test per surface that must refuse a source id: `list_files`, `container_delete`,
`upload_file`, `delete_file`, and the REST document/folder routes. Each asserts a refusal, not
merely an empty result — an empty listing today would become a real listing the moment someone
"fixes" the resolver.

**Done when:** every enumeration and mutation surface has a test pinning its refusal.

## Not in this phase

The Sources tab, the Connections settings screen, and Home simplification are Phase 4 (#352).
Removing the Filesystem write flags is deferred there too — it touches 10+ sites in
`FileBrowser.razor`. Dropping `containers.connector_type`/`connector_config` is Phase 5 (#353).

**Mutating `/api/connections` routes are out of scope — decided, not deferred.** Connections are
the credential and filesystem-root boundary, and this project already refuses to accept cloud
credentials over an API; a filesystem `allowedRoot` confers the same class of authority. They are
configured out of band and managed in Admin Settings, which is the channel-scoped model Grafana
uses, where the provisioning file can create datasources the HTTP API deliberately cannot.
Read-only connection routes are fine if Phase 4 wants them for the UI.

Any later proposal to add connection mutation over REST, CLI, or MCP reopens this security
review rather than being an incremental addition — recorded here so it cannot arrive as one.
Background: `docs/research/programmatic-source-configuration-safety-2026-08-17.md`.
