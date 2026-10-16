# Custom source protocol — design

**Date:** 2026-10-14
**Status:** Draft for owner review
**Research:** [custom-source-protocol-2026-10-14.md](../../research/custom-source-protocol-2026-10-14.md)

## Goal

Let a customer connect Connapse to any system — Slack, Discord, S3, Jira, Linear, an internal app — by
building their own **middle-man server** against a published protocol. The middle man supplies content
and decides, live, what each Connapse user may read. Connapse will not build middle men; it ships the
specification, a conformance tool, and a small example server.

## Decisions

| Decision | Choice |
|---|---|
| Who builds middle men | Customers only |
| Model | Provider → Connection → Source, like GitHub and Atlassian. A **custom provider** registers a middle man for permissions; a **custom connection** requires a provider; a **custom source** is created under a connection (per-user) or without one (content only) |
| Content | Connapse pulls; full listing required, change feed optional; Connapse parses, chunks and embeds |
| Content-only sources | A custom source with no connection. Users must be signed in to Connapse and have access to the source, but nothing is checked against the target system |
| User identity sent | Only the Connapse user ID. All mapping to the target system is the middle man's job |
| Account linking | Optional, provider-level. The middle man hosts the linking page and **stores the link in its own database**; Connapse sends a signed token, confirms the link against the session, reads link status live, and stores nothing about the link |
| Permissions (connected sources) | Live. A **check** of the final results on every search is required; **access keys** for pre-filtering are optional |
| Key list caching | None by default; a middle man may opt in with a TTL |
| Sync interval | 5 minutes default; a middle man may declare a longer minimum |
| Transport | HTTPS + JSON; permissions follow the OpenID AuthZEN 1.0 Authorization API |

## Providers, connections and sources

**Custom provider** — on the Providers page, built from `ProviderStepCard` like the other providers.
There is no limit: an admin may register as many custom providers as they want, each with its own name,
middle man, token and link secret. Unlike the built-in providers, which appear once each, custom providers
are a list with an **Add custom provider** button, and each opens its own page at
`/admin/providers/custom/{id}`. Each provider may back any number of connections.
1. *Endpoint* — base URL and bearer token; Connapse reads the discovery document and checks that a wrong
   token is refused.
2. *Permissions* — the discovery document must list `permissions.check`; Connapse runs one check call to
   prove it answers. The provider cannot be saved without this step.
3. *Account linking (optional)* — shown when discovery lists `permissions.linking`; Connapse generates the
   link secret and shows it once for the admin to paste into the middle man.

**Custom connection** — on the Connections page; the admin picks a custom provider and names it. It
holds no credential of its own: it is what turns on permission checks for the sources that use it, through
its provider's permission endpoints.

**Custom source** — one kind of source, whether or not it has a connection (owner decision, 2026-10-15). It
is created the same way, holds the same things, and syncs through the same connector either way: the
middle man's URL and token, a scope chosen from `/v1/scopes`, the same document paths and the same sync.
**The only difference is whether its connection is filled in:**
- *No connection (content only)*: signed-in users with access to the source see all of its content. The
  source dialog and source page say "Signed-in users with access to this source see all of its content —
  nothing is checked against the source system."
- *A custom connection*: searches apply the live permission check (and keys, when offered) through the
  connection's provider, and its documents carry a resource URI and access keys so they can be checked.

Whether a source checks permissions is decided by its structure, never by what the middle man
advertises:
- A source is created either under a connection or without one, and never moves between the two (the
  existing `ck_sources_connection_xor_provider` constraint already holds this). To change it, the admin
  deletes the source and creates a new one.
- A connection cannot be deleted while it has sources, and a provider cannot be deleted while it has
  connections, so nothing is ever orphaned into content only.
- If a provider's middle man stops listing `permissions.check`, or the provider's settings are cleared,
  searches of its connected sources deny until it is fixed.
- A content-only source whose middle man advertises `permissions` keeps working as content only, and its
  source page warns that it could be created under a connection instead.

Prior art: Coveo and Amazon Q Business make "everyone sees it" an explicit per-source admin choice;
Amazon Kendra and Elastic treat a missing permission field as public, which is the shape this structure
avoids (see the research report).

## How a connected source's search stays correct and fast

1. **Keys (optional).** Connapse asks the middle man which access keys the user holds in each custom
   source. An access key is an opaque label the middle man attached to documents at sync, such as
   `channel:C123`. Postgres then considers only documents carrying one of those keys.
