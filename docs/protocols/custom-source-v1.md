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
- Pages must not shift while documents come and go. Order the listing by a stable key, such as the ID, and
  let `next_page_token` mean "after the last item returned", never an offset: with offsets, deleting an
  item on an earlier page pushes an unchanged one back across the boundary, so it appears in no page and
  Connapse removes it. Adding or removing documents between pages may change which of *those* documents a
  listing includes — the change feed covers them — but must never drop an unchanged one.
- A `page_token` you no longer recognise: `410 Gone`. Connapse abandons that listing without removing
  anything and starts a new one.
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
- An upsert carries the same members as a listing item, including every member the listing requires
  (`path`, `content_type`, `size`, `version`).
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
- If you cannot decide one item, answer `{ "decision": false, "context": { "error": "…" } }` for it rather
  than failing the batch. An item carrying `context.error` is **no access even when its decision is true**.
- Connapse shows a document only on an explicit `"decision": true` with no `context.error`. A timeout, an
  error response, a missing or malformed decision, or a result count that does not match the request hides
  the documents involved.

### Access keys — optional

Only with `permissions.access_keys`. An access key is an opaque label you attach to documents in the
listing, such as `channel:C123` or `project:ENG`. Connapse uses a user's keys only to choose which documents
to rank before the check; the check alone decides what is shown, so keys make search find more for users
who can read a small part of a source.

Once you advertise access keys, **give every document at least one key**: a document with no keys is
ranked for no one, so it never reaches the check and never appears in search. For documents every linked
user may read, use one shared key (for example `everyone`) and return it for every linked user.

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
   it and shows it to the admin once; the HMAC key is the UTF-8 bytes of the secret exactly as shown). Claims: `iss` (Connapse's base URL), `sub` (Connapse user ID),
   `aud` (Connapse's provider ID), `jti`, `exp` (5 minutes), `return_url`.
2. Verify the signature, `iss`, `aud` and `exp` (allow at most 60 seconds of clock skew), and refuse a
   `jti` you have already seen. Accept only `HS256`: reject any token whose header names another
   algorithm, including `none`, rather than letting the header choose how it is verified. Sign the user in to your system, binding that sign-in to this browser with
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
