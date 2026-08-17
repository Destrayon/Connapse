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
`/api/sources/{id}/browse`. `GET /api/sources/{id}` returns the scope and its sync state — name,
description, connection, enabled, last sync status, last error, last synced at — and never the
`ScopeJson` credentials-adjacent detail beyond what the source itself declares.

Authorization matches the container endpoints: `RequireViewer` to read, `RequireEditor` to
mutate. Creation and update validate that the named connection exists, because a source pointing
at a missing connection is skipped silently by the sync service.

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

Whether `/api/connections` needs REST routes is an open question for Phase 4: the Blazor UI can
use `IConnectionStore` directly, and the issue scopes 3c to sources only. Flagging rather than
deciding it here.
