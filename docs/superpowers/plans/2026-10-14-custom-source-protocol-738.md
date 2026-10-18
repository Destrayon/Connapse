# Custom Source Protocol v1 — Specification and Example Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish the custom source protocol (a prose specification and an OpenAPI 3.1 document) and a small example middle-man server that implements every capability, so later issues and third parties have something exact to build and test against. Issue #738, part of epic #737.

**Architecture:** The protocol lives in `docs/protocols/`. The example server is a standalone ASP.NET Core minimal API in `samples/Connapse.CustomSource.Example` that references no Connapse project, because it stands in for what a customer writes. It serves files from a data folder (`scopes/{scopeId}/...`), derives one access key per top-level folder, reads users, grants, denies and preset links from `example.json`, and keeps account links in `links.json` in that folder. Its tests run it in-process with `WebApplicationFactory`, and one test fails if the OpenAPI document and the server's routes disagree.

**Tech Stack:** .NET 10 minimal APIs, System.Text.Json (snake_case), Microsoft.IdentityModel.JsonWebTokens 8.18.0 (HS256 link tokens), xUnit 2.9.3, FluentAssertions 8.8.0, Microsoft.AspNetCore.Mvc.Testing 10.0.7.

**Spec:** `docs/superpowers/specs/2026-10-14-custom-source-protocol-design.md`

## Global Constraints

- .NET 10, file-scoped namespaces, nullable enabled, implicit usings, primary constructors for DI, records for DTOs, no `var` for primitive types, no `dynamic`, async all the way.
- The example server references no project under `src/`.
- JSON bodies are snake_case; null members are omitted; unknown fields are ignored.
- Every endpoint requires `Authorization: Bearer <token>` except the two browser endpoints `POST /link` and `POST /link/complete`.
- Errors are RFC 9457 problem details (`application/problem+json`).
- Only an explicit `"decision": true` grants access.
- Link token: HS256, claims `iss`, `sub`, `aud` (provider ID), `jti`, `exp` (5 minutes), `return_url`; at most 60 seconds of clock skew; `jti` single use.
- Tests: `[Trait("Category", "Unit")]` (in-process, no Docker), naming `MethodName_Scenario_ExpectedResult`.
- Commits: `<type>: <summary>` ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Stage explicit paths only, never `git add -A`.
- PRs target `epic/737-custom-source-protocol`, stay under ~300 hand-written lines each, and stack: PR A = Task 1; PR B = Tasks 2–4; PR C = Tasks 5–7. Intermediate PRs say `Part of #738`, the last says `Closes #738`.

## Review Focus

1. A document ID that decodes to `../` or an absolute path must return 404 and never read outside the scope folder — pinned in Task 3.
2. A cursor the server did not issue (forged, or from before a restart) must return 410, not an empty change list — pinned in Task 4.
3. An evaluations batch larger than `max_batch` must be refused with 400 rather than silently truncated, and malformed items must decide `false` — pinned in Task 5.
4. A link token replayed, signed with the wrong secret, or for another audience must be refused — pinned in Task 6.
5. A link code confirmed for a different Connapse user than the one who started the link must link nobody — pinned in Task 6.

---

## File Structure

```
docs/protocols/custom-source-v1.md                 prose specification (Task 1)
docs/protocols/custom-source-v1.openapi.json       OpenAPI 3.1 (Task 1)
samples/Connapse.CustomSource.Example/
  Connapse.CustomSource.Example.csproj
  Program.cs                   wiring
  ExampleConfig.cs             example.json model + ExampleDataDirectory
  BearerTokenFilter.cs         bearer auth endpoint filter
  ProtocolModels.cs            discovery, listing and change DTOs
  ContentEndpoints.cs          discovery, scopes, listing, changes, content
  FolderCatalog.cs             scope folder scan, document IDs, versions, access keys
  ListingToken.cs              listing page token
  ChangeJournal.cs             snapshot diff, cursors, 410
  AuthZenModels.cs             AuthZEN request/response DTOs
  AccessPolicy.cs              keys held and per-document decisions
  PermissionEndpoints.cs       /access/v1/search/resource, /access/v1/evaluations
  LinkStore.cs                 links.json, pending links, sessions, used jtis
  LinkEndpoints.cs             /link, /link/complete, /v1/links/*
  sample-data/                 example.json + scopes/ (Task 7)
  Dockerfile, README.md        (Task 7)
tests/Connapse.CustomSource.Example.Tests/
  Connapse.CustomSource.Example.Tests.csproj
  ExampleServer.cs             test host over a temp data folder
  DiscoveryTests.cs, DocumentListingTests.cs, ChangeFeedTests.cs,
  PermissionTests.cs, LinkingTests.cs, OpenApiAgreementTests.cs
Connapse.slnx                  add both projects under /samples/ and /tests/
```

---

### Task 1: Protocol specification and OpenAPI document

**Files:**
- Create: `docs/protocols/custom-source-v1.md`
- Create: `docs/protocols/custom-source-v1.openapi.json`

**Interfaces:**
- Produces: the paths every later task serves, exactly: `/.well-known/connapse-source`, `/v1/scopes`, `/v1/scopes/{scope}/documents`, `/v1/scopes/{scope}/changes`, `/v1/scopes/{scope}/documents/{id}/content`, `/access/v1/search/resource`, `/access/v1/evaluations`, `/v1/links/confirm`, `/v1/links/{connapse_user_id}`. Task 7's agreement test compares these against the server.

- [ ] **Step 1: Write the prose specification**

Create `docs/protocols/custom-source-v1.md`:

````markdown
# Connapse custom source protocol, version 1

A **middle man** is a server you run in front of any system — Slack, Jira, S3, an internal app. Connapse
pulls content from it and, when it is registered as a custom provider, asks it live which documents each
Connapse user may read. Connapse parses, chunks and embeds the content itself.

There are two ways Connapse uses a middle man:

