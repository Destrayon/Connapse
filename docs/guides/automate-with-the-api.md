---
title: Automate Connapse with the API
type: how-to
audience: admin
---

# Automate Connapse with the API

Everything the web UI does goes through the same operations as the REST API, so a script can set Connapse up from scratch: add a connection, add sources on it, upload to a container, search, and change settings. This guide walks that sequence with `curl`. Every route and body is in the [REST API reference](../reference/rest-api.md).

## Prerequisites

- Connapse running, here at `http://localhost:5001`.
- An Owner's or Admin's [personal access token](tokens-and-agents.md#create-a-personal-access-token) with the scopes the calls need: `knowledge:read` and `knowledge:write` for containers and search, `admin:sources` for connections and sources, and `admin:settings` for settings and providers. Export it:

  ```bash
  export TOKEN=cnp_your_token
  ```

Send it in the `X-Api-Key` header. `Authorization: Bearer` takes only the short-lived JWTs that `POST /api/v1/auth/token` issues for an email and password.

## Steps

### 1. Upload to a container and search it

```bash
CONTAINER_ID=$(curl -s -X POST http://localhost:5001/api/containers \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{ "name": "runbooks", "description": "On-call runbooks" }' | jq -r .id)

curl -X POST http://localhost:5001/api/containers/$CONTAINER_ID/files \
  -H "X-Api-Key: $TOKEN" -F "files=@restart-db.md" -F "path=/db/"
```

The upload answers with a `batchId` and one entry per document. Each document is parsed and embedded in the background; `GET /api/containers/$CONTAINER_ID/files?skip=0&take=50` shows its status. Once it is `Ready`, search:

```bash
curl -X POST http://localhost:5001/api/containers/$CONTAINER_ID/search \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{ "query": "how do I restart the primary database", "topK": 5 }'
```

REST routes take the container's id; `GET /api/containers/by-name/runbooks` looks one up by name. List endpoints need `skip` and `take` (at most 200).

### 2. Add a connection and a source

A connection says how Connapse signs in to a system, and each source on it picks what to read. This example reads a folder on the Connapse host; each [source page](../sources/index.md) has the body for its provider.

```bash
curl -X POST http://localhost:5001/api/v1/connections \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{ "name": "knowledge-share", "provider": "Filesystem", "config": { "allowedRoot": "/srv/knowledge" } }'

curl -X POST http://localhost:5001/api/v1/connections/CONNECTION_ID/test -H "X-Api-Key: $TOKEN"

curl -X POST http://localhost:5001/api/sources \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{ "name": "handbook", "connectionId": "CONNECTION_ID", "syncIntervalSeconds": 600,
        "scope": { "type": "filesystem", "subPath": "handbook", "includePatterns": ["*.md"] } }'
```

Send `scope`, not the deprecated `scopeJson` string. The source syncs on its interval; to start one cycle now and search it:

```bash
curl -X POST http://localhost:5001/api/sources/SOURCE_ID/sync -H "X-Api-Key: $TOKEN"

curl -X POST http://localhost:5001/api/sources/SOURCE_ID/search \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{ "query": "parental leave" }'
```

Source search checks every hit against the caller's permissions at the source, as a container search does.

### 3. Change a setting safely

A settings save replaces the whole category, so read it, change what you need, and send all of it back. The read's `ETag` header, sent back as `If-Match`, makes the save fail rather than overwrite someone else's change:

```bash
curl -s -D headers.txt -H "X-Api-Key: $TOKEN" http://localhost:5001/api/settings/search > search.json
ETAG=$(grep -i '^etag:' headers.txt | cut -d' ' -f2 | tr -d '\r')
jq '.topK = 20' search.json > search-new.json
curl -X PUT http://localhost:5001/api/settings/search \
  -H "X-Api-Key: $TOKEN" -H "If-Match: $ETAG" \
  -H "Content-Type: application/json" --data @search-new.json
```

The categories are `embedding`, `chunking`, `search`, `llm`, `upload` and `summary`. API keys are never returned: one you leave out or send as `null` keeps its value, and an empty string clears it. `DELETE /api/v1/settings/{category}` resets a category to its configured defaults.

### 4. Delete safely

Deleting a connection or a source needs its name as `confirmName`, so a script with a wrong id fails instead of deleting something else:

```bash
curl -X DELETE "http://localhost:5001/api/sources/SOURCE_ID?confirmName=handbook" -H "X-Api-Key: $TOKEN"
curl -X DELETE "http://localhost:5001/api/v1/connections/CONNECTION_ID?confirmName=knowledge-share" -H "X-Api-Key: $TOKEN"
```

A connection can be deleted only once no source uses it. A container can be deleted only once it's empty.

## Handle errors

Every failure is an `application/problem+json` body with `type`, `title`, `status` and usually `detail`. When `type` points at [Problem types](../problems.md), the heading it names explains the problem and what to do. The ones a script meets most:

| Status | Problem | Meaning |
|--------|---------|---------|
| `401` | | The token is missing, wrong, expired or revoked |
| `403` | [`forbidden`](../problems.md#forbidden) | The role or the token's scopes don't allow the call |
| `409` | | The name is taken, or the object is still in use |
| `412` | [`settings-changed`](../problems.md#settings-changed) | The settings changed since your read; read again and reapply |
| `429` | | Rate limited; wait for `Retry-After` |

## Related

- [REST API reference](../reference/rest-api.md)
- [Problem types](../problems.md)
- [Tokens and agents](tokens-and-agents.md)
- [Containers, connections and sources](../sources/index.md)
