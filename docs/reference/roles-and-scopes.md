---
title: Roles and scopes
type: reference
audience: admin
---

# Roles and scopes

A call passes only when the caller's **role** allows it and, for a token, one of the token's **scopes** does too. A browser sign-in or a JWT from `POST /api/v1/auth/token` has every scope its role implies; a personal access token or agent key has only the scopes it was issued with. Why it works this way is in [About permissions](../concepts/permissions.md).

## Roles

| Role | Who | Can |
|------|-----|-----|
| Owner | The first account, created at setup | Everything an Admin can, and manage other Admins |
| Admin | Invited by the Owner | Manage users, invites, agents, connections, sources, settings and providers; read the audit log; reach `/mcp` |
| Editor | Invited by an Owner or Admin | Create and delete containers, upload and delete files, and everything a Viewer can |
| Viewer | Invited by an Owner or Admin | Search, browse and read documents |
| Agent | An agent key, not an account | Reach `/mcp` with the scopes its key holds |

## Scopes

| Scope | Allows | Needs the role |
|-------|--------|----------------|
| `knowledge:read` | Search, list and read containers, sources and documents | Viewer or above, or an agent key |
| `knowledge:write` | Create and delete containers; upload and delete files; unlink your own accounts | Editor or above, or an agent key |
| `agent:ingest` | Nothing yet: no operation checks it | — |
| `admin:users` | Users, invites, agents and agent keys | Admin or Owner; managing Admins needs the Owner |
| `admin:audit` | Read the audit log | Admin or Owner |
| `admin:oauth-clients` | List and revoke OAuth clients | Admin or Owner |
| `admin:settings` | Read, change and reset settings, reindex, providers | Admin or Owner |
| `admin:sources` | Connections and sources, including their credentials | Admin or Owner |

A personal access token may be issued any of these scopes, but it can never do more than its owner's role allows. An agent key may hold only `knowledge:read`, `knowledge:write` and `agent:ingest`. `pats:manage` is held by no token: you manage your own personal access tokens only while signed in yourself.

A few operations ask for more than their scope's role: regenerating or clearing a container's summaries needs an Admin, and so does the GitHub App install callback.

## Operations by scope

Generated from the `x-connapse-operation` extension in [`docs/api/openapi.json`](../api/openapi.json). **Risk** is Read, Write, Destructive or Credential; over MCP, a Destructive or Credential call from a token that asks for approval is held until a person approves it. **MCP** says whether the operation is reachable through the generic MCP tools; see [MCP tools](mcp-tools.md).

### `admin:audit`

| Operation | Route | Risk | MCP |
|-----------|-------|------|-----|
| `audit.list` | `GET /api/v1/audit` | Read | Generic tools |

### `admin:oauth-clients`

| Operation | Route | Risk | MCP |
|-----------|-------|------|-----|
| `oauthClients.list` | `GET /api/v1/oauth-clients` | Read | Generic tools |
| `oauthClients.revoke` | `DELETE /api/v1/oauth-clients/{clientId}` | Destructive | Generic tools |

### `admin:settings`

| Operation | Route | Risk | MCP |
|-----------|-------|------|-----|
| `providers.aws.access` | `PUT /api/v1/providers/aws/access` | Credential | Generic tools |
| `providers.aws.access.get` | `GET /api/v1/providers/aws/access` | Read | Generic tools |
| `providers.aws.access.reset` | `DELETE /api/v1/providers/aws/access` | Destructive | Generic tools |
| `providers.aws.identityCenter` | `PUT /api/v1/providers/aws/identity-center` | Write | Generic tools |
| `providers.aws.identityCenter.reset` | `DELETE /api/v1/providers/aws/identity-center` | Destructive | Generic tools |
| `providers.aws.permissions` | `PUT /api/v1/providers/aws/permissions` | Destructive | Generic tools |
| `providers.aws.permissions.reset` | `DELETE /api/v1/providers/aws/permissions` | Destructive | Generic tools |
| `providers.azure.access` | `PUT /api/v1/providers/azure/access` | Credential | Generic tools |
| `providers.azure.access.reset` | `DELETE /api/v1/providers/azure/access` | Destructive | Generic tools |
| `providers.azure.permissions` | `PUT /api/v1/providers/azure/permissions` | Destructive | Generic tools |
| `providers.azure.permissions.reset` | `DELETE /api/v1/providers/azure/permissions` | Destructive | Generic tools |
| `providers.custom.check` | `POST /api/v1/providers/custom/{providerId}/check` | Write | Generic tools |
| `providers.custom.create` | `POST /api/v1/providers/custom` | Credential | Generic tools |
| `providers.custom.delete` | `DELETE /api/v1/providers/custom/{providerId}` | Destructive | Generic tools |
| `providers.custom.linkSecret` | `PUT /api/v1/providers/custom/{providerId}/link-secret` | Credential | Generic tools |
| `providers.custom.list` | `GET /api/v1/providers/custom` | Read | Generic tools |
| `providers.custom.update` | `PATCH /api/v1/providers/custom/{providerId}` | Credential | Generic tools |
| `providers.github.app` | `PUT /api/v1/providers/github/app` | Credential | Generic tools |
| `providers.github.app.get` | `GET /api/v1/providers/github/app` | Read | Generic tools |
| `providers.github.app.remove` | `DELETE /api/v1/providers/github/app` | Destructive | Generic tools |
| `providers.github.createApp` | `POST /api/v1/providers/github/manifest` | Write | — |
| `providers.github.createApp.callback` | `GET /api/v1/providers/github/manifest/callback` | Credential | — |
| `providers.github.installed` | `GET /api/v1/providers/github/installed` | Read | Generic tools |
| `providers.status` | `GET /api/v1/providers` | Read | Generic tools |
| `settings.embeddingModels` | `GET /api/settings/embedding-models` | Read | Generic tools |
| `settings.get` | `GET /api/settings/{category}` | Read | Generic tools |
| `settings.reindex` | `POST /api/settings/reindex` | Write | Generic tools |
| `settings.reindexStatus` | `GET /api/settings/reindex/status` | Read | Generic tools |
| `settings.reset` | `DELETE /api/v1/settings/{category}` | Destructive | Generic tools |
| `settings.summaries.clear` | `POST /api/v1/settings/summaries/clear` | Destructive | Generic tools |
| `settings.testConnection` | `POST /api/settings/test-connection` | Write | Generic tools |
| `settings.update` | `PUT /api/settings/{category}` | Credential | Generic tools |