- **Content-only source.** Implement [Discovery](#discovery), [Scopes](#scopes) and [Content](#content).
  Signed-in Connapse users with access to the source see all of its documents.
- **Custom provider.** Also implement [Permissions](#permissions), and optionally [Linking](#linking).
  Every search checks each result with you before showing it.

## Conventions

- HTTPS, JSON request and response bodies, snake_case member names.
- Every request carries `Authorization: Bearer <token>`, the token the admin entered in Connapse. Refuse a
  missing or wrong token with `401`. The browser endpoints in [Linking](#linking) are the only exception.
- Ignore members you do not recognise; Connapse does the same. Omit members you have no value for.
- Errors are RFC 9457 problem details (`application/problem+json`). Add `"retryable": true` when retrying
  the same request later may succeed. `429` and `503` may carry `Retry-After` (seconds).
- Page tokens and cursors are opaque strings you choose. Connapse sends them back unchanged.
- Document IDs are strings you choose, stable for the life of the document, at most 512 characters.
  Connapse percent-encodes an ID as a single path segment.
- The version is in the path (`/v1`). Version 1 only gains optional members and optional capabilities.

## Discovery

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

| Member | Meaning |
|---|---|
| `changes` | You serve [`/changes`](#change-feed). Without it Connapse runs a full listing every sync. |
| `tombstones` | Your change feed reports deletions. Without it Connapse finds deletions by comparing full listings. |
| `min_sync_interval_seconds` | Connapse never syncs a source more often than this. The default interval is 300. |
| `max_page_size` | The most items you return in one page. |
| `permissions.check` | Required for a custom provider. `max_batch` is the most evaluations you accept in one call. |
| `permissions.access_keys` | You attach access keys to documents and answer key searches. `cache_ttl_seconds` lets Connapse cache a user's keys; 0 means never. |
| `permissions.linking` | You run an account-linking page at `link_url`. |

An absent member means unsupported. A content-only middle man omits `permissions`.

## Scopes

`GET /v1/scopes?page_token=`

```json
{ "items": [{ "id": "eng", "name": "Engineering", "description": "Engineering workspace" }], "next_page_token": null }
```

A scope is one isolated part of your system — a workspace, bucket or project. Each scope an admin picks
becomes a separate Connapse source with its own sync state.

## Content

### Full listing

`GET /v1/scopes/{scope}/documents?page_token=`

```json
{
  "items": [{
    "id": "C123-1697040000.000100",
    "path": "general/2026-10/deploy-freeze.md",
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

- `path` is for display and folder grouping only. `title`, `url`, `modified_at` are optional.
- `version` must change whenever the bytes change. Connapse downloads and re-embeds a document only when
  its `version` changes.
- `access_keys` and `access_version` are only for middle men with `permissions.access_keys`.
  `access_version` must change whenever `access_keys` change; Connapse then updates the keys without
  re-embedding.
- A page carries `next_page_token` when more pages follow, or — on the last page only — `change_cursor`.
  Never both.
- Take `change_cursor` **before** you start reading the listing, so changes made while Connapse pages
  through it are not lost.
- Unknown scope: `404`.

### Change feed

Only when `capabilities.changes` is true.

`GET /v1/scopes/{scope}/changes?cursor=&page_token=`

```json
{
  "items": [
    { "id": "C123-1697040000.000100", "op": "upsert", "path": "…", "version": "a1b2c4", "...": "listing members" },
    { "id": "C123-1697040099.000200", "op": "delete" }
  ],
  "next_page_token": null,
  "next_cursor": "opaque"
}
```

- `cursor` is a `change_cursor` or `next_cursor` you issued. Connapse sends `page_token` instead of
  `cursor` to continue a multi-page response.
- An upsert carries the same members as a listing item.
- A page carries `next_page_token` when more pages follow, or `next_cursor` on the last page. Connapse
  saves the cursor only after it reaches the last page; after a crash it replays from the previous cursor,
  so the same change may arrive twice.
- A cursor you no longer recognise: `410 Gone`. Connapse discards it and runs a full listing.
- Rate limited: `429` with `Retry-After`. Connapse keeps the cursor and tries later.

### Bytes

`GET /v1/scopes/{scope}/documents/{id}/content`

Stream the bytes with `Content-Type` and `ETag`. Answer `If-None-Match` with `304` when unchanged.
`404` or `410` means the document is gone.

### Deletions

With `tombstones`, report deletions in the change feed. Without it, Connapse removes documents missing from
a **completed** full listing, and pauses instead when an unusually large share would be removed.

## Permissions

Required for a custom provider. These two endpoints are a profile of the OpenID AuthZEN Authorization API
1.0. The subject is always `{ "type": "user", "id": "<Connapse user ID>" }` — Connapse sends nothing else
about the user, so mapping that ID to an account in your system is your job (see [Linking](#linking)).
The action is always `{ "name": "read" }`.

### Check — required

`POST /access/v1/evaluations`

```json
{
  "subject": { "type": "user", "id": "8f0c4f7e-…" },
  "action": { "name": "read" },
  "evaluations": [
    { "resource": { "type": "document", "id": "C123-1697040000.000100", "properties": { "scope": "eng" } } }
  ]
}
```
→
```json
{ "evaluations": [{ "decision": true }] }
```

- Connapse sends `subject` and `action` once at the top level; each item carries only its `resource`.
- Answer with exactly one result per item, in the same order.
- `true` only when your system would let that user read that document right now. Answer `false` for an
  unknown user, an unknown document, or anything you cannot decide.
- Refuse a batch larger than `max_batch` with `400`.
- Connapse shows a document only on an explicit `"decision": true`. A timeout, an error, a missing or
  malformed decision, or a result count that does not match the request hides the documents involved.

### Access keys — optional

Only with `permissions.access_keys`. An access key is an opaque label you attach to documents in the
listing, such as `channel:C123` or `project:ENG`. Connapse uses a user's keys only to choose which documents
to rank before the check; the check alone decides what is shown, so keys make search find more for users
who can read a small part of a source.

`POST /access/v1/search/resource`

```json
{
  "subject": { "type": "user", "id": "8f0c4f7e-…" },
  "action": { "name": "read" },
  "resource": { "type": "access_key", "properties": { "scope": "eng" } },
  "page": { "token": "", "limit": 1000 }
}
```
→
```json
{ "results": [{ "type": "access_key", "id": "channel:C123" }], "page": { "next_token": "" } }
```

Return every key the user holds in that scope. An empty `next_token` means the last page. Where a document
is readable only when two conditions hold (a Jira project and an issue security level, say), give it one
key for that combination and grant that key only to users who meet both.

## Linking

Optional, with `permissions.linking`. Use it when you need the user's own account in your system. You keep
the link in your own storage; Connapse stores nothing about it.

1. The user clicks **Link** in Connapse. Connapse sends the browser to `link_url` with an auto-submitting
   form POST carrying `token`, an HS256 JWT signed with the provider's **link secret** (Connapse generates
   it and shows it to the admin once). Claims: `iss` (Connapse's base URL), `sub` (Connapse user ID),
   `aud` (Connapse's provider ID), `jti`, `exp` (5 minutes), `return_url`.
2. Verify the signature, `iss`, `aud` and `exp` (allow at most 60 seconds of clock skew), and refuse a
   `jti` you have already seen. Sign the user in to your system, binding that sign-in to this browser with
   your own state cookie. Keep the result as a **pending** link and redirect to `return_url` with
   `code=<one-time code>` added to its query.
3. Connapse checks who is signed in to Connapse and calls `POST /v1/links/confirm` with
   `{ "code": "…", "connapse_user_id": "…" }`. Activate the pending link only if the code is unused,
   unexpired and was issued for that same Connapse user; answer `200` with the status below. Otherwise
   discard it and answer `409`.

Step 3 stops one person from finishing a link another person started — an attacker who forwards an
unopened link page cannot get a colleague's account tied to their own.

Also serve:
- `GET /v1/links/{connapse_user_id}` → `{ "linked": true, "display_name": "alice@acme" }`
- `DELETE /v1/links/{connapse_user_id}` → `204`, also when there was no link. Connapse calls it when the
  user unlinks and when a Connapse user is deleted, and retries until it succeeds.

## Testing your middle man

The example server in `samples/Connapse.CustomSource.Example` implements every capability, and
`custom-source-v1.openapi.json` describes every endpoint above.
````

- [ ] **Step 2: Write the OpenAPI document**

Create `docs/protocols/custom-source-v1.openapi.json`:

```json
{
  "openapi": "3.1.0",
  "info": {
    "title": "Connapse custom source protocol",
    "version": "1",
    "description": "Endpoints a middle man implements so Connapse can sync content from it and check permissions live. Prose specification: custom-source-v1.md."
  },
  "security": [{ "bearer": [] }],
  "paths": {
    "/.well-known/connapse-source": {
      "get": {
        "summary": "Discovery document",
        "responses": {
          "200": { "description": "Capabilities", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Discovery" } } } },
          "401": { "$ref": "#/components/responses/Problem" }
        }
      }
    },
    "/v1/scopes": {
      "get": {
        "summary": "List scopes",
        "parameters": [{ "$ref": "#/components/parameters/PageToken" }],
        "responses": {
          "200": { "description": "Scopes", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ScopePage" } } } },
          "401": { "$ref": "#/components/responses/Problem" }
        }
      }
    },
    "/v1/scopes/{scope}/documents": {
      "get": {
        "summary": "Full listing",
        "parameters": [{ "$ref": "#/components/parameters/Scope" }, { "$ref": "#/components/parameters/PageToken" }],
        "responses": {
          "200": { "description": "One page of documents", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/DocumentPage" } } } },
          "400": { "$ref": "#/components/responses/Problem" },
          "401": { "$ref": "#/components/responses/Problem" },
          "404": { "$ref": "#/components/responses/Problem" },
          "429": { "$ref": "#/components/responses/Problem" }
        }
      }
    },
    "/v1/scopes/{scope}/changes": {
      "get": {
        "summary": "Change feed",
        "parameters": [
          { "$ref": "#/components/parameters/Scope" },
          { "name": "cursor", "in": "query", "schema": { "type": "string" } },
          { "$ref": "#/components/parameters/PageToken" }
        ],
        "responses": {
          "200": { "description": "One page of changes", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ChangePage" } } } },
          "400": { "$ref": "#/components/responses/Problem" },
          "401": { "$ref": "#/components/responses/Problem" },
          "404": { "$ref": "#/components/responses/Problem" },
          "410": { "$ref": "#/components/responses/Problem" },
          "429": { "$ref": "#/components/responses/Problem" }
        }
      }
    },
    "/v1/scopes/{scope}/documents/{id}/content": {
      "get": {
        "summary": "Document bytes",
        "parameters": [
          { "$ref": "#/components/parameters/Scope" },
          { "name": "id", "in": "path", "required": true, "schema": { "type": "string", "maxLength": 512 } },
          { "name": "If-None-Match", "in": "header", "schema": { "type": "string" } }
        ],
        "responses": {
          "200": { "description": "The bytes", "headers": { "ETag": { "schema": { "type": "string" } } }, "content": { "*/*": { "schema": { "type": "string", "contentMediaType": "application/octet-stream" } } } },
          "304": { "description": "Unchanged" },
          "401": { "$ref": "#/components/responses/Problem" },
          "404": { "$ref": "#/components/responses/Problem" },
          "410": { "$ref": "#/components/responses/Problem" }
        }
      }
    },
    "/access/v1/search/resource": {
      "post": {
        "summary": "Access keys the user holds in a scope (AuthZEN resource search)",
        "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ResourceSearchRequest" } } } },
        "responses": {
          "200": { "description": "Keys", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ResourceSearchResponse" } } } },
          "400": { "$ref": "#/components/responses/Problem" },
          "401": { "$ref": "#/components/responses/Problem" }
        }
      }
    },
    "/access/v1/evaluations": {
      "post": {
        "summary": "Batched permission check (AuthZEN evaluations)",
        "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/EvaluationsRequest" } } } },
        "responses": {
          "200": { "description": "One decision per item, in order", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/EvaluationsResponse" } } } },
          "400": { "$ref": "#/components/responses/Problem" },
          "401": { "$ref": "#/components/responses/Problem" }
        }
      }
    },
    "/v1/links/confirm": {
      "post": {
        "summary": "Activate a pending link for the signed-in Connapse user",
        "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ConfirmRequest" } } } },
        "responses": {
          "200": { "description": "Linked", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/LinkStatus" } } } },
          "400": { "$ref": "#/components/responses/Problem" },
          "401": { "$ref": "#/components/responses/Problem" },
          "409": { "$ref": "#/components/responses/Problem" }
        }
      }
    },
    "/v1/links/{connapse_user_id}": {
      "parameters": [{ "name": "connapse_user_id", "in": "path", "required": true, "schema": { "type": "string" } }],
      "get": {
        "summary": "Link status",
        "responses": {
          "200": { "description": "Status", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/LinkStatus" } } } },
          "401": { "$ref": "#/components/responses/Problem" }
        }
      },
      "delete": {
        "summary": "Unlink (idempotent)",
        "responses": {
          "204": { "description": "Not linked any more" },
          "401": { "$ref": "#/components/responses/Problem" }
        }
      }
    }
  },
  "components": {
    "securitySchemes": { "bearer": { "type": "http", "scheme": "bearer" } },
    "parameters": {
      "Scope": { "name": "scope", "in": "path", "required": true, "schema": { "type": "string" } },
      "PageToken": { "name": "page_token", "in": "query", "schema": { "type": "string" } }
    },
    "responses": {
      "Problem": { "description": "RFC 9457 problem details", "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/Problem" } } } }
    },
    "schemas": {
      "Problem": {
        "type": "object",
        "properties": {
          "type": { "type": "string" }, "title": { "type": "string" }, "status": { "type": "integer" },
          "detail": { "type": "string" }, "retryable": { "type": "boolean" }
        }
      },
      "Discovery": {
        "type": "object",
        "required": ["protocol_versions", "name", "capabilities"],
        "properties": {
          "protocol_versions": { "type": "array", "items": { "type": "string" } },
          "name": { "type": "string" },
          "capabilities": {
            "type": "object",
            "properties": {
              "changes": { "type": "boolean" },
              "tombstones": { "type": "boolean" },
              "min_sync_interval_seconds": { "type": "integer", "minimum": 0 },
              "max_page_size": { "type": "integer", "minimum": 1 },
              "permissions": {
                "type": "object",
                "required": ["check"],
                "properties": {
                  "check": { "type": "object", "required": ["max_batch"], "properties": { "max_batch": { "type": "integer", "minimum": 1 } } },
                  "access_keys": { "type": "object", "properties": { "cache_ttl_seconds": { "type": "integer", "minimum": 0 } } },
                  "linking": { "type": "object", "required": ["link_url"], "properties": { "link_url": { "type": "string", "format": "uri" } } }
                }
              }
            }
          }
        }
      },
      "Scope": {
        "type": "object", "required": ["id", "name"],
        "properties": { "id": { "type": "string" }, "name": { "type": "string" }, "description": { "type": "string" } }
      },
      "ScopePage": {
        "type": "object", "required": ["items"],
        "properties": { "items": { "type": "array", "items": { "$ref": "#/components/schemas/Scope" } }, "next_page_token": { "type": ["string", "null"] } }
      },
      "Document": {
        "type": "object",
        "required": ["id", "path", "content_type", "size", "version"],
        "properties": {
          "id": { "type": "string", "maxLength": 512 },
          "path": { "type": "string" },
          "title": { "type": "string" },
          "content_type": { "type": "string" },
          "size": { "type": "integer", "minimum": 0 },
          "version": { "type": "string" },
          "modified_at": { "type": "string", "format": "date-time" },
          "url": { "type": "string", "format": "uri" },
          "access_keys": { "type": "array", "items": { "type": "string" } },
          "access_version": { "type": "string" }
        }
      },
      "DocumentPage": {
        "type": "object", "required": ["items"],
        "properties": {
          "items": { "type": "array", "items": { "$ref": "#/components/schemas/Document" } },
          "next_page_token": { "type": ["string", "null"] },
          "change_cursor": { "type": ["string", "null"] }
        }
      },
      "Change": {
        "type": "object", "required": ["id", "op"],
        "properties": {
          "id": { "type": "string" }, "op": { "enum": ["upsert", "delete"] },
          "path": { "type": "string" }, "title": { "type": "string" }, "content_type": { "type": "string" },
          "size": { "type": "integer" }, "version": { "type": "string" }, "modified_at": { "type": "string", "format": "date-time" },
          "url": { "type": "string", "format": "uri" }, "access_keys": { "type": "array", "items": { "type": "string" } },
          "access_version": { "type": "string" }
        }
      },
      "ChangePage": {
        "type": "object", "required": ["items"],
        "properties": {
          "items": { "type": "array", "items": { "$ref": "#/components/schemas/Change" } },
          "next_page_token": { "type": ["string", "null"] },
          "next_cursor": { "type": ["string", "null"] }
        }
      },
      "Subject": {
        "type": "object", "required": ["type", "id"],
        "properties": { "type": { "const": "user" }, "id": { "type": "string" } }
      },
      "Action": { "type": "object", "required": ["name"], "properties": { "name": { "const": "read" } } },
      "ScopeProperties": { "type": "object", "required": ["scope"], "properties": { "scope": { "type": "string" } } },
      "ResourceSearchRequest": {
        "type": "object", "required": ["subject", "action", "resource"],
        "properties": {
          "subject": { "$ref": "#/components/schemas/Subject" },
          "action": { "$ref": "#/components/schemas/Action" },
          "resource": {
            "type": "object", "required": ["type", "properties"],
            "properties": { "type": { "const": "access_key" }, "properties": { "$ref": "#/components/schemas/ScopeProperties" } }
          },
          "page": { "type": "object", "properties": { "token": { "type": "string" }, "limit": { "type": "integer", "minimum": 1 } } }
        }
      },
      "ResourceSearchResponse": {
        "type": "object", "required": ["results", "page"],
        "properties": {
          "results": {
            "type": "array",
            "items": { "type": "object", "required": ["type", "id"], "properties": { "type": { "const": "access_key" }, "id": { "type": "string" } } }
          },
          "page": { "type": "object", "required": ["next_token"], "properties": { "next_token": { "type": "string" } } }
        }
      },
      "EvaluationsRequest": {
        "type": "object", "required": ["subject", "action", "evaluations"],
        "properties": {
          "subject": { "$ref": "#/components/schemas/Subject" },
          "action": { "$ref": "#/components/schemas/Action" },
          "evaluations": {
            "type": "array",
            "items": {
              "type": "object", "required": ["resource"],
              "properties": {
                "resource": {
                  "type": "object", "required": ["type", "id", "properties"],
                  "properties": { "type": { "const": "document" }, "id": { "type": "string" }, "properties": { "$ref": "#/components/schemas/ScopeProperties" } }
                }
              }
            }
          }
        }
      },
      "EvaluationsResponse": {
        "type": "object", "required": ["evaluations"],
        "properties": {
          "evaluations": { "type": "array", "items": { "type": "object", "required": ["decision"], "properties": { "decision": { "type": "boolean" } } } }
        }
      },
      "ConfirmRequest": {
        "type": "object", "required": ["code", "connapse_user_id"],
        "properties": { "code": { "type": "string" }, "connapse_user_id": { "type": "string" } }
      },
      "LinkStatus": {
        "type": "object", "required": ["linked"],
        "properties": { "linked": { "type": "boolean" }, "display_name": { "type": "string" } }
      }
    }
  }
}
```

- [ ] **Step 3: Check the OpenAPI document parses**

Run: `python -c "import json; d=json.load(open('docs/protocols/custom-source-v1.openapi.json', encoding='utf-8')); print(len(d['paths']))"`
Expected: `9`

- [ ] **Step 4: Commit and open PR A**

```bash
git add docs/protocols/custom-source-v1.md docs/protocols/custom-source-v1.openapi.json
git commit -m "docs: custom source protocol v1 specification and OpenAPI (#738)"
```

---

### Task 2: Example server project, configuration, bearer auth, discovery and scopes

**Files:**
- Create: `samples/Connapse.CustomSource.Example/Connapse.CustomSource.Example.csproj`
- Create: `samples/Connapse.CustomSource.Example/Program.cs`
- Create: `samples/Connapse.CustomSource.Example/ExampleConfig.cs`
- Create: `samples/Connapse.CustomSource.Example/BearerTokenFilter.cs`
- Create: `samples/Connapse.CustomSource.Example/ProtocolModels.cs`
- Create: `samples/Connapse.CustomSource.Example/ContentEndpoints.cs`
- Create: `tests/Connapse.CustomSource.Example.Tests/Connapse.CustomSource.Example.Tests.csproj`
- Create: `tests/Connapse.CustomSource.Example.Tests/ExampleServer.cs`
- Test: `tests/Connapse.CustomSource.Example.Tests/DiscoveryTests.cs`
- Modify: `Connapse.slnx`

**Interfaces:**
- Produces: `ExampleConfig` (record, `required Name`, `required Token`, `PageSize`, `PermissionsEnabled`, `MaxCheckBatch`, `LinkSecret`, `ConnapseIssuer`, `ProviderId`, `PublicBaseUrl`, `Scopes`, `Grants`, `Denies`, `PresetLinks`, `Users`; static `Load(string)`, static `JsonOptions`); `ExampleDataDirectory(string Path)`; `BearerTokenFilter`; `ContentEndpoints.MapContentEndpoints(this IEndpointRouteBuilder)`; test host `ExampleServer` with `Token`, `LinkSecret`, `AliceId`, `BobId`, `DefaultConfig()`, `WriteFile`, `DeleteFile`, `Client(bool authorized = true)`.

- [ ] **Step 1: Create the projects and add them to the solution**

`samples/Connapse.CustomSource.Example/Connapse.CustomSource.Example.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" Version="8.18.0" />
  </ItemGroup>