2. **Check (required).** Connapse sends the top candidates to the middle man in one batched call. Only an
   explicit yes survives.

The check is the only thing that grants access. Keys only choose which candidates the check sees, so a
wrong or stale key list can lower recall but can never show a document the user may not read.

Freshness, with a 5-minute sync and no key cache:
- Lost access (user removed, document locked down) applies on the next search.
- A user gaining access (joined a channel) applies on the next search.
- A document opening up (restriction removed) becomes findable at the next sync, because its keys update then.

## Protocol v1

All paths are relative to the middle man's base URL (the provider's, or a content-only source's own). Every request carries
`Authorization: Bearer <token>`. JSON bodies; unknown fields are ignored on both sides.

### Discovery

`GET /.well-known/connapse-source`

```json
{
  "protocol_versions": ["1"],
  "name": "Acme Slack bridge",
  "capabilities": {
    "changes": true,
    "tombstones": true,
    "min_sync_interval_seconds": 300,
    "max_page_size": 500,
    "permissions": {
      "check": { "max_batch": 200 },
      "access_keys": { "cache_ttl_seconds": 0 },
      "linking": { "link_url": "https://bridge.acme.internal/link" }
    }
  }
}
```

A content-only middle man omits `permissions` entirely. An absent capability means unsupported. Connapse re-reads this document when the connection is saved and
before each sync.

### Scopes

`GET /v1/scopes?page_token=` → `{ "items": [{ "id", "name", "description" }], "next_page_token" }`

The admin chooses which scopes become Sources.

### Content

`GET /v1/scopes/{scope}/documents?page_token=` — the full listing.

```json
{
  "items": [{
    "id": "C123/1697040000.000100",
    "path": "general/2026-10/thread-1697040000.md",
    "title": "Deploy freeze thread",
    "content_type": "text/markdown",
    "size": 2048,
    "version": "a1b2c3",
    "modified_at": "2026-10-13T19:20:00Z",
    "url": "https://acme.slack.com/archives/C123/p1697040000000100",
    "access_keys": ["channel:C123"],
    "access_version": "k7"
  }],
  "next_page_token": null,
  "change_cursor": "opaque"
}
```

- `id` is stable and chosen by the middle man. `path` is for display and folder grouping only.
- `version` changes when content changes; `access_version` changes when `access_keys` change. Connapse
  re-downloads and re-embeds only when `version` changes.
- A page carries either `next_page_token` or, on the last page, `change_cursor` — never both. The middle
  man takes `change_cursor` **before** it starts the listing.

`GET /v1/scopes/{scope}/changes?cursor=&page_token=` — only when `capabilities.changes`.
Items are `{ "id", "op": "upsert" | "delete", ...listing fields when upsert }`. The last page carries
`next_cursor`. `410 Gone` means the cursor is no longer valid: Connapse discards it and runs a full listing.

`GET /v1/scopes/{scope}/documents/{id}/content` — the bytes, with `Content-Type` and `ETag`.
`If-None-Match` may return `304`. `404`/`410` mean the document is gone.

Sync rules Connapse applies (these already exist in `ISyncCursorConnector`):
- The cursor is saved only after a pass completes; a crash replays from the last saved cursor.
- Without tombstones, deletions come from comparing a completed full listing with the index, behind the
  existing deletion guard. A listing that did not finish never deletes anything.
- `429` / `503` with `Retry-After` defers the sync and keeps the cursor.

### Permissions (AuthZEN 1.0 profile) — required for a provider

The subject is always `{ "type": "user", "id": "<connapse user id>" }`. The action is always `{ "name": "read" }`.

**Keys** — only when `permissions.access_keys`.
`POST /access/v1/search/resource`

```json
{
  "subject": { "type": "user", "id": "8f0c…" },
  "action": { "name": "read" },
  "resource": { "type": "access_key", "properties": { "scope": "C123-workspace" } },
  "page": { "token": "", "limit": 1000 }
}
```
→ `{ "results": [{ "type": "access_key", "id": "channel:C123" }], "page": { "next_token": "" } }`

Connapse reads at most 10,000 keys per user per scope. If there are more, it uses the first 10,000 and
searches show a "results may be incomplete" notice.

**Check** — required.
`POST /access/v1/evaluations`