### `admin:sources`

| Operation | Route | Risk | MCP |
|-----------|-------|------|-----|
| `atlassian.sites.delete` | `DELETE /api/v1/atlassian/sites/{connectionId}` | Destructive | Generic tools |
| `atlassian.sites.get` | `GET /api/v1/atlassian/sites/{connectionId}` | Read | Generic tools |
| `atlassian.sites.list` | `GET /api/v1/atlassian/sites` | Read | Generic tools |
| `atlassian.sites.pause` | `POST /api/v1/atlassian/sites/{connectionId}/pause` | Write | Generic tools |
| `atlassian.sites.resolve` | `POST /api/v1/atlassian/sites/resolve` | Write | Generic tools |
| `atlassian.sites.resume` | `POST /api/v1/atlassian/sites/{connectionId}/resume` | Write | Generic tools |
| `atlassian.sites.test` | `POST /api/v1/atlassian/sites/{connectionId}/test` | Write | Generic tools |
| `atlassian.sites.update` | `PATCH /api/v1/atlassian/sites/{connectionId}` | Write | Generic tools |
| `connections.create` | `POST /api/v1/connections` | Credential | Generic tools |
| `connections.delete` | `DELETE /api/v1/connections/{connectionId}` | Destructive | Generic tools |
| `connections.discover` | `GET /api/v1/connections/{connectionId}/discover` | Read | Generic tools |
| `connections.forgetHostKey` | `DELETE /api/v1/connections/{connectionId}/host-key` | Write | Generic tools |
| `connections.get` | `GET /api/v1/connections/{connectionId}` | Read | Generic tools |
| `connections.list` | `GET /api/v1/connections` | Read | Generic tools |
| `connections.pause` | `POST /api/v1/connections/{connectionId}/pause` | Write | Generic tools |
| `connections.replaceSecret` | `PUT /api/v1/connections/{connectionId}/secret` | Credential | Generic tools |
| `connections.resume` | `POST /api/v1/connections/{connectionId}/resume` | Write | Generic tools |
| `connections.test` | `POST /api/v1/connections/{connectionId}/test` | Write | Generic tools |
| `connections.update` | `PATCH /api/v1/connections/{connectionId}` | Credential | Generic tools |
| `sources.create` | `POST /api/sources` | Write | Generic tools |
| `sources.delete` | `DELETE /api/sources/{sourceId}` | Destructive | Generic tools |
| `sources.retryFailed` | `POST /api/sources/{sourceId}/retry-failed` | Write | Generic tools |
| `sources.sync` | `POST /api/sources/{sourceId}/sync` | Write | Generic tools |
| `sources.update` | `PATCH /api/sources/{sourceId}` | Write | Generic tools |

### `admin:users`