</Project>
```

`tests/Connapse.CustomSource.Example.Tests/Connapse.CustomSource.Example.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="FluentAssertions" Version="8.8.0" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.7" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\samples\Connapse.CustomSource.Example\Connapse.CustomSource.Example.csproj" />
  </ItemGroup>

</Project>
```

In `Connapse.slnx`, add before `<Folder Name="/src/">`:

```xml
  <Folder Name="/samples/">
    <Project Path="samples/Connapse.CustomSource.Example/Connapse.CustomSource.Example.csproj" />
  </Folder>
```

and inside `<Folder Name="/tests/">`:

```xml
    <Project Path="tests/Connapse.CustomSource.Example.Tests/Connapse.CustomSource.Example.Tests.csproj" />
```

- [ ] **Step 2: Write the test host**

`tests/Connapse.CustomSource.Example.Tests/ExampleServer.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Connapse.CustomSource.Example.Tests;

/// <summary>Runs the example server in-process over a fresh data folder.</summary>
public sealed class ExampleServer : IDisposable
{
    public const string Token = "test-token-0123456789abcdef";
    public const string LinkSecret = "test-link-secret-0123456789abcdef0123456789";
    public const string Issuer = "https://connapse.test";
    public const string ProviderId = "provider-1";
    public const string AliceId = "connapse-alice";
    public const string BobId = "connapse-bob";

    private readonly WebApplicationFactory<Program> _factory;

    public ExampleServer(ExampleConfig? config = null, IReadOnlyDictionary<string, string>? files = null)
    {
        DataDirectory = Directory.CreateTempSubdirectory("connapse-example-").FullName;
        File.WriteAllText(
            Path.Combine(DataDirectory, "example.json"),
            JsonSerializer.Serialize(config ?? DefaultConfig(), ExampleConfig.JsonOptions));
        foreach ((string path, string content) in files ?? new Dictionary<string, string>())
            WriteFile(path, content);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("DataDirectory", DataDirectory));
    }

    public string DataDirectory { get; }

    public IServiceProvider Services => _factory.Services;

    public static ExampleConfig DefaultConfig() => new()
    {
        Name = "Test bridge",
        Token = Token,
        PageSize = 2,
        Scopes = [new ScopeConfig("eng", "Engineering", "Engineering docs"), new ScopeConfig("ops", "Operations", null)],
        Grants = new() { ["alice"] = ["folder:eng/", "folder:eng/design"], ["bob"] = ["folder:eng/", "folder:ops/"] },
        Denies = new() { ["alice"] = ["eng/design/secret.md"] },
        PresetLinks = new() { [AliceId] = "alice", [BobId] = "bob" },
        Users = ["alice", "bob"],
        LinkSecret = LinkSecret,
        ConnapseIssuer = Issuer,
        ProviderId = ProviderId,
    };

    /// <param name="relativePath">Path under <c>scopes/</c>, starting with the scope ID.</param>
    public void WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(DataDirectory, "scopes", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void DeleteFile(string relativePath) => File.Delete(Path.Combine(DataDirectory, "scopes", relativePath));

    public HttpClient Client(bool authorized = true)
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (authorized)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(DataDirectory, recursive: true); }
        catch (IOException) { }
    }
}
```

- [ ] **Step 3: Write the failing discovery tests**

`tests/Connapse.CustomSource.Example.Tests/DiscoveryTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
public class DiscoveryTests
{
    [Fact]
    public async Task Discovery_WithoutToken_Returns401Problem()
    {
        using ExampleServer server = new();

        HttpResponseMessage response = await server.Client(authorized: false).GetAsync("/.well-known/connapse-source");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Discovery_WithWrongToken_Returns401()
    {
        using ExampleServer server = new();
        HttpClient client = server.Client(authorized: false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        HttpResponseMessage response = await client.GetAsync("/.well-known/connapse-source");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Discovery_FullConfig_AdvertisesEveryCapability()
    {
        using ExampleServer server = new();

        JsonElement root = await GetJson(server, "/.well-known/connapse-source");

        root.GetProperty("protocol_versions")[0].GetString().Should().Be("1");
        root.GetProperty("name").GetString().Should().Be("Test bridge");
        JsonElement capabilities = root.GetProperty("capabilities");
        capabilities.GetProperty("changes").GetBoolean().Should().BeTrue();
        capabilities.GetProperty("tombstones").GetBoolean().Should().BeTrue();
        capabilities.GetProperty("max_page_size").GetInt32().Should().Be(2);
        JsonElement permissions = capabilities.GetProperty("permissions");
        permissions.GetProperty("check").GetProperty("max_batch").GetInt32().Should().Be(100);
        permissions.GetProperty("access_keys").GetProperty("cache_ttl_seconds").GetInt32().Should().Be(0);
        permissions.GetProperty("linking").GetProperty("link_url").GetString().Should().EndWith("/link");
    }

    [Fact]
    public async Task Discovery_ContentOnlyConfig_OmitsPermissions()
    {
        using ExampleServer server = new(ExampleServer.DefaultConfig() with { PermissionsEnabled = false, LinkSecret = null });

        JsonElement root = await GetJson(server, "/.well-known/connapse-source");

        root.GetProperty("capabilities").TryGetProperty("permissions", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Scopes_ListsConfiguredScopes()
    {
        using ExampleServer server = new();

        JsonElement root = await GetJson(server, "/v1/scopes");

        root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString())
            .Should().Equal("eng", "ops");
        root.TryGetProperty("next_page_token", out _).Should().BeFalse();
    }

    internal static async Task<JsonElement> GetJson(ExampleServer server, string url)
    {
        HttpResponseMessage response = await server.Client().GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/Connapse.CustomSource.Example.Tests --filter "FullyQualifiedName~DiscoveryTests"`
Expected: build FAILS — `ExampleConfig`, `ScopeConfig` and `Program` do not exist.

- [ ] **Step 5: Write configuration, auth, models, endpoints and wiring**

`samples/Connapse.CustomSource.Example/ExampleConfig.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connapse.CustomSource.Example;

/// <summary>The folder holding <c>example.json</c>, <c>links.json</c> and <c>scopes/</c>.</summary>
public sealed record ExampleDataDirectory(string Path);

public sealed record ScopeConfig(string Id, string Name, string? Description);

/// <summary>
/// Everything the example "target system" knows, read from <c>example.json</c>. Users are plain names;
/// grants are the access keys each user holds; denies are document paths (<c>scope/relative/path</c>) a
/// user may not read even though they hold the document's key — the per-document exceptions that only the
/// check can express.
/// </summary>
public sealed record ExampleConfig
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public required string Name { get; init; }
    public required string Token { get; init; }
    public int PageSize { get; init; } = 100;
    public bool PermissionsEnabled { get; init; } = true;
    public int MaxCheckBatch { get; init; } = 100;
    public string? LinkSecret { get; init; }
    public string? ConnapseIssuer { get; init; }
    public string? ProviderId { get; init; }
    public string? PublicBaseUrl { get; init; }
    public List<ScopeConfig> Scopes { get; init; } = [];
    public Dictionary<string, List<string>> Grants { get; init; } = [];
    public Dictionary<string, List<string>> Denies { get; init; } = [];
    public Dictionary<string, string> PresetLinks { get; init; } = [];
    public List<string> Users { get; init; } = [];

    public bool HasScope(string scopeId) => Scopes.Any(s => s.Id == scopeId);

    public static ExampleConfig Load(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, "example.json");
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ExampleConfig>(stream, JsonOptions)
            ?? throw new InvalidDataException($"{path} is empty.");
    }
}
```

`samples/Connapse.CustomSource.Example/BearerTokenFilter.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Connapse.CustomSource.Example;

/// <summary>Refuses any request whose bearer token is not the configured one.</summary>
public sealed class BearerTokenFilter(ExampleConfig config) : IEndpointFilter
{
    private const string Prefix = "Bearer ";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        string header = context.HttpContext.Request.Headers.Authorization.ToString();
        bool valid = header.StartsWith(Prefix, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(header[Prefix.Length..]),
                Encoding.UTF8.GetBytes(config.Token));

        return valid
            ? await next(context)
            : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Missing or wrong bearer token.");
    }
}
```

`samples/Connapse.CustomSource.Example/ProtocolModels.cs`:

```csharp
namespace Connapse.CustomSource.Example;

public sealed record DiscoveryDocument(IReadOnlyList<string> ProtocolVersions, string Name, Capabilities Capabilities);

public sealed record Capabilities(
    bool Changes,
    bool Tombstones,
    int MinSyncIntervalSeconds,
    int MaxPageSize,
    PermissionCapabilities? Permissions);

public sealed record PermissionCapabilities(CheckCapability Check, AccessKeysCapability AccessKeys, LinkingCapability? Linking);

public sealed record CheckCapability(int MaxBatch);

public sealed record AccessKeysCapability(int CacheTtlSeconds);

public sealed record LinkingCapability(string LinkUrl);

public sealed record ScopeItem(string Id, string Name, string? Description);

public sealed record ScopePage(IReadOnlyList<ScopeItem> Items, string? NextPageToken);
```

`samples/Connapse.CustomSource.Example/ContentEndpoints.cs`:

```csharp
namespace Connapse.CustomSource.Example;

public static class ContentEndpoints
{
    public static void MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/connapse-source", Discovery).AddEndpointFilter<BearerTokenFilter>();

        RouteGroupBuilder scopes = app.MapGroup("/v1/scopes").AddEndpointFilter<BearerTokenFilter>();
        scopes.MapGet("", ListScopes);
    }

    private static IResult Discovery(HttpRequest request, ExampleConfig config)
    {
        string baseUrl = config.PublicBaseUrl ?? $"{request.Scheme}://{request.Host}";
        PermissionCapabilities? permissions = config.PermissionsEnabled
            ? new PermissionCapabilities(
                new CheckCapability(config.MaxCheckBatch),
                new AccessKeysCapability(0),
                config.LinkSecret is null ? null : new LinkingCapability($"{baseUrl.TrimEnd('/')}/link"))
            : null;

        return Results.Ok(new DiscoveryDocument(
            ["1"],
            config.Name,
            new Capabilities(Changes: true, Tombstones: true, MinSyncIntervalSeconds: 60, config.PageSize, permissions)));
    }

    private static IResult ListScopes(ExampleConfig config) =>
        Results.Ok(new ScopePage(config.Scopes.Select(s => new ScopeItem(s.Id, s.Name, s.Description)).ToList(), null));
}
```

`samples/Connapse.CustomSource.Example/Program.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.CustomSource.Example;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddProblemDetails();

// Resolved lazily so a test host's DataDirectory setting is visible.
builder.Services.AddSingleton(sp => new ExampleDataDirectory(
    sp.GetRequiredService<IConfiguration>()["DataDirectory"]
    ?? Path.Combine(AppContext.BaseDirectory, "sample-data")));
builder.Services.AddSingleton(sp => ExampleConfig.Load(sp.GetRequiredService<ExampleDataDirectory>().Path));

WebApplication app = builder.Build();

app.MapContentEndpoints();

app.Run();

public partial class Program;
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Connapse.CustomSource.Example.Tests --filter "FullyQualifiedName~DiscoveryTests"`
Expected: PASS (5 tests). `Scopes_ListsConfiguredScopes` must pass against `GET /v1/scopes` with no trailing slash; if the empty group route does not match it, map `ListScopes` with `app.MapGet("/v1/scopes", ListScopes).AddEndpointFilter<BearerTokenFilter>()` instead.

- [ ] **Step 7: Commit**

```bash
git add Connapse.slnx samples/Connapse.CustomSource.Example/Connapse.CustomSource.Example.csproj samples/Connapse.CustomSource.Example/Program.cs samples/Connapse.CustomSource.Example/ExampleConfig.cs samples/Connapse.CustomSource.Example/BearerTokenFilter.cs samples/Connapse.CustomSource.Example/ProtocolModels.cs samples/Connapse.CustomSource.Example/ContentEndpoints.cs tests/Connapse.CustomSource.Example.Tests/Connapse.CustomSource.Example.Tests.csproj tests/Connapse.CustomSource.Example.Tests/ExampleServer.cs tests/Connapse.CustomSource.Example.Tests/DiscoveryTests.cs
git commit -m "feat: example custom source server with discovery and scopes (#738)"
```

---

### Task 3: Folder catalog, full listing and content

**Files:**
- Create: `samples/Connapse.CustomSource.Example/FolderCatalog.cs`
- Create: `samples/Connapse.CustomSource.Example/ListingToken.cs`
- Modify: `samples/Connapse.CustomSource.Example/ProtocolModels.cs` (append listing DTOs)
- Modify: `samples/Connapse.CustomSource.Example/ContentEndpoints.cs` (listing and content routes)
- Modify: `samples/Connapse.CustomSource.Example/Program.cs` (register `FolderCatalog`, and in Task 4 `ChangeJournal`)
- Test: `tests/Connapse.CustomSource.Example.Tests/DocumentListingTests.cs`

**Interfaces:**
- Consumes: `ExampleConfig`, `ExampleDataDirectory`, `BearerTokenFilter`, `ExampleServer`, `DiscoveryTests.GetJson`.
- Produces: `ExampleDocument(string Id, string ScopeId, string Path, string FullPath, string ContentType, long Size, string Version, DateTimeOffset ModifiedAt, IReadOnlyList<string> AccessKeys, string AccessVersion)`; `FolderCatalog.List(string scopeId)`, `FolderCatalog.Find(string scopeId, string id)`, static `FolderCatalog.AccessKey(string scopeId, string relativePath)`; `DocumentId.Encode(string)`, `DocumentId.TryDecode(string, out string)`; `DocumentItem.From(ExampleDocument, bool includeAccess)`; `ListingToken.Format(int, string)`, `ListingToken.TryParse(string, out int, out string)`. Task 4 adds `ChangeJournal.HeadCursor(string scopeId)`, which the listing calls; in this task the listing calls it too, so Step 5 of this task creates a minimal `ChangeJournal` with `HeadCursor` only, which Task 4 completes.

- [ ] **Step 1: Write the failing tests**

`tests/Connapse.CustomSource.Example.Tests/DocumentListingTests.cs`:

```csharp
using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
public class DocumentListingTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["eng/README.md"] = "# Engineering",
        ["eng/design/architecture.md"] = "# Architecture",
        ["eng/design/secret.md"] = "# Secret",
        ["ops/runbook.md"] = "# Runbook",
    };