```json
{
  "subject": { "type": "user", "id": "8f0c…" },
  "action": { "name": "read" },
  "evaluations": [
    { "resource": { "type": "document", "id": "C123/1697040000.000100", "properties": { "scope": "C123-workspace" } } }
  ]
}
```
→ `{ "evaluations": [{ "decision": true }] }`, one result per request item, in order.

### Linking — only when `permissions.linking`

Connapse cannot verify a user's identity in the target system, and the middle man cannot see who is
signed in to Connapse. The link token joins the two. The link itself — which target-system account belongs
to which Connapse user, and any target-system tokens — lives only in the middle man's database. Connapse
keeps no link table for custom connections:

1. On the profile integrations page, the user clicks **Link** for the provider.
2. Connapse sends the browser to `link_url` with an auto-submitting form POST (not a query string, so the
   token stays out of Referer headers, logs and history) carrying `token=<jwt>`. The JWT is HS256-signed
   with the provider's **link secret**, which Connapse generates and shows the admin once. Claims:
   `iss` (Connapse's base URL), `sub` (Connapse user ID), `aud` (provider ID), `jti`, `exp` (5 minutes),
   `return_url`. The middle man allows at most 60 seconds of clock skew.
3. The middle man verifies the token, rejects a reused `jti`, and signs the user in to the target system,
   binding that OAuth flow to the browser with its own `state` cookie (PKCE recommended). It stores the
   result as a **pending** link and redirects to `return_url?code=<one-time code>`.
4. **Confirmation.** Connapse's return endpoint requires a signed-in session. It calls
   `POST /v1/links/confirm` with `{ "code", "connapse_user_id": <the session's user> }`. The middle man
   activates the pending link only if the code is unused, unexpired, and belongs to that same Connapse user;
   otherwise it discards it and answers `409`.
5. Status: `GET /v1/links/{connapse_user_id}` → `{ "linked": true, "display_name": "alice@acme" }`.
   Connapse calls this live whenever the profile integrations page renders; if it fails, the row shows
   "status unknown".
   Unlink: `DELETE /v1/links/{connapse_user_id}`, idempotent (`404` counts as done).

Why step 4 exists: without it, an attacker could click Link, send the resulting page to a colleague, and
have the colleague finish the target-system sign-in in their own browser — tying the attacker's Connapse
account to the colleague's Slack or Jira account. The jti and the middle man's `state` cannot stop that,
because the whole flow is consistent inside the colleague's browser. Confirmation fails because the
colleague's Connapse session is not the user in the token. This is the same reason Connapse's built-in
AWS, Atlassian and GitHub links confirm against the session, and it is how LTI 1.3 launches defend
against login CSRF.

Connapse keeps no link state, but it does:
- record audit events (link started, confirmed, rejected, unlinked) with user, connection and time;
- on Connapse user deletion, queue `DELETE /v1/links/{id}` to every custom provider with linking in a
  durable outbox and retry until each acknowledges, alerting on connections that never do.

An unlinked user simply gets no keys and "no" from the check.

### Errors

Errors use RFC 9457 problem details (`application/problem+json`) with an optional `retryable` boolean.
`429` and `503` may carry `Retry-After`.

## Fail-closed rules (connected sources)

- Only an explicit `"decision": true` grants access. A timeout, a non-200 response, a missing or malformed
  decision, a result count that does not match the request, or an item with `context.error` means no access
  for that document.
- The check has a deadline (default 2 s, admin-configurable). When it fails, the source's results are
  dropped silently; other sources are unaffected. No notice names the source: a user who may not see it
  must not learn it exists (owner, 2026-10-16; #753 closed).
- When the keys call fails, Connapse skips the pre-filter for that source (check-only) rather than
  denying outright — the check still decides. If both fail, the source contributes nothing.
- A caller that is not a person (no Connapse user) gets nothing from custom sources.
- In a source that supports keys, a document with no keys matches no one's pre-filter.
- Connapse compiles keys into SQL parameters only. Nothing from the middle man is ever spliced into a query.

## Connapse changes

**Data.**
- A custom provider record holds base URL, bearer token (encrypted like other credentials), link secret
  (when linking is used), and the last discovery document. Several custom providers may exist.
- `ConnectionProvider.Custom` connections reference one custom provider and hold no credential.
- Every custom source stores its own URL (in its scope) and encrypted token, with or without a connection.
  Without one it sets `Provider = Custom`; with one it sets `ConnectionId` and its provider is the
  connection's, as the existing `ck_sources_connection_xor_provider` constraint requires.
- Each custom source stores its scope ID. Documents gain `access_keys text[]` (GIN index) and
  `access_version text`, populated only for connected custom sources. Resource URIs are
  `custom://{sourceId}/{documentId}`.
- No link table: link state is read from the middle man.

**Connector.** `CustomSourceConnector : ISyncCursorConnector`, used by both kinds of source, maps listing
and changes onto `SyncDelta` (`IsFullListing`, `RequiresFullResync` on 410, `Deferred` on 429/503). A
key-only change (`access_version` differs, `version` same) updates the row without re-embedding.

**Search.** Content-only sources are searched like any other source without per-user filtering — still
behind Connapse sign-in and source access. For connected sources, a `CustomSourceScopeResolver` joins the
existing `CompositeSearchScopeResolver` and returns, per source, the source prefix plus the user's keys.
Vector and keyword search add `access_keys && @keys` for those sources. Enable
`ivfflat.iterative_scan = relaxed_order` so narrow key sets do not starve results. A
`CustomSourceResultVerifier` (same role as `AtlassianSearchResultVerifier`) over-fetches 3× the requested
count from connected custom sources, checks them in batches of `max_batch`, and refills once if too few
survive. Result counts are reported after the check.

**Sync scheduling.** Default interval 5 minutes; the effective interval is the larger of the source's
setting and `min_sync_interval_seconds`.

**UI.**
- Providers page: a "Custom providers" list (unlimited) with **Add custom provider**; each provider's page
  is built from `ProviderStepCard` steps (endpoint, permissions, optional linking).
- Connections page: add a custom connection by picking a custom provider; shows its declared capabilities.
- Sources page: the New source dialog offers a connection's scopes, or a content-only custom source
  (URL, token, scope) with the content-only label.
- Profile integrations page: one row per custom provider with linking, showing live status and Link/Unlink.

**Security.** Custom providers and connections are admin-only configuration with no REST, CLI or MCP write
route, like every connection. Content-only custom sources are created like any other source (see
below). All middle-man URLs must be
HTTPS except explicitly allowed private hosts, and go through the connection allowlist to prevent SSRF.
Tokens and link secrets are never shown again after creation and never logged.

## Who can create content-only sources

Content-only custom sources follow the same rules as every other source: anyone permitted to create
sources can create one, from the UI or the admin REST source route. Their URL still goes through the same
HTTPS and private-host checks as every middle-man URL. An allowed-hosts list is deferred until someone
asks for it.

## Shipped with the protocol

- `docs/protocols/custom-source-v1.md` and an OpenAPI 3.1 document.
- **Conformance tool** — a CLI run against a URL, with tests gated by declared capabilities: discovery,
  auth rejection, pagination (stable tokens, end marker, page-size cap), full listing then change round
  trip, cursor 410, tombstones, content ETag/304, keys pagination, check ordering and batch size,
  fail-closed behaviour, link-token verification, `jti` replay rejection, and confirmation refusing a code
  presented for a different Connapse user.
- **Example server** — a small server backed by a folder of files and a JSON permissions file. It exists
  to test against and to read, not to deploy.

## Out of scope

- Push indexing (the middle man sending documents to Connapse); webhooks may come later only as a
  "sync now" hint.
- Caching check results.
- MCP as the transport.
- Write access to the target system.
- Per-user permissions for systems with no per-user answer (for example Notion) — those can only be
  content-only sources.

## Delivery

One epic, split into issues filed before implementation:
1. Protocol document, OpenAPI, and example server.
2. Content-only custom sources: discovery, scope selection, content sync (usable on its own).
3. Custom provider and connection: provider steps, connection, key storage, search pre-filter, iterative
   scan, result verifier.
4. Account linking (link secret, token, confirmation, live status on the profile integrations row).
5. Conformance CLI.

## Testing

- Unit: `SyncDelta` mapping, fail-closed decision parsing (every malformed shape denies), key-to-SQL
  parameter compilation, link-token claims.
- Integration: the example server in a container, as a content-only source and under a provider;
  content-only search returns everything to a signed-in user with source access, a provider whose middle man
  stops advertising the check makes its connected sources deny, deleting a connection with sources is refused, sync, key change without re-embed, search with a user who
  sees a small slice (recall stays up with keys), check timeout drops results, unlinked user sees nothing.
- The conformance CLI is run in CI against the example server.