| Operation | Route | Risk | MCP |
|-----------|-------|------|-----|
| `agents.create` | `POST /api/v1/agents` | Write | Generic tools |
| `agents.delete` | `DELETE /api/v1/agents/{id}` | Destructive | Generic tools |
| `agents.get` | `GET /api/v1/agents/{id}` | Read | Generic tools |
| `agents.keys.create` | `POST /api/v1/agents/{id}/keys` | Credential | Generic tools |
| `agents.keys.list` | `GET /api/v1/agents/{id}/keys` | Read | Generic tools |
| `agents.keys.revoke` | `DELETE /api/v1/agents/{id}/keys/{keyId}` | Destructive | Generic tools |
| `agents.list` | `GET /api/v1/agents` | Read | Generic tools |
| `agents.setActive` | `PUT /api/v1/agents/{id}/active` | Write | Generic tools |
| `invites.create` | `POST /api/v1/invites` | Write | Generic tools |
| `invites.list` | `GET /api/v1/invites` | Read | Generic tools |
| `invites.revoke` | `DELETE /api/v1/invites/{id}` | Destructive | Generic tools |
| `users.delete` | `DELETE /api/v1/auth/users/{id}` | Destructive | Generic tools |
| `users.list` | `GET /api/v1/auth/users` | Read | Generic tools |
| `users.setRoles` | `PUT /api/v1/auth/users/{id}/roles` | Write | Generic tools |

### `knowledge:read`

| Operation | Route | Risk | MCP |
|-----------|-------|------|-----|
| `batches.status` | `GET /api/batches/{id}/status` | Read | Generic tools |
| `containers.get` | `GET /api/containers/by-name/{name}` | Read | — |
| `containers.get` | `GET /api/containers/{containerId}` | Read | — |
| `containers.list` | `GET /api/containers` | Read | — |
| `containers.settings.get` | `GET /api/containers/{containerId}/settings` | Read | Generic tools |
| `containers.stats` | `GET /api/containers/{containerId}/stats` | Read | — |
| `files.content` | `GET /api/containers/{containerId}/files/{fileId}/content` | Read | — |
| `files.get` | `GET /api/containers/{containerId}/files/{fileId}` | Read | Generic tools |
| `files.list` | `GET /api/containers/{containerId}/files` | Read | — |
| `files.reindexCheck` | `GET /api/containers/{containerId}/files/{fileId}/reindex-check` | Read | Generic tools |
| `links.list` | `GET /api/v1/auth/links` | Read | Generic tools |
| `search.container` | `GET /api/containers/{containerId}/search` | Read | — |
| `search.container` | `POST /api/containers/{containerId}/search` | Read | — |
| `search.source` | `POST /api/sources/{sourceId}/search` | Read | — |
| `sources.get` | `GET /api/sources/{sourceId}` | Read | Generic tools |
| `sources.list` | `GET /api/sources` | Read | — |
| `sources.scopeSchema` | `GET /api/v1/sources/scope-schema/{provider}` | Read | Generic tools |
| `sources.stats` | `GET /api/sources/{sourceId}/stats` | Read | — |

### `knowledge:write`

| Operation | Route | Risk | MCP |
|-----------|-------|------|-----|
| `containers.create` | `POST /api/containers` | Write | — |
| `containers.delete` | `DELETE /api/containers/{containerId}` | Destructive | — |
| `containers.reconcile` | `POST /api/containers/{containerId}/sync` | Write | Generic tools |
| `containers.reindex` | `POST /api/containers/{containerId}/reindex` | Write | Generic tools |
| `containers.rename` | `PATCH /api/containers/{containerId}` | Write | Generic tools |
| `containers.retryFailed` | `POST /api/containers/{containerId}/retry-failed` | Write | Generic tools |
| `containers.settings.reset` | `DELETE /api/containers/{containerId}/settings` | Write | Generic tools |
| `containers.settings.update` | `PUT /api/containers/{containerId}/settings` | Credential | Generic tools |
| `containers.summaries.clear` | `DELETE /api/containers/{containerId}/summaries` | Destructive | Generic tools |
| `containers.summaries.regenerate` | `POST /api/containers/{containerId}/summaries` | Write | Generic tools |
| `files.bulkDelete` | `POST /api/containers/{containerId}/files/bulk-delete` | Destructive | — |
| `files.delete` | `DELETE /api/containers/{containerId}/files/{fileId}` | Destructive | — |
| `files.retry` | `POST /api/containers/{containerId}/files/{fileId}/retry` | Write | Generic tools |
| `folders.create` | `POST /api/containers/{containerId}/folders` | Write | Generic tools |
| `folders.delete` | `DELETE /api/containers/{containerId}/folders` | Destructive | Generic tools |
| `links.unlink` | `DELETE /api/v1/auth/cloud/{provider}` | Destructive | Generic tools |
| `links.unlink` | `DELETE /api/v1/auth/custom/{providerId}` | Destructive | Generic tools |

### `pats:manage`

| Operation | Route | Risk | MCP |
|-----------|-------|------|-----|
| `pats.create` | `POST /api/v1/auth/pats` | Credential | — |
| `pats.list` | `GET /api/v1/auth/pats` | Read | — |
| `pats.revoke` | `DELETE /api/v1/auth/pats/{id}` | Destructive | — |

## Related

- [About permissions](../concepts/permissions.md)
- [Tokens and agents](../guides/tokens-and-agents.md)
- [REST API](rest-api.md)