    [Fact]
    public async Task Listing_PagesThroughEveryDocument_AndEndsWithChangeCursor()
    {
        using ExampleServer server = new(files: Files);

        JsonElement first = await DiscoveryTests.GetJson(server, "/v1/scopes/eng/documents");
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        first.TryGetProperty("change_cursor", out _).Should().BeFalse();
        string token = first.GetProperty("next_page_token").GetString()!;

        JsonElement second = await DiscoveryTests.GetJson(server, $"/v1/scopes/eng/documents?page_token={Uri.EscapeDataString(token)}");
        second.GetProperty("items").GetArrayLength().Should().Be(1);
        second.TryGetProperty("next_page_token", out _).Should().BeFalse();
        second.GetProperty("change_cursor").GetString().Should().NotBeNullOrEmpty();

        first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("path").GetString())
            .Should().BeEquivalentTo("README.md", "design/architecture.md", "design/secret.md");
    }

    [Fact]
    public async Task Listing_AttachesOneAccessKeyPerTopLevelFolder()
    {
        using ExampleServer server = new(ExampleServer.DefaultConfig() with { PageSize = 10 }, Files);

        JsonElement root = await DiscoveryTests.GetJson(server, "/v1/scopes/eng/documents");

        Dictionary<string, string> keys = root.GetProperty("items").EnumerateArray().ToDictionary(
            i => i.GetProperty("path").GetString()!,
            i => i.GetProperty("access_keys")[0].GetString()!);
        keys["README.md"].Should().Be("folder:eng/");
        keys["design/architecture.md"].Should().Be("folder:eng/design");
        root.GetProperty("items")[0].GetProperty("access_version").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Listing_ContentOnlyConfig_OmitsAccessKeys()
    {
        using ExampleServer server = new(ExampleServer.DefaultConfig() with { PermissionsEnabled = false }, Files);

        JsonElement root = await DiscoveryTests.GetJson(server, "/v1/scopes/eng/documents");

        root.GetProperty("items")[0].TryGetProperty("access_keys", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Listing_UnknownScope_Returns404()
    {
        using ExampleServer server = new(files: Files);

        HttpResponseMessage response = await server.Client().GetAsync("/v1/scopes/nope/documents");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Listing_MalformedPageToken_Returns400()
    {
        using ExampleServer server = new(files: Files);

        HttpResponseMessage response = await server.Client().GetAsync("/v1/scopes/eng/documents?page_token=%%%");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Content_ReturnsBytesWithETag_And304WhenUnchanged()
    {
        using ExampleServer server = new(files: Files);
        HttpClient client = server.Client();
        string id = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("README.md"));

        HttpResponseMessage response = await client.GetAsync($"/v1/scopes/eng/documents/{id}/content");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("# Engineering");
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        string etag = response.Headers.ETag!.Tag;

        HttpRequestMessage again = new(HttpMethod.Get, $"/v1/scopes/eng/documents/{id}/content");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        (await client.SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Theory]
    [InlineData("../ops/runbook.md")]
    [InlineData("/etc/passwd")]
    [InlineData("design/../../ops/runbook.md")]
    public async Task Content_PathOutsideScope_Returns404(string relativePath)
    {
        using ExampleServer server = new(files: Files);
        string id = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(relativePath));

        HttpResponseMessage response = await server.Client().GetAsync($"/v1/scopes/eng/documents/{id}/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Content_IdThatIsNotBase64Url_Returns404()
    {
        using ExampleServer server = new(files: Files);

        HttpResponseMessage response = await server.Client().GetAsync("/v1/scopes/eng/documents/not*base64/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Connapse.CustomSource.Example.Tests --filter "FullyQualifiedName~DocumentListingTests"`
Expected: FAIL — listing and content routes return 404 for every request.

- [ ] **Step 3: Write the catalog and listing token**

`samples/Connapse.CustomSource.Example/FolderCatalog.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Connapse.CustomSource.Example;

public sealed record ExampleDocument(
    string Id,
    string ScopeId,
    string Path,
    string FullPath,
    string ContentType,
    long Size,
    string Version,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<string> AccessKeys,
    string AccessVersion);

/// <summary>Document IDs are the base64url of the path inside the scope, so they are safe in a URL segment.</summary>
public static class DocumentId
{
    public static string Encode(string relativePath) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(relativePath));

    public static bool TryDecode(string id, out string relativePath)
    {
        relativePath = "";
        if (!Base64Url.IsValid(id)) return false;
        relativePath = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(id));
        return relativePath.Length > 0;
    }
}

/// <summary>Reads <c>scopes/{scopeId}/</c> — the stand-in for the target system's content.</summary>
public sealed class FolderCatalog(ExampleDataDirectory data)
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
    };

    public IReadOnlyList<ExampleDocument> List(string scopeId)
    {
        string root = ScopeRoot(scopeId);
        if (!Directory.Exists(root)) return [];

        return Directory.EnumerateFiles(root, "*", Walk)
            .Select(file => Describe(scopeId, root, file))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Null when the ID is malformed, points outside the scope, or names no file.</summary>
    public ExampleDocument? Find(string scopeId, string id)
    {
        if (!DocumentId.TryDecode(id, out string relativePath) || System.IO.Path.IsPathRooted(relativePath))
            return null;

        string root = System.IO.Path.GetFullPath(ScopeRoot(scopeId));
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relativePath));
        bool inside = full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);

        return inside && File.Exists(full) ? Describe(scopeId, root, full) : null;
    }

    /// <summary>One key per top-level folder; files at the scope root share <c>folder:{scope}/</c>.</summary>
    public static string AccessKey(string scopeId, string relativePath)
    {
        int slash = relativePath.IndexOf('/');
        return slash < 0 ? $"folder:{scopeId}/" : $"folder:{scopeId}/{relativePath[..slash]}";
    }

    private string ScopeRoot(string scopeId) => System.IO.Path.Combine(data.Path, "scopes", scopeId);

    private static ExampleDocument Describe(string scopeId, string root, string file)
    {
        string relativePath = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
        FileInfo info = new(file);
        IReadOnlyList<string> keys = [AccessKey(scopeId, relativePath)];

        return new ExampleDocument(
            DocumentId.Encode(relativePath),
            scopeId,
            relativePath,
            info.FullName,
            ContentTypeOf(relativePath),
            info.Length,
            Hash(File.ReadAllBytes(file), 16),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            keys,
            Hash(Encoding.UTF8.GetBytes(string.Join('\n', keys)), 8));
    }

    private static string Hash(byte[] bytes, int length) =>
        Convert.ToHexString(SHA256.HashData(bytes))[..length].ToLowerInvariant();

    private static string ContentTypeOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        ".html" or ".htm" => "text/html",
        ".json" => "application/json",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };
}
```

`samples/Connapse.CustomSource.Example/ListingToken.cs`:

```csharp
using System.Buffers.Text;
using System.Text;

namespace Connapse.CustomSource.Example;

/// <summary>
/// A listing page token carries the offset of the next page and the change cursor taken before the first
/// page, so the cursor the last page returns covers every change made while the listing was read.
/// </summary>
public static class ListingToken
{
    public static string Format(int offset, string cursor) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{offset}|{cursor}"));

    public static bool TryParse(string token, out int offset, out string cursor)
    {
        offset = 0;
        cursor = "";
        if (!Base64Url.IsValid(token)) return false;

        string text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
        int bar = text.IndexOf('|');
        if (bar <= 0 || !int.TryParse(text[..bar], out offset) || offset < 0) return false;

        cursor = text[(bar + 1)..];
        return cursor.Length > 0;
    }
}
```

- [ ] **Step 4: Append the listing DTOs**

Append to `samples/Connapse.CustomSource.Example/ProtocolModels.cs`:

```csharp
public sealed record DocumentItem(
    string Id,
    string Path,
    string Title,
    string ContentType,
    long Size,
    string Version,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<string>? AccessKeys,
    string? AccessVersion)
{
    public static DocumentItem From(ExampleDocument d, bool includeAccess) => new(
        d.Id,
        d.Path,
        System.IO.Path.GetFileNameWithoutExtension(d.Path),
        d.ContentType,
        d.Size,
        d.Version,
        d.ModifiedAt,
        includeAccess ? d.AccessKeys : null,
        includeAccess ? d.AccessVersion : null);
}

public sealed record DocumentPage(IReadOnlyList<DocumentItem> Items, string? NextPageToken, string? ChangeCursor);
```

- [ ] **Step 5: Add a minimal change journal, the routes, and registrations**

Create `samples/Connapse.CustomSource.Example/ChangeJournal.cs` (Task 4 replaces it with the full version):

```csharp
using System.Security.Cryptography;

namespace Connapse.CustomSource.Example;

public sealed class ChangeJournal
{
    private readonly string _epoch = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    public string HeadCursor(string scopeId) => $"{_epoch}.0";
}
```

In `ContentEndpoints.MapContentEndpoints`, after `scopes.MapGet("", ListScopes);` add:

```csharp
        scopes.MapGet("/{scope}/documents", ListDocuments);
        scopes.MapGet("/{scope}/documents/{id}/content", GetContent);
```

Add to the top of `ContentEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
```

Add these methods to `ContentEndpoints`:

```csharp
    private static IResult ListDocuments(
        string scope,
        [FromQuery(Name = "page_token")] string? pageToken,
        ExampleConfig config,
        FolderCatalog catalog,
        ChangeJournal journal)
    {
        if (!config.HasScope(scope)) return UnknownScope();

        int offset = 0;
        string cursor;
        if (string.IsNullOrEmpty(pageToken))
            cursor = journal.HeadCursor(scope);
        else if (!ListingToken.TryParse(pageToken, out offset, out cursor))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed page_token.");

        IReadOnlyList<ExampleDocument> all = catalog.List(scope);
        List<DocumentItem> page = all.Skip(offset).Take(config.PageSize)
            .Select(d => DocumentItem.From(d, config.PermissionsEnabled))
            .ToList();
        int next = offset + page.Count;

        return next < all.Count
            ? Results.Ok(new DocumentPage(page, ListingToken.Format(next, cursor), null))
            : Results.Ok(new DocumentPage(page, null, cursor));
    }

    private static IResult GetContent(string scope, string id, HttpContext http, ExampleConfig config, FolderCatalog catalog)
    {
        if (!config.HasScope(scope)) return UnknownScope();

        ExampleDocument? document = catalog.Find(scope, id);
        if (document is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such document.");

        string etag = $"\"{document.Version}\"";
        if (http.Request.Headers.IfNoneMatch.ToString() == etag)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        http.Response.Headers.ETag = etag;
        return Results.Stream(File.OpenRead(document.FullPath), document.ContentType);
    }

    private static IResult UnknownScope() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown scope.");
```

In `Program.cs`, after the `ExampleConfig` registration add:

```csharp
builder.Services.AddSingleton<FolderCatalog>();
builder.Services.AddSingleton<ChangeJournal>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Connapse.CustomSource.Example.Tests`
Expected: PASS (all DiscoveryTests and DocumentListingTests).

- [ ] **Step 7: Commit**

```bash
git add samples/Connapse.CustomSource.Example/FolderCatalog.cs samples/Connapse.CustomSource.Example/ListingToken.cs samples/Connapse.CustomSource.Example/ChangeJournal.cs samples/Connapse.CustomSource.Example/ProtocolModels.cs samples/Connapse.CustomSource.Example/ContentEndpoints.cs samples/Connapse.CustomSource.Example/Program.cs tests/Connapse.CustomSource.Example.Tests/DocumentListingTests.cs
git commit -m "feat: example server full listing and content (#738)"
```

---

### Task 4: Change feed

**Files:**
- Modify: `samples/Connapse.CustomSource.Example/ChangeJournal.cs` (replace whole file)
- Modify: `samples/Connapse.CustomSource.Example/ProtocolModels.cs` (append change DTOs)
- Modify: `samples/Connapse.CustomSource.Example/ContentEndpoints.cs` (changes route)
- Test: `tests/Connapse.CustomSource.Example.Tests/ChangeFeedTests.cs`

**Interfaces:**
- Consumes: `FolderCatalog.List/Find`, `DocumentItem.From`, `ExampleConfig.PageSize`.
- Produces: `ChangeJournal(FolderCatalog, ExampleConfig)` with `HeadCursor(string scopeId)` and `Read(string scopeId, string cursor)` returning `ChangeWindow?` (null means 410); `JournalEntry(string Id, bool Deleted)`; `ChangeWindow(IReadOnlyList<JournalEntry> Entries, string? NextPageToken, string? NextCursor)`.

- [ ] **Step 1: Write the failing tests**

`tests/Connapse.CustomSource.Example.Tests/ChangeFeedTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
public class ChangeFeedTests
{
    private static ExampleServer NewServer() => new(files: new Dictionary<string, string>
    {
        ["eng/README.md"] = "# Engineering",
        ["eng/design/secret.md"] = "# Secret",
    });

    [Fact]
    public async Task Changes_AfterListing_ReportAddChangeAndDelete()
    {
        using ExampleServer server = NewServer();
        string cursor = await ListToEnd(server, "eng");

        server.WriteFile("eng/new.md", "# New");
        server.WriteFile("eng/README.md", "# Engineering, edited");
        server.DeleteFile("eng/design/secret.md");

        JsonElement page = await DiscoveryTests.GetJson(server, $"/v1/scopes/eng/changes?cursor={Uri.EscapeDataString(cursor)}");

        Dictionary<string, string> ops = page.GetProperty("items").EnumerateArray().ToDictionary(
            i => i.TryGetProperty("path", out JsonElement p) ? p.GetString()! : "deleted",
            i => i.GetProperty("op").GetString()!);
        ops.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["new.md"] = "upsert",
            ["README.md"] = "upsert",
            ["deleted"] = "delete",
        });
        string next = page.GetProperty("next_cursor").GetString()!;

        JsonElement quiet = await DiscoveryTests.GetJson(server, $"/v1/scopes/eng/changes?cursor={Uri.EscapeDataString(next)}");
        quiet.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Changes_MoreThanOnePage_UsePageTokenThenNextCursor()
    {
        using ExampleServer server = NewServer();
        string cursor = await ListToEnd(server, "eng");
        server.WriteFile("eng/a.md", "a");
        server.WriteFile("eng/b.md", "b");
        server.WriteFile("eng/c.md", "c");

        JsonElement first = await DiscoveryTests.GetJson(server, $"/v1/scopes/eng/changes?cursor={Uri.EscapeDataString(cursor)}");
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        first.TryGetProperty("next_cursor", out _).Should().BeFalse();
        string token = first.GetProperty("next_page_token").GetString()!;

        JsonElement second = await DiscoveryTests.GetJson(server, $"/v1/scopes/eng/changes?page_token={Uri.EscapeDataString(token)}");
        second.GetProperty("items").GetArrayLength().Should().Be(1);
        second.GetProperty("next_cursor").GetString().Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("deadbeef.0")]
    [InlineData("garbage")]
    public async Task Changes_CursorNotIssuedByThisServer_Returns410(string cursor)
    {
        using ExampleServer server = NewServer();
        await ListToEnd(server, "eng");

        HttpResponseMessage response = await server.Client().GetAsync($"/v1/scopes/eng/changes?cursor={cursor}");

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Changes_CursorBeyondTheHead_Returns410()
    {
        using ExampleServer server = NewServer();
        string cursor = await ListToEnd(server, "eng");
        string epoch = cursor[..cursor.IndexOf('.')];

        HttpResponseMessage response = await server.Client().GetAsync($"/v1/scopes/eng/changes?cursor={epoch}.999");

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Changes_WithoutCursor_Returns400()
    {
        using ExampleServer server = NewServer();

        HttpResponseMessage response = await server.Client().GetAsync("/v1/scopes/eng/changes");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<string> ListToEnd(ExampleServer server, string scope)
    {
        string url = $"/v1/scopes/{scope}/documents";
        while (true)
        {
            JsonElement page = await DiscoveryTests.GetJson(server, url);
            if (page.TryGetProperty("change_cursor", out JsonElement cursor))
                return cursor.GetString()!;
            url = $"/v1/scopes/{scope}/documents?page_token={Uri.EscapeDataString(page.GetProperty("next_page_token").GetString()!)}";
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Connapse.CustomSource.Example.Tests --filter "FullyQualifiedName~ChangeFeedTests"`
Expected: FAIL — `/changes` returns 404.

- [ ] **Step 3: Replace the change journal**

Replace `samples/Connapse.CustomSource.Example/ChangeJournal.cs` with:

```csharp
using System.Security.Cryptography;

namespace Connapse.CustomSource.Example;

public sealed record JournalEntry(string Id, bool Deleted);

/// <summary>One page of the journal: either more pages follow (<c>NextPageToken</c>) or this is the last (<c>NextCursor</c>).</summary>
public sealed record ChangeWindow(IReadOnlyList<JournalEntry> Entries, string? NextPageToken, string? NextCursor);

/// <summary>
/// The example system has no change log of its own, so this builds one: each call rescans the scope, diffs
/// it against the last snapshot, and appends what changed. A cursor is <c>{epoch}.{sequence}</c>; the epoch
/// is new on every start, so a cursor from before a restart is refused with 410 and Connapse re-lists.
/// </summary>
public sealed class ChangeJournal(FolderCatalog catalog, ExampleConfig config)
{
    private readonly string _epoch = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JournalEntry>> _entries = new(StringComparer.Ordinal);

    public string HeadCursor(string scopeId)
    {
        lock (_gate)
        {
            Refresh(scopeId);
            return Format(_entries[scopeId].Count);
        }
    }

    /// <summary>Null when the cursor was not issued by this process; the caller answers 410.</summary>
    public ChangeWindow? Read(string scopeId, string cursor)
    {
        lock (_gate)
        {
            Refresh(scopeId);
            List<JournalEntry> entries = _entries[scopeId];
            if (!TryParse(cursor, out int sequence) || sequence > entries.Count) return null;

            int end = Math.Min(sequence + config.PageSize, entries.Count);
            List<JournalEntry> slice = entries.GetRange(sequence, end - sequence);
            return end < entries.Count
                ? new ChangeWindow(slice, Format(end), null)
                : new ChangeWindow(slice, null, Format(end));
        }
    }

    private void Refresh(string scopeId)
    {
        Dictionary<string, string> current = catalog.List(scopeId)
            .ToDictionary(d => d.Id, d => $"{d.Version}|{d.AccessVersion}", StringComparer.Ordinal);

        if (!_snapshots.TryGetValue(scopeId, out Dictionary<string, string>? previous))
        {
            _snapshots[scopeId] = current;
            _entries[scopeId] = [];
            return;
        }

        List<JournalEntry> entries = _entries[scopeId];
        foreach ((string id, string stamp) in current.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!previous.TryGetValue(id, out string? old) || old != stamp)
                entries.Add(new JournalEntry(id, Deleted: false));
        }
        foreach (string id in previous.Keys.Where(k => !current.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            entries.Add(new JournalEntry(id, Deleted: true));

        _snapshots[scopeId] = current;
    }

    private string Format(int sequence) => $"{_epoch}.{sequence}";

    private bool TryParse(string cursor, out int sequence)
    {
        sequence = 0;
        int dot = cursor.IndexOf('.');
        return dot > 0
            && cursor[..dot] == _epoch
            && int.TryParse(cursor[(dot + 1)..], out sequence)
            && sequence >= 0;
    }
}
```

- [ ] **Step 4: Append the change DTOs**

Append to `samples/Connapse.CustomSource.Example/ProtocolModels.cs`:

```csharp
public sealed record ChangeItem(
    string Id,
    string Op,
    string? Path = null,
    string? Title = null,
    string? ContentType = null,
    long? Size = null,
    string? Version = null,
    DateTimeOffset? ModifiedAt = null,
    IReadOnlyList<string>? AccessKeys = null,
    string? AccessVersion = null)
{
    public static ChangeItem Delete(string id) => new(id, "delete");

    public static ChangeItem Upsert(DocumentItem d) => new(
        d.Id, "upsert", d.Path, d.Title, d.ContentType, d.Size, d.Version, d.ModifiedAt, d.AccessKeys, d.AccessVersion);
}

public sealed record ChangePage(IReadOnlyList<ChangeItem> Items, string? NextPageToken, string? NextCursor);
```

- [ ] **Step 5: Add the changes route**

In `ContentEndpoints.MapContentEndpoints`, after the listing route add:

```csharp
        scopes.MapGet("/{scope}/changes", ListChanges);
```

Add to `ContentEndpoints`:

```csharp
    private static IResult ListChanges(
        string scope,
        [FromQuery] string? cursor,
        [FromQuery(Name = "page_token")] string? pageToken,
        ExampleConfig config,
        FolderCatalog catalog,
        ChangeJournal journal)
    {
        if (!config.HasScope(scope)) return UnknownScope();

        string? from = string.IsNullOrEmpty(pageToken) ? cursor : pageToken;
        if (string.IsNullOrEmpty(from))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "cursor or page_token is required.");

        ChangeWindow? window = journal.Read(scope, from);
        if (window is null)
            return Results.Problem(statusCode: StatusCodes.Status410Gone, title: "This cursor is no longer valid; run a full listing.");

        // The journal records IDs; read the document now, so an upsert for a file deleted since becomes a delete.
        List<ChangeItem> items = window.Entries
            .Select(e => e.Deleted || catalog.Find(scope, e.Id) is not { } document
                ? ChangeItem.Delete(e.Id)
                : ChangeItem.Upsert(DocumentItem.From(document, config.PermissionsEnabled)))
            .ToList();

        return Results.Ok(new ChangePage(items, window.NextPageToken, window.NextCursor));
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Connapse.CustomSource.Example.Tests`
Expected: PASS (Discovery, DocumentListing and ChangeFeed tests).

- [ ] **Step 7: Commit and open PR B (Tasks 2–4)**

```bash
git add samples/Connapse.CustomSource.Example/ChangeJournal.cs samples/Connapse.CustomSource.Example/ProtocolModels.cs samples/Connapse.CustomSource.Example/ContentEndpoints.cs tests/Connapse.CustomSource.Example.Tests/ChangeFeedTests.cs
git commit -m "feat: example server change feed with 410 on unknown cursors (#738)"
```

---

### Task 5: Permission endpoints — access keys and batched check

**Files:**
- Create: `samples/Connapse.CustomSource.Example/AuthZenModels.cs`
- Create: `samples/Connapse.CustomSource.Example/AccessPolicy.cs`
- Create: `samples/Connapse.CustomSource.Example/LinkStore.cs` (links only; Task 6 adds pending links and sessions)
- Create: `samples/Connapse.CustomSource.Example/PermissionEndpoints.cs`
- Modify: `samples/Connapse.CustomSource.Example/Program.cs`
- Test: `tests/Connapse.CustomSource.Example.Tests/PermissionTests.cs`

**Interfaces:**
- Consumes: `FolderCatalog.Find`, `ExampleConfig.Grants/Denies/PresetLinks/MaxCheckBatch/HasScope`.
- Produces: `LinkStore(ExampleDataDirectory, ExampleConfig, TimeProvider)` with `ExternalUser(string connapseUserId)` and `Remove(string connapseUserId)`; `AccessPolicy.KeysFor(string connapseUserId, string scopeId)`, `AccessPolicy.CanRead(string connapseUserId, string scopeId, string documentId)`; `PermissionEndpoints.MapPermissionEndpoints(this IEndpointRouteBuilder)`.

- [ ] **Step 1: Write the failing tests**

`tests/Connapse.CustomSource.Example.Tests/PermissionTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
public class PermissionTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["eng/README.md"] = "# Engineering",
        ["eng/design/architecture.md"] = "# Architecture",
        ["eng/design/secret.md"] = "# Secret",
    };

    [Fact]
    public async Task SearchResource_LinkedUser_ReturnsTheirKeysInThatScope()
    {
        using ExampleServer server = new(files: Files);

        JsonElement root = await PostJson(server, "/access/v1/search/resource", KeySearch(ExampleServer.AliceId, "eng"));

        root.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("id").GetString())
            .Should().Equal("folder:eng/", "folder:eng/design");
        root.GetProperty("results")[0].GetProperty("type").GetString().Should().Be("access_key");
        root.GetProperty("page").GetProperty("next_token").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task SearchResource_LimitOne_PagesWithNextToken()
    {
        using ExampleServer server = new(files: Files);

        JsonElement first = await PostJson(server, "/access/v1/search/resource", KeySearch(ExampleServer.AliceId, "eng", limit: 1));
        string token = first.GetProperty("page").GetProperty("next_token").GetString()!;
        JsonElement second = await PostJson(server, "/access/v1/search/resource", KeySearch(ExampleServer.AliceId, "eng", token, 1));

        first.GetProperty("results")[0].GetProperty("id").GetString().Should().Be("folder:eng/");
        token.Should().NotBeEmpty();
        second.GetProperty("results")[0].GetProperty("id").GetString().Should().Be("folder:eng/design");
        second.GetProperty("page").GetProperty("next_token").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task SearchResource_UnlinkedUser_ReturnsNoKeys()
    {
        using ExampleServer server = new(files: Files);

        JsonElement root = await PostJson(server, "/access/v1/search/resource", KeySearch("connapse-stranger", "eng"));

        root.GetProperty("results").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task SearchResource_WrongResourceType_Returns400()
    {
        using ExampleServer server = new(files: Files);
        object body = new
        {
            subject = new { type = "user", id = ExampleServer.AliceId },
            action = new { name = "read" },
            resource = new { type = "document", properties = new { scope = "eng" } },
        };

        HttpResponseMessage response = await server.Client().PostAsJsonAsync("/access/v1/search/resource", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Evaluations_ReturnOneDecisionPerItemInOrder()
    {
        using ExampleServer server = new(files: Files);

        JsonElement root = await PostJson(server, "/access/v1/evaluations",
            Check(ExampleServer.AliceId, "eng", "README.md", "design/architecture.md", "design/secret.md"));

        Decisions(root).Should().Equal(true, true, false);
    }

    [Fact]
    public async Task Evaluations_UserWithoutTheKey_IsDenied()
    {
        using ExampleServer server = new(files: Files);

        JsonElement root = await PostJson(server, "/access/v1/evaluations",
            Check(ExampleServer.BobId, "eng", "README.md", "design/architecture.md"));

        Decisions(root).Should().Equal(true, false);
    }

    [Fact]
    public async Task Evaluations_UnknownDocumentWrongTypeOrUnlinkedUser_AreFalse()
    {
        using ExampleServer server = new(files: Files);
        object body = new
        {
            subject = new { type = "user", id = ExampleServer.AliceId },
            action = new { name = "read" },
            evaluations = new object[]
            {
                new { resource = new { type = "document", id = DocumentId.Encode("missing.md"), properties = new { scope = "eng" } } },
                new { resource = new { type = "folder", id = DocumentId.Encode("README.md"), properties = new { scope = "eng" } } },
                new { resource = new { type = "document", id = DocumentId.Encode("README.md") } },
            },
        };

        JsonElement mixed = await PostJson(server, "/access/v1/evaluations", body);
        JsonElement stranger = await PostJson(server, "/access/v1/evaluations", Check("connapse-stranger", "eng", "README.md"));

        Decisions(mixed).Should().Equal(false, false, false);
        Decisions(stranger).Should().Equal(false);
    }

    [Fact]
    public async Task Evaluations_OverTheBatchLimit_Returns400()
    {
        using ExampleServer server = new(ExampleServer.DefaultConfig() with { MaxCheckBatch = 2 }, Files);

        HttpResponseMessage response = await server.Client().PostAsJsonAsync("/access/v1/evaluations",
            Check(ExampleServer.AliceId, "eng", "README.md", "design/architecture.md", "design/secret.md"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Permissions_ContentOnlyConfig_Return404()
    {
        using ExampleServer server = new(ExampleServer.DefaultConfig() with { PermissionsEnabled = false }, Files);

        HttpResponseMessage response = await server.Client().PostAsJsonAsync("/access/v1/evaluations",
            Check(ExampleServer.AliceId, "eng", "README.md"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Permissions_WithoutToken_Return401()
    {
        using ExampleServer server = new(files: Files);

        HttpResponseMessage response = await server.Client(authorized: false).PostAsJsonAsync("/access/v1/evaluations",
            Check(ExampleServer.AliceId, "eng", "README.md"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    internal static object KeySearch(string userId, string scope, string token = "", int limit = 100) => new
    {
        subject = new { type = "user", id = userId },
        action = new { name = "read" },
        resource = new { type = "access_key", properties = new { scope } },
        page = new { token, limit },
    };

    private static object Check(string userId, string scope, params string[] paths) => new
    {
        subject = new { type = "user", id = userId },
        action = new { name = "read" },
        evaluations = paths.Select(p => new
        {
            resource = new { type = "document", id = DocumentId.Encode(p), properties = new { scope } },
        }).ToArray(),
    };

    private static IEnumerable<bool> Decisions(JsonElement root) =>
        root.GetProperty("evaluations").EnumerateArray().Select(e => e.GetProperty("decision").GetBoolean());

    internal static async Task<JsonElement> PostJson(ExampleServer server, string url, object body)
    {
        HttpResponseMessage response = await server.Client().PostAsJsonAsync(url, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Connapse.CustomSource.Example.Tests --filter "FullyQualifiedName~PermissionTests"`
Expected: FAIL — the permission routes return 404 (and the content-only and no-token tests may pass by accident; the others fail).

- [ ] **Step 3: Write the AuthZEN models**

`samples/Connapse.CustomSource.Example/AuthZenModels.cs`:

```csharp
namespace Connapse.CustomSource.Example;

public sealed record AuthZenSubject(string? Type, string? Id);

public sealed record AuthZenAction(string? Name);

public sealed record ResourceProperties(string? Scope);

public sealed record AuthZenResource(string? Type, string? Id, ResourceProperties? Properties);

public sealed record AuthZenPageRequest(string? Token, int? Limit);

public sealed record ResourceSearchRequest(
    AuthZenSubject? Subject,
    AuthZenAction? Action,
    AuthZenResource? Resource,
    AuthZenPageRequest? Page);

public sealed record ResourceRef(string Type, string Id);

public sealed record AuthZenPageResponse(string NextToken);

public sealed record ResourceSearchResponse(IReadOnlyList<ResourceRef> Results, AuthZenPageResponse Page);

public sealed record EvaluationItem(AuthZenResource? Resource);

public sealed record EvaluationsRequest(AuthZenSubject? Subject, AuthZenAction? Action, IReadOnlyList<EvaluationItem>? Evaluations);

public sealed record DecisionItem(bool Decision);

public sealed record EvaluationsResponse(IReadOnlyList<DecisionItem> Evaluations);
```

- [ ] **Step 4: Write the link store (links only) and the access policy**

`samples/Connapse.CustomSource.Example/LinkStore.cs`:

```csharp
using System.Text.Json;

namespace Connapse.CustomSource.Example;

/// <summary>
/// Which example-system user each Connapse user is linked to. This is the middle man's own storage —
/// Connapse keeps no copy — persisted to <c>links.json</c>, with <c>preset_links</c> from
/// <c>example.json</c> added at start.
/// </summary>
public sealed class LinkStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _links;

    public LinkStore(ExampleDataDirectory data, ExampleConfig config, TimeProvider time)
    {
        Time = time;
        _path = Path.Combine(data.Path, "links.json");
        _links = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? []
            : [];
        foreach ((string connapseUserId, string user) in config.PresetLinks)
            _links.TryAdd(connapseUserId, user);
    }

    private TimeProvider Time { get; }

    public string? ExternalUser(string connapseUserId)
    {
        lock (_gate) return _links.GetValueOrDefault(connapseUserId);
    }

    public void Remove(string connapseUserId)
    {
        lock (_gate)
        {
            if (_links.Remove(connapseUserId)) Save();
        }
    }

    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_links, ExampleConfig.JsonOptions));
}
```

`samples/Connapse.CustomSource.Example/AccessPolicy.cs`:

```csharp
namespace Connapse.CustomSource.Example;

/// <summary>The example system's own permission rules: keys a user holds, and per-document decisions.</summary>
public sealed class AccessPolicy(ExampleConfig config, LinkStore links, FolderCatalog catalog)
{
    public IReadOnlyList<string> KeysFor(string connapseUserId, string scopeId)
    {
        string? user = links.ExternalUser(connapseUserId);
        if (user is null || !config.Grants.TryGetValue(user, out List<string>? keys)) return [];

        string prefix = $"folder:{scopeId}/";
        return keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// True only when the user is linked, holds the document's key, and is not denied that document.
    /// Anything unknown is false.
    /// </summary>
    public bool CanRead(string connapseUserId, string scopeId, string documentId)
    {
        if (!config.HasScope(scopeId)) return false;

        string? user = links.ExternalUser(connapseUserId);
        if (user is null) return false;

        ExampleDocument? document = catalog.Find(scopeId, documentId);
        if (document is null) return false;

        IReadOnlyList<string> held = KeysFor(connapseUserId, scopeId);
        if (!document.AccessKeys.Any(k => held.Contains(k, StringComparer.Ordinal))) return false;

        return !(config.Denies.TryGetValue(user, out List<string>? denied)
            && denied.Contains($"{scopeId}/{document.Path}", StringComparer.Ordinal));
    }
}
```

- [ ] **Step 5: Write the permission endpoints and register services**

`samples/Connapse.CustomSource.Example/PermissionEndpoints.cs`:

```csharp
namespace Connapse.CustomSource.Example;

public static class PermissionEndpoints
{
    public static void MapPermissionEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder access = app.MapGroup("/access/v1").AddEndpointFilter<BearerTokenFilter>();
        access.MapPost("/search/resource", SearchResources);
        access.MapPost("/evaluations", Evaluate);
    }

    private static IResult SearchResources(ResourceSearchRequest request, ExampleConfig config, AccessPolicy policy)
    {
        if (!config.PermissionsEnabled) return Results.NotFound();
        if (!IsUserReading(request.Subject, request.Action) || request.Resource?.Type != "access_key")
            return BadRequest("Expected a user subject, the read action, and resource type access_key.");

        string? scope = request.Resource.Properties?.Scope;
        if (scope is null || !config.HasScope(scope))
            return BadRequest("resource.properties.scope must name a known scope.");

        int offset = 0;
        string token = request.Page?.Token ?? "";
        if (token.Length > 0 && (!int.TryParse(token, out offset) || offset < 0))
            return BadRequest("Malformed page.token.");

        int limit = Math.Clamp(request.Page?.Limit ?? config.PageSize, 1, config.PageSize);
        IReadOnlyList<string> keys = policy.KeysFor(request.Subject!.Id!, scope);
        List<ResourceRef> page = keys.Skip(offset).Take(limit).Select(k => new ResourceRef("access_key", k)).ToList();
        int next = offset + page.Count;

        return Results.Ok(new ResourceSearchResponse(page, new AuthZenPageResponse(next < keys.Count ? next.ToString() : "")));
    }

    private static IResult Evaluate(EvaluationsRequest request, ExampleConfig config, AccessPolicy policy)
    {
        if (!config.PermissionsEnabled) return Results.NotFound();
        if (!IsUserReading(request.Subject, request.Action) || request.Evaluations is null)
            return BadRequest("Expected a user subject, the read action, and an evaluations array.");
        if (request.Evaluations.Count > config.MaxCheckBatch)
            return BadRequest($"Too many evaluations; the limit is {config.MaxCheckBatch}.");

        string userId = request.Subject!.Id!;
        List<DecisionItem> decisions = request.Evaluations
            .Select(e => new DecisionItem(
                e.Resource is { Type: "document", Id: { Length: > 0 } id, Properties.Scope: { Length: > 0 } scope }
                && policy.CanRead(userId, scope, id)))
            .ToList();

        return Results.Ok(new EvaluationsResponse(decisions));
    }

    private static bool IsUserReading(AuthZenSubject? subject, AuthZenAction? action) =>
        subject is { Type: "user", Id: { Length: > 0 } } && action?.Name == "read";

    private static IResult BadRequest(string title) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
```

In `Program.cs`, after `builder.Services.AddSingleton<ChangeJournal>();` add:

```csharp
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LinkStore>();
builder.Services.AddSingleton<AccessPolicy>();
```

and after `app.MapContentEndpoints();` add:

```csharp
app.MapPermissionEndpoints();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Connapse.CustomSource.Example.Tests`
Expected: PASS (all tests so far).

- [ ] **Step 7: Commit**

```bash
git add samples/Connapse.CustomSource.Example/AuthZenModels.cs samples/Connapse.CustomSource.Example/AccessPolicy.cs samples/Connapse.CustomSource.Example/LinkStore.cs samples/Connapse.CustomSource.Example/PermissionEndpoints.cs samples/Connapse.CustomSource.Example/Program.cs tests/Connapse.CustomSource.Example.Tests/PermissionTests.cs
git commit -m "feat: example server AuthZEN key search and batched check (#738)"
```

---

### Task 6: Account linking with confirmation

**Files:**
- Modify: `samples/Connapse.CustomSource.Example/LinkStore.cs` (add pending links, sessions, used token IDs)
- Create: `samples/Connapse.CustomSource.Example/LinkEndpoints.cs`
- Modify: `samples/Connapse.CustomSource.Example/Program.cs`
- Test: `tests/Connapse.CustomSource.Example.Tests/LinkingTests.cs`

**Interfaces:**
- Consumes: `LinkStore.ExternalUser/Remove`, `ExampleConfig.LinkSecret/ConnapseIssuer/ProviderId/Users`, `PermissionTests.PostJson/KeySearch`.
- Produces: `LinkStore.TryUseTokenId(string jti, DateTimeOffset expiresAt)`, `LinkStore.StartSession(string connapseUserId, string returnUrl)`, `LinkStore.TakeSession(string state)`, `LinkStore.AddPending(string connapseUserId, string externalUser)`, `LinkStore.Confirm(string code, string connapseUserId)`; records `LinkSession`, `PendingLink`, `ConfirmRequest`, `LinkStatus`; `LinkEndpoints.MapLinkEndpoints(this IEndpointRouteBuilder)`.

- [ ] **Step 1: Write the failing tests**

`tests/Connapse.CustomSource.Example.Tests/LinkingTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
public partial class LinkingTests
{
    private const string Carol = "connapse-carol";
    private const string ReturnUrl = "https://connapse.test/integrations/custom/return";

    [Fact]
    public async Task Link_FullFlow_LinksOnlyAfterConfirmation()
    {
        using ExampleServer server = new();
        HttpClient browser = server.Client(authorized: false);

        string code = await StartAndComplete(browser, LinkToken(Carol), "bob");
        (await Status(server, Carol)).GetProperty("linked").GetBoolean().Should().BeFalse();

        HttpResponseMessage confirm = await server.Client().PostAsJsonAsync("/v1/links/confirm", new { code, connapse_user_id = Carol });

        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement status = await Status(server, Carol);
        status.GetProperty("linked").GetBoolean().Should().BeTrue();
        status.GetProperty("display_name").GetString().Should().Be("bob");
        JsonElement keys = await PermissionTests.PostJson(server, "/access/v1/search/resource", PermissionTests.KeySearch(Carol, "eng"));
        keys.GetProperty("results").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Link_ConfirmedForADifferentConnapseUser_Returns409AndLinksNobody()
    {
        using ExampleServer server = new();
        // The attacker (Carol) starts a link and forwards the page; the victim finishes it in their own
        // browser, and Connapse confirms with the victim's signed-in user.
        string code = await StartAndComplete(server.Client(authorized: false), LinkToken(Carol), "bob");

        HttpResponseMessage confirm = await server.Client().PostAsJsonAsync("/v1/links/confirm", new { code, connapse_user_id = "connapse-victim" });
        HttpResponseMessage retry = await server.Client().PostAsJsonAsync("/v1/links/confirm", new { code, connapse_user_id = Carol });

        confirm.StatusCode.Should().Be(HttpStatusCode.Conflict);
        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Status(server, Carol)).GetProperty("linked").GetBoolean().Should().BeFalse();
        (await Status(server, "connapse-victim")).GetProperty("linked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Link_CodeIsSingleUse()
    {
        using ExampleServer server = new();
        string code = await StartAndComplete(server.Client(authorized: false), LinkToken(Carol), "bob");

        (await server.Client().PostAsJsonAsync("/v1/links/confirm", new { code, connapse_user_id = Carol }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await server.Client().PostAsJsonAsync("/v1/links/confirm", new { code, connapse_user_id = Carol }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Link_ReplayedToken_Returns400()
    {
        using ExampleServer server = new();
        string token = LinkToken(Carol);

        (await Start(server.Client(authorized: false), token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Start(server.Client(authorized: false), token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("wrong-secret-0123456789abcdef0123456789", ExampleServer.Issuer, ExampleServer.ProviderId)]
    [InlineData(ExampleServer.LinkSecret, "https://someone-else.test", ExampleServer.ProviderId)]
    [InlineData(ExampleServer.LinkSecret, ExampleServer.Issuer, "provider-2")]
    public async Task Link_TokenWithWrongSecretIssuerOrAudience_Returns400(string secret, string issuer, string audience)
    {
        using ExampleServer server = new();

        HttpResponseMessage response = await Start(server.Client(authorized: false), LinkToken(Carol, secret, issuer, audience));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Link_ExpiredToken_Returns400()
    {
        using ExampleServer server = new();

        HttpResponseMessage response = await Start(server.Client(authorized: false), LinkToken(Carol, expiresIn: TimeSpan.FromMinutes(-5)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Link_CompletedInADifferentBrowser_Returns400()
    {
        using ExampleServer server = new();
        HttpResponseMessage started = await Start(server.Client(authorized: false), LinkToken(Carol));
        string state = StateField().Match(await started.Content.ReadAsStringAsync()).Groups[1].Value;

        HttpResponseMessage response = await server.Client(authorized: false).PostAsync("/link/complete",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = state, ["user"] = "bob" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unlink_RemovesTheLink_AndIsIdempotent()
    {
        using ExampleServer server = new();
        HttpClient api = server.Client();

        (await api.DeleteAsync($"/v1/links/{ExampleServer.AliceId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.DeleteAsync($"/v1/links/{ExampleServer.AliceId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Status(server, ExampleServer.AliceId)).GetProperty("linked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task LinkApi_WithoutToken_Returns401()
    {
        using ExampleServer server = new();

        HttpResponseMessage response = await server.Client(authorized: false).GetAsync($"/v1/links/{ExampleServer.AliceId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static string LinkToken(
        string connapseUserId,
        string secret = ExampleServer.LinkSecret,
        string issuer = ExampleServer.Issuer,
        string audience = ExampleServer.ProviderId,
        TimeSpan? expiresIn = null)
    {
        DateTime now = DateTime.UtcNow;
        DateTime expires = now + (expiresIn ?? TimeSpan.FromMinutes(5));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = expires < now ? expires.AddMinutes(-1) : now,
            IssuedAt = expires < now ? expires.AddMinutes(-1) : now,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", connapseUserId),
                new Claim("jti", Guid.NewGuid().ToString("N")),
                new Claim("return_url", ReturnUrl),
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256),
        });
    }

    private static Task<HttpResponseMessage> Start(HttpClient browser, string token) =>
        browser.PostAsync("/link", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));

    /// <returns>The one-time code from the redirect back to Connapse.</returns>
    private static async Task<string> StartAndComplete(HttpClient browser, string token, string user)
    {
        HttpResponseMessage started = await Start(browser, token);
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        started.Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
        string state = StateField().Match(await started.Content.ReadAsStringAsync()).Groups[1].Value;

        HttpResponseMessage completed = await browser.PostAsync("/link/complete",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = state, ["user"] = user }));

        completed.StatusCode.Should().Be(HttpStatusCode.Redirect);
        Uri location = completed.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).Should().Be(ReturnUrl);
        return Uri.UnescapeDataString(location.Query.Split("code=")[1]);
    }

    private static async Task<JsonElement> Status(ExampleServer server, string connapseUserId) =>
        await DiscoveryTests.GetJson(server, $"/v1/links/{connapseUserId}");

    [GeneratedRegex("name=\"state\" value=\"([^\"]+)\"")]
    private static partial Regex StateField();
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Connapse.CustomSource.Example.Tests --filter "FullyQualifiedName~LinkingTests"`
Expected: FAIL — `/link` and `/v1/links/*` return 404.

- [ ] **Step 3: Extend the link store**

In `samples/Connapse.CustomSource.Example/LinkStore.cs`, add these usings at the top:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
```

Add these records above the `LinkStore` class:

```csharp
/// <summary>A browser that presented a valid link token and has not yet signed in to the example system.</summary>
public sealed record LinkSession(string ConnapseUserId, string ReturnUrl, DateTimeOffset ExpiresAt);

/// <summary>A finished sign-in waiting for Connapse to confirm it is the same Connapse user.</summary>
public sealed record PendingLink(string ConnapseUserId, string ExternalUser, DateTimeOffset ExpiresAt);
```

Add these fields beside `_links`:

```csharp
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, LinkSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingLink> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _usedTokenIds = new(StringComparer.Ordinal);
```

Add these methods to `LinkStore`:

```csharp
    /// <summary>False when this token ID was already used. Expired IDs are forgotten.</summary>
    public bool TryUseTokenId(string tokenId, DateTimeOffset expiresAt)
    {
        lock (_gate)
        {
            DateTimeOffset now = Time.GetUtcNow();
            foreach (string stale in _usedTokenIds.Where(p => p.Value < now - Lifetime).Select(p => p.Key).ToList())
                _usedTokenIds.Remove(stale);
            return _usedTokenIds.TryAdd(tokenId, expiresAt);
        }
    }

    public string StartSession(string connapseUserId, string returnUrl)
    {
        string state = NewSecret();
        lock (_gate) _sessions[state] = new LinkSession(connapseUserId, returnUrl, Time.GetUtcNow() + Lifetime);
        return state;
    }

    public LinkSession? TakeSession(string state)
    {
        lock (_gate)
        {
            return _sessions.Remove(state, out LinkSession? session) && session.ExpiresAt > Time.GetUtcNow()
                ? session
                : null;
        }
    }

    public string AddPending(string connapseUserId, string externalUser)
    {
        string code = NewSecret();
        lock (_gate) _pending[code] = new PendingLink(connapseUserId, externalUser, Time.GetUtcNow() + Lifetime);
        return code;
    }

    /// <summary>
    /// Activates the pending link only for the Connapse user it was started for. The code is consumed
    /// either way, so a wrong guess cannot be followed by the right one.
    /// </summary>
    public bool Confirm(string code, string connapseUserId)
    {
        lock (_gate)
        {
            if (!_pending.Remove(code, out PendingLink? pending)) return false;
            if (pending.ConnapseUserId != connapseUserId || pending.ExpiresAt <= Time.GetUtcNow()) return false;

            _links[connapseUserId] = pending.ExternalUser;
            Save();
            return true;
        }
    }

    private static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
```

- [ ] **Step 4: Write the link endpoints**

`samples/Connapse.CustomSource.Example/LinkEndpoints.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.CustomSource.Example;

public sealed record ConfirmRequest(string? Code, string? ConnapseUserId);

public sealed record LinkStatus(bool Linked, string? DisplayName);

/// <summary>
/// The linking page Connapse sends a browser to, and the API Connapse uses to confirm and read links.
/// Choosing a name on the sign-in page stands in for signing in to the real target system.
/// </summary>
public static class LinkEndpoints
{
    private const string StateCookie = "example_link_state";

    public static void MapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/link", StartLink);
        app.MapPost("/link/complete", CompleteLink);

        RouteGroupBuilder links = app.MapGroup("/v1/links").AddEndpointFilter<BearerTokenFilter>();
        links.MapPost("/confirm", Confirm);
        links.MapGet("/{connapseUserId}", Status);
        links.MapDelete("/{connapseUserId}", Unlink);
    }

    private static async Task<IResult> StartLink(HttpContext http, ExampleConfig config, LinkStore links)
    {
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (config.LinkSecret is null) return Results.NotFound();

        IFormCollection form = await http.Request.ReadFormAsync();
        TokenValidationResult result = await new JsonWebTokenHandler().ValidateTokenAsync(
            form["token"].ToString(),
            new TokenValidationParameters
            {
                ValidIssuer = config.ConnapseIssuer,
                ValidAudience = config.ProviderId,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.LinkSecret)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromSeconds(60),
                RequireExpirationTime = true,
            });
        if (!result.IsValid) return BadRequest("The link token is not valid.");

        string? connapseUserId = result.ClaimsIdentity.FindFirst("sub")?.Value;
        string? tokenId = result.ClaimsIdentity.FindFirst("jti")?.Value;
        string? returnUrl = result.ClaimsIdentity.FindFirst("return_url")?.Value;
        if (string.IsNullOrEmpty(connapseUserId) || string.IsNullOrEmpty(tokenId)
            || !Uri.TryCreate(returnUrl, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            return BadRequest("The link token is missing sub, jti or an absolute return_url.");

        DateTimeOffset expiresAt = new(result.SecurityToken.ValidTo, TimeSpan.Zero);
        if (!links.TryUseTokenId(tokenId, expiresAt)) return BadRequest("This link token was already used.");

        string state = links.StartSession(connapseUserId, returnUrl!);
        http.Response.Cookies.Append(StateCookie, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/link",
            MaxAge = TimeSpan.FromMinutes(5),
        });
        return Results.Content(SignInPage(state, config.Users), "text/html");
    }

    private static async Task<IResult> CompleteLink(HttpContext http, ExampleConfig config, LinkStore links)
    {
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        IFormCollection form = await http.Request.ReadFormAsync();
        string state = form["state"].ToString();
        string user = form["user"].ToString();
        string? cookie = http.Request.Cookies[StateCookie];

        // The state cookie proves this browser is the one that presented the link token.
        if (state.Length == 0 || cookie is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(state)))
            return BadRequest("This sign-in did not start in this browser.");

        LinkSession? session = links.TakeSession(state);
        if (session is null) return BadRequest("This sign-in expired; start again from Connapse.");
        if (!config.Users.Contains(user, StringComparer.Ordinal)) return BadRequest("Unknown user.");

        string code = links.AddPending(session.ConnapseUserId, user);
        http.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/link" });
        string separator = session.ReturnUrl.Contains('?') ? "&" : "?";
        return Results.Redirect($"{session.ReturnUrl}{separator}code={Uri.EscapeDataString(code)}");
    }

    private static IResult Confirm(ConfirmRequest request, ExampleConfig config, LinkStore links)
    {
        if (config.LinkSecret is null) return Results.NotFound();
        if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.ConnapseUserId))
            return BadRequest("code and connapse_user_id are required.");

        return links.Confirm(request.Code, request.ConnapseUserId)
            ? Results.Ok(new LinkStatus(true, links.ExternalUser(request.ConnapseUserId)))
            : Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The code is unknown, expired, already used, or was issued for another user.");
    }

    private static IResult Status(string connapseUserId, LinkStore links)
    {
        string? user = links.ExternalUser(connapseUserId);
        return Results.Ok(new LinkStatus(user is not null, user));
    }

    private static IResult Unlink(string connapseUserId, LinkStore links)
    {
        links.Remove(connapseUserId);
        return Results.NoContent();
    }

    private static IResult BadRequest(string title) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);

    private static string SignInPage(string state, IReadOnlyList<string> users)
    {
        StringBuilder html = new("<!doctype html><html><head><meta charset=\"utf-8\"><title>Sign in</title></head><body>");
        html.Append("<h1>Sign in to the example system</h1><p>Choose who you are in the example system.</p>");
        foreach (string user in users)
        {
            string name = WebUtility.HtmlEncode(user);
            html.Append("<form method=\"post\" action=\"/link/complete\">")
                .Append($"<input type=\"hidden\" name=\"state\" value=\"{WebUtility.HtmlEncode(state)}\">")
                .Append($"<input type=\"hidden\" name=\"user\" value=\"{name}\">")
                .Append($"<button type=\"submit\">{name}</button></form>");
        }
        return html.Append("</body></html>").ToString();
    }
}
```

In `Program.cs`, after `app.MapPermissionEndpoints();` add:

```csharp
app.MapLinkEndpoints();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Connapse.CustomSource.Example.Tests`
Expected: PASS (all tests so far). If `Link_ExpiredToken_Returns400` fails because token creation rejects `NotBefore` after `Expires`, keep `NotBefore`/`IssuedAt` one minute before `Expires` as written; the server must still answer 400.

- [ ] **Step 6: Commit**

```bash
git add samples/Connapse.CustomSource.Example/LinkStore.cs samples/Connapse.CustomSource.Example/LinkEndpoints.cs samples/Connapse.CustomSource.Example/Program.cs tests/Connapse.CustomSource.Example.Tests/LinkingTests.cs
git commit -m "feat: example server account linking with session-bound confirmation (#738)"
```

---

### Task 7: OpenAPI agreement, sample data, container and README

**Files:**
- Test: `tests/Connapse.CustomSource.Example.Tests/OpenApiAgreementTests.cs`
- Create: `samples/Connapse.CustomSource.Example/sample-data/example.json`
- Create: `samples/Connapse.CustomSource.Example/sample-data/scopes/eng/README.md`
- Create: `samples/Connapse.CustomSource.Example/sample-data/scopes/eng/design/architecture.md`
- Create: `samples/Connapse.CustomSource.Example/sample-data/scopes/eng/design/incident-review.md`
- Create: `samples/Connapse.CustomSource.Example/sample-data/scopes/ops/runbook.md`
- Modify: `samples/Connapse.CustomSource.Example/Connapse.CustomSource.Example.csproj` (copy sample data)
- Create: `samples/Connapse.CustomSource.Example/Dockerfile`
- Create: `samples/Connapse.CustomSource.Example/README.md`

**Interfaces:**
- Consumes: every route from Tasks 2–6; `docs/protocols/custom-source-v1.openapi.json` from Task 1.

- [ ] **Step 1: Write the agreement test**

`tests/Connapse.CustomSource.Example.Tests/OpenApiAgreementTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
public class OpenApiAgreementTests
{
    [Fact]
    public void ServerRoutes_MatchTheOpenApiPaths()
    {
        using ExampleServer server = new();
        server.Client();

        HashSet<string> served = server.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => "/" + e.RoutePattern.RawText!.Trim('/'))
            .Where(p => !p.StartsWith("/link", StringComparison.Ordinal)) // link_url is chosen by the middle man
            .Select(Normalise)
            .ToHashSet();

        using JsonDocument openApi = JsonDocument.Parse(File.ReadAllText(OpenApiPath()));
        HashSet<string> documented = openApi.RootElement.GetProperty("paths").EnumerateObject()
            .Select(p => Normalise(p.Name))
            .ToHashSet();

        served.Should().BeEquivalentTo(documented);
    }

    // Parameter names differ in case between C# and the document; compare shapes only.
    private static string Normalise(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path, "\\{[^}]+\\}", "{}");

    private static string OpenApiPath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test runs inside the repository");
        return Path.Combine(dir!.FullName, "docs", "protocols", "custom-source-v1.openapi.json");
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/Connapse.CustomSource.Example.Tests --filter "FullyQualifiedName~OpenApiAgreementTests"`
Expected: PASS. If it fails, the failure lists the path that is served but undocumented or documented but not served; fix whichever side is wrong against the prose specification, then re-run.

- [ ] **Step 3: Add the sample data and copy it to the output**

`samples/Connapse.CustomSource.Example/sample-data/example.json`:

```json
{
  "name": "Example folder bridge",
  "token": "change-me-example-token",
  "page_size": 100,
  "permissions_enabled": true,
  "max_check_batch": 100,
  "link_secret": "change-me-link-secret-at-least-32-characters",
  "connapse_issuer": "http://localhost:5001",
  "provider_id": "paste-the-connapse-provider-id-here",
  "scopes": [
    { "id": "eng", "name": "Engineering", "description": "Engineering handbook and design notes" },
    { "id": "ops", "name": "Operations", "description": "Runbooks" }
  ],
  "grants": {
    "alice": ["folder:eng/", "folder:eng/design"],
    "bob": ["folder:eng/", "folder:ops/"]
  },
  "denies": {
    "alice": ["eng/design/incident-review.md"]
  },
  "preset_links": {},
  "users": ["alice", "bob"]
}
```

`sample-data/scopes/eng/README.md`:

```markdown
# Engineering handbook

Everyone in engineering can read this page. Deploys happen on Tuesdays and Thursdays.
```

`sample-data/scopes/eng/design/architecture.md`:

```markdown
# Architecture

The design folder is readable by people granted `folder:eng/design`. Services talk over HTTP and share one Postgres.
```

`sample-data/scopes/eng/design/incident-review.md`:

```markdown
# Incident review

Alice holds the design folder's key but is denied this page, so only the permission check — not the key — keeps it from her.
```

`sample-data/scopes/ops/runbook.md`:

```markdown
# On-call runbook

Restart the worker first; page the database owner if replication lag passes five minutes.
```

In `Connapse.CustomSource.Example.csproj`, add:

```xml
  <ItemGroup>
    <None Include="sample-data\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 4: Add the Dockerfile and README**

`samples/Connapse.CustomSource.Example/Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Connapse.CustomSource.Example.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore -p:MinVerSkip=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Connapse.CustomSource.Example.dll"]
```

`samples/Connapse.CustomSource.Example/README.md`:

````markdown
# Example custom source middle man

A small server that implements every part of the [Connapse custom source protocol](../../docs/protocols/custom-source-v1.md),
backed by a folder of files. It exists to test Connapse against and to read as a reference — not to deploy.

## What it pretends to be

- Each folder under `sample-data/scopes/` is a **scope**.
- Each top-level folder inside a scope gives its documents one **access key**: `folder:{scope}/{folder}`, or
  `folder:{scope}/` for files at the scope root.
- `example.json` lists the system's **users**, the keys each one holds (`grants`), and documents a user may not
  read even with the key (`denies`).
- Account links are kept in `links.json` next to `example.json` — the middle man's own storage.

## Run it

```bash
docker build -t connapse-custom-source-example samples/Connapse.CustomSource.Example
```

```bash
docker run --rm -p 8080:8080 -v "$PWD/samples/Connapse.CustomSource.Example/sample-data:/app/sample-data" connapse-custom-source-example
```

```bash
curl -H "Authorization: Bearer change-me-example-token" http://localhost:8080/.well-known/connapse-source
```

Or without Docker:

```bash
dotnet run --project samples/Connapse.CustomSource.Example --urls http://localhost:8080
```

## Settings in `example.json`

| Member | Meaning |
|---|---|
| `token` | The bearer token Connapse must send. |
| `permissions_enabled` | `false` makes it a content-only middle man. |
| `link_secret`, `connapse_issuer`, `provider_id` | Must match the custom provider in Connapse for linking to work. Remove `link_secret` to turn linking off. |
| `page_size`, `max_check_batch` | Page size and the largest permission-check batch accepted. |
| `preset_links` | Connapse user ID → example user, to skip the linking flow while testing. |
````

- [ ] **Step 5: Build and run the container, and check discovery and a check call**

Run: `docker build -t connapse-custom-source-example samples/Connapse.CustomSource.Example`
Expected: build succeeds.

Run: `docker run -d --name cs-example -p 18080:8080 connapse-custom-source-example`

Run: `curl -s -H "Authorization: Bearer change-me-example-token" http://localhost:18080/.well-known/connapse-source`
Expected: JSON with `"protocol_versions":["1"]` and a `permissions` object.

Run: `curl -s -o /dev/null -w "%{http_code}" http://localhost:18080/v1/scopes`
Expected: `401`

Run: `docker rm -f cs-example`

- [ ] **Step 6: Run the whole test project and the solution build**

Run: `dotnet test tests/Connapse.CustomSource.Example.Tests`
Expected: PASS, every test.

Run: `dotnet build Connapse.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit and open PR C (Tasks 5–7, `Closes #738`)**

```bash
git add samples/Connapse.CustomSource.Example/Connapse.CustomSource.Example.csproj samples/Connapse.CustomSource.Example/Dockerfile samples/Connapse.CustomSource.Example/README.md samples/Connapse.CustomSource.Example/sample-data/example.json samples/Connapse.CustomSource.Example/sample-data/scopes/eng/README.md samples/Connapse.CustomSource.Example/sample-data/scopes/eng/design/architecture.md samples/Connapse.CustomSource.Example/sample-data/scopes/eng/design/incident-review.md samples/Connapse.CustomSource.Example/sample-data/scopes/ops/runbook.md tests/Connapse.CustomSource.Example.Tests/OpenApiAgreementTests.cs
git commit -m "feat: example server sample data, container and OpenAPI agreement test (#738)"
```
