---
title: REST API
type: reference
audience: developer
---

# REST API

Everything the web app does is also a REST call. The full contract, with every parameter, body and response, is the OpenAPI 3.1 document [`docs/api/openapi.json`](../api/openapi.json), which the build writes from the code. Open it in any OpenAPI viewer, such as Redoc or Scalar, to browse it, or generate a client from it. This page covers what applies to every call, then lists the endpoints. For worked examples, see [Automate Connapse with the API](../guides/automate-with-the-api.md).

## Base paths

Calls go to your server's address, such as `http://localhost:5001`. Containers, sources, settings and batches live under `/api/containers`, `/api/sources`, `/api/settings` and `/api/batches`; everything else lives under `/api/v1`. The unversioned paths predate versioning and keep their routes so existing scripts don't break.

A route that names a container takes its id (a GUID). To find a container's id from its name, call `GET /api/containers/by-name/{name}`.

## Authentication

| Header | Carries | Get one from |
|--------|---------|--------------|
| `X-Api-Key: cnp_…` | A personal access token, or an agent key | **API Tokens** (`/settings/tokens`), or **Agents** → **API Keys** (`/admin/agents`) |
| `Authorization: Bearer …` | A JWT access token | `POST /api/v1/auth/token` with your email and password; renew it with `POST /api/v1/auth/token/refresh` |

The web app signs you in with a cookie, which the API also accepts. See [Tokens and agents](../guides/tokens-and-agents.md).

A token can do no more than both its owner's role and its scopes allow. A token issued with scopes is limited to them; one issued with none passes no scope check. The scopes are listed in [Roles and scopes](roles-and-scopes.md), and the **Scope** column below shows which one each endpoint needs. "Administrator" means the Owner or Admin role, through a sign-in or any token of theirs.

## Errors

Every failure is an RFC 9457 problem: `application/problem+json` with `type`, `title`, `status` and usually `detail`. When `type` is a link into [Problem types](../problems.md), that entry explains the cause and what to do; otherwise it names the HTTP status. Match on `type` in scripts: it never changes once shipped, while `title` and `detail` may.

```json
{
  "type": "https://github.com/Destrayon/Connapse/blob/main/docs/problems.md#settings-changed",
  "title": "Settings changed since they were read",
  "status": 412
}
```

## Paging

List endpoints take `skip` (default `0`) and `take` (default `50`, at most `200`) and return `{ items, totalCount, hasMore }`. A `skip` below 0 or a `take` outside 1 to 200 is refused with `400`. The audit log pages with a `cursor` instead.

## Conditional saves

`GET /api/settings/{category}` returns an `ETag`. Send it back in `If-Match` on the `PUT`, and the save is refused with `412` and the [`settings-changed`](../problems.md#settings-changed) problem if someone changed the settings since you read them. Without `If-Match`, the save overwrites them.

## Rate limits

Each caller has a budget per minute: 200 requests to the API per user, 600 to `/mcp` per agent, and 10 per IP address to the sign-in and token endpoints and to any API call made without signing in. Over the budget, the answer is `429`, with `Retry-After` when the server knows how long to wait. Self-hosters can change the budgets with the `RateLimiting` settings in [Configuration](configuration.md).

## Endpoints

Generated from `docs/api/openapi.json`, grouped by its tags. "—" in **Scope** means the endpoint isn't an operation with a scope: it is public, part of a sign-in or linking flow, or checks the caller as its description says.

### Discovery and accounts

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `GET` | `/health` | — | Liveness check. No sign-in. |
| `GET` | `/api/v1/info` | — | The server's edition (`self-hosted`), version and features. No sign-in. |
| `GET` | `/.well-known/oauth-protected-resource` | — | OAuth protected-resource metadata (RFC 9728) for MCP clients. |
| `GET` | `/.well-known/oauth-protected-resource/{resourcePath}` | — | The same metadata for a path under the server. |
| `GET` | `/.well-known/oauth-authorization-server` | — | OAuth authorization-server metadata (RFC 8414). |
| `GET` | `/oauth/clients/cli.json` | — | The client metadata document the connapse CLI signs in with. |
| `POST` | `/oauth/token` | — | OAuth token endpoint: exchange an authorization code or refresh token. |
| `POST` | `/oauth/register` | — | OAuth dynamic client registration (RFC 7591). |
| `POST` | `/api/v1/identity/register` | — | ASP.NET Core Identity's built-in account endpoint. |
| `POST` | `/api/v1/identity/login` | — | ASP.NET Core Identity's built-in account endpoint. |
| `POST` | `/api/v1/identity/refresh` | — | ASP.NET Core Identity's built-in account endpoint. |
| `GET` | `/api/v1/identity/confirmEmail` | — | ASP.NET Core Identity's built-in account endpoint. |
| `POST` | `/api/v1/identity/resendConfirmationEmail` | — | ASP.NET Core Identity's built-in account endpoint. |
| `POST` | `/api/v1/identity/forgotPassword` | — | ASP.NET Core Identity's built-in account endpoint. |
| `POST` | `/api/v1/identity/resetPassword` | — | ASP.NET Core Identity's built-in account endpoint. |
| `POST` | `/api/v1/identity/manage/2fa` | — | ASP.NET Core Identity's built-in account endpoint. |
| `GET` | `/api/v1/identity/manage/info` | — | ASP.NET Core Identity's built-in account endpoint. |
| `POST` | `/api/v1/identity/manage/info` | — | ASP.NET Core Identity's built-in account endpoint. |

### Auth

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `POST` | `/api/v1/auth/token` | — | Exchange email/password for a JWT access token and refresh token |
| `POST` | `/api/v1/auth/token/refresh` | — | Exchange a refresh token for a new JWT access token and refresh token |
| `GET` | `/api/v1/auth/me` | — | Return the authenticated caller's identity (id, email, display name, roles). |
| `GET` | `/api/v1/auth/links` | `knowledge:read` | List the caller's linked accounts (AWS, Entra ID, GitHub, Atlassian, custom providers): whether each is linked, as whom, since when, and whether this server offers linking. |
| `GET` | `/api/v1/auth/pats` | `pats:manage` | List your personal access tokens, newest first, revoked ones included. |
| `POST` | `/api/v1/auth/pats` | `pats:manage` | Create a personal access token; its value is returned only here. |
| `DELETE` | `/api/v1/auth/pats/{id}` | `pats:manage` | Revoke one of your personal access tokens by id. |
| `GET` | `/api/v1/auth/users` | `admin:users` | List users with pagination (?skip=0&take=50) (Admin only) |
| `PUT` | `/api/v1/auth/users/{id}/roles` | `admin:users` | Replace a user's roles (Admin only). |
| `DELETE` | `/api/v1/auth/users/{id}` | `admin:users` | Delete a user (Admin only). |

### Invites

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `GET` | `/api/v1/invites` | `admin:users` | List pending invites with pagination (?skip=0&take=50) (Admin only). |
| `POST` | `/api/v1/invites` | `admin:users` | Invite someone with the Viewer, Editor or Admin role (Admin only; Admins only by the Owner). |
| `DELETE` | `/api/v1/invites/{id}` | `admin:users` | Revoke a pending invite, so its link stops working (Admin only). |

### Cloud Identity

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `GET` | `/api/v1/auth/cloud/aws/connect` | — | One step of linking your AWS account, opened by the browser, not called by scripts. |
| `POST` | `/api/v1/auth/cloud/aws/acs` | — | Where AWS IAM Identity Center posts the SAML answer when you link your AWS account. |
| `GET` | `/api/v1/auth/cloud/aws/confirm` | — | One step of linking your AWS account, opened by the browser, not called by scripts. |
| `GET` | `/api/v1/auth/cloud/azure/connect` | — | One step of linking your Microsoft account, opened by the browser, not called by scripts. |
| `GET` | `/api/v1/auth/cloud/azure/callback` | — | One step of linking your Microsoft account, opened by the browser, not called by scripts. |
| `GET` | `/api/v1/auth/cloud/azure/confirm` | — | One step of linking your Microsoft account, opened by the browser, not called by scripts. |
| `GET` | `/api/v1/auth/cloud/github/connect` | — | One step of linking your GitHub account, opened by the browser, not called by scripts. |
| `GET` | `/api/v1/auth/cloud/github/callback` | — | One step of linking your GitHub account, opened by the browser, not called by scripts. |
| `GET` | `/api/v1/auth/cloud/github/confirm` | — | One step of linking your GitHub account, opened by the browser, not called by scripts. |
| `GET` | `/api/v1/auth/cloud/atlassian/connect` | — | One step of linking your Atlassian account, opened by the browser, not called by scripts. |
| `GET` | `/api/v1/auth/cloud/atlassian/callback` | — | One step of linking your Atlassian account, opened by the browser, not called by scripts. |
| `GET` | `/api/v1/auth/cloud/atlassian/confirm` | — | One step of linking your Atlassian account, opened by the browser, not called by scripts. |
| `DELETE` | `/api/v1/auth/cloud/{provider}` | `knowledge:write` | Unlink your account for a built-in provider: `AWS`, `Azure`, `GitHub` or `Atlassian`. |
| `GET` | `/api/v1/auth/custom/{providerId}/connect` | — | One step of linking your custom source server account, opened by the browser, not called by scripts. |
| `GET` | `/api/v1/auth/custom/{providerId}/return` | — | One step of linking your custom source server account, opened by the browser, not called by scripts. |
| `DELETE` | `/api/v1/auth/custom/{providerId}` | `knowledge:write` | Unlink your account for a custom source provider. |

### Agents

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `POST` | `/api/v1/agents` | `admin:users` | Create a new agent (Admin only). |
| `GET` | `/api/v1/agents` | `admin:users` | List all agents (Admin only) |
| `GET` | `/api/v1/agents/{id}` | `admin:users` | Get agent details (Admin only) |
| `DELETE` | `/api/v1/agents/{id}` | `admin:users` | Delete an agent and revoke all its API keys (Admin only) |
| `PUT` | `/api/v1/agents/{id}/active` | `admin:users` | Enable or disable an agent (Admin only) |
| `GET` | `/api/v1/agents/{id}/keys` | `admin:users` | List API keys for an agent, revoked ones included (Admin only) |
| `POST` | `/api/v1/agents/{id}/keys` | `admin:users` | Create an API key for an agent, with scopes from knowledge:read and knowledge:write (the deprecated agent:ingest is accepted and dropped). |
| `DELETE` | `/api/v1/agents/{id}/keys/{keyId}` | `admin:users` | Revoke an agent API key (Admin only) |

### Containers

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `POST` | `/api/containers` | `knowledge:write` | Create a new container for organizing files |
| `GET` | `/api/containers` | `knowledge:read` | List containers with pagination (?skip=0&take=50) |
| `GET` | `/api/containers/{containerId}` | `knowledge:read` | Get a specific container by ID |
| `PATCH` | `/api/containers/{containerId}` | `knowledge:write` | Rename a container or change its description. |
| `DELETE` | `/api/containers/{containerId}` | `knowledge:write` | Delete a container. |
| `GET` | `/api/containers/by-name/{name}` | `knowledge:read` | Get a specific container by name |
| `GET` | `/api/containers/{containerId}/stats` | `knowledge:read` | Get statistics for a container: document counts by status, chunk count, storage size, embedding models, last indexed time |
| `GET` | `/api/containers/{containerId}/settings` | `knowledge:read` | Get per-container settings overrides |
| `PUT` | `/api/containers/{containerId}/settings` | `knowledge:write` | Save per-container settings overrides |
| `DELETE` | `/api/containers/{containerId}/settings` | `knowledge:write` | Clear per-container settings overrides, so the container follows the global settings |
| `POST` | `/api/containers/{containerId}/reindex` | `knowledge:write` | Reindex all documents in a container |
| `POST` | `/api/containers/{containerId}/retry-failed` | `knowledge:write` | Re-enqueue every document in a container whose ingestion failed, with a fresh attempt budget |
| `POST` | `/api/containers/{containerId}/sync` | `knowledge:write` | Reconcile a container's managed storage with the document table. |
| `POST` | `/api/containers/{containerId}/summaries` | `knowledge:write` | Queue a rollup of the container's summary from its documents' summaries. |
| `DELETE` | `/api/containers/{containerId}/summaries` | `knowledge:write` | Clear the container's summary and every document summary in it. |

### Sources

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `GET` | `/api/sources` | `knowledge:read` | List sources with pagination (?skip=0&take=50). |
| `POST` | `/api/sources` | `admin:sources` | Create a source inside an existing connection. |
| `GET` | `/api/sources/{sourceId}` | `knowledge:read` | Get a source's configuration summary and sync state. |
| `PATCH` | `/api/sources/{sourceId}` | `admin:sources` | Update a source's name, description, scope, sync interval, or enabled state. |
| `DELETE` | `/api/sources/{sourceId}` | `admin:sources` | Delete a source and its indexed documents. |
| `GET` | `/api/sources/{sourceId}/stats` | `knowledge:read` | Get statistics for a source: document counts by status, chunk count, storage size, last indexed time |
| `POST` | `/api/sources/{sourceId}/sync` | `admin:sources` | Run one sync cycle for a source immediately, rather than waiting for the next scheduled poll. |
| `POST` | `/api/sources/{sourceId}/retry-failed` | `admin:sources` | Re-enqueue every document of a source whose ingestion failed, with a fresh attempt budget, rather than waiting for the retry cooldown or a change to the file. |
| `GET` | `/api/v1/sources/scope-schema/{provider}` | `knowledge:read` | The JSON schema of each kind of scope a provider's sources take: the "scope" a create or update sends, with its "type". |

### Connections

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `GET` | `/api/v1/connections` | `admin:sources` | List connections by name (?skip=0&take=50, optionally ?provider=S3). |
| `POST` | `/api/v1/connections` | `admin:sources` | Create a filesystem, SFTP, S3, Azure Blob, GitHub or custom connection. |
| `GET` | `/api/v1/connections/{connectionId}` | `admin:sources` | Get a connection's settings, health and sync state. |
| `PATCH` | `/api/v1/connections/{connectionId}` | `admin:sources` | Rename a connection or change its settings. |
| `DELETE` | `/api/v1/connections/{connectionId}` | `admin:sources` | Delete a connection that no source uses. |
| `POST` | `/api/v1/connections/{connectionId}/pause` | `admin:sources` | Pause syncing for every source on a connection. |
| `POST` | `/api/v1/connections/{connectionId}/resume` | `admin:sources` | Resume syncing for the sources named in sourceIds, or every source on the connection. |
| `POST` | `/api/v1/connections/{connectionId}/test` | `admin:sources` | Test a connection against its provider: its stored settings, or settings given in the body before they are saved. |
| `GET` | `/api/v1/connections/{connectionId}/discover` | `admin:sources` | List what a connection reaches: an S3 connection's buckets, Azure storage accounts and the connection account's containers, the GitHub App's installations, or an Atlassian site's spaces (?includePersonal=true adds personal spaces) or projects. |
| `DELETE` | `/api/v1/connections/{connectionId}/host-key` | `admin:sources` | Forget the host key an SFTP connection pinned on first connect. |
| `PUT` | `/api/v1/connections/{connectionId}/secret` | `admin:sources` | Replace an SFTP connection's private key or a custom source server's token. |

### Search

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `POST` | `/api/sources/{sourceId}/search` | `knowledge:read` | Search a source. |
| `GET` | `/api/containers/{containerId}/search` | `knowledge:read` | Search within a container using query string parameters |
| `POST` | `/api/containers/{containerId}/search` | `knowledge:read` | Search within a container with advanced filters and options |
| `GET` | `/api/containers/{containerId}/search/models` | — | Get embedding models with vectors in this container |

### Audit

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `GET` | `/api/v1/audit` | `admin:audit` | Read the audit log, newest first (?take=50, up to 200); pass a page's nextBefore as ?before= for the next. |

### OAuth Clients

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `GET` | `/api/v1/oauth-clients` | `admin:oauth-clients` | List the OAuth clients with access: those that registered themselves, then those known by a metadata URL that hold grants, with their active grant and user counts. |
| `DELETE` | `/api/v1/oauth-clients/{clientId}` | `admin:oauth-clients` | Revoke an OAuth client: remove its registration, revoke its refresh tokens and delete its pending authorization codes. |

### Files

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `POST` | `/api/containers/{containerId}/files` | — | Upload one or more files to a container |
| `GET` | `/api/containers/{containerId}/files` | `knowledge:read` | List files and folders at a path within a container (?skip=0&take=50) |
| `GET` | `/api/containers/{containerId}/files/{fileId}` | `knowledge:read` | Get file details including indexing status |
| `DELETE` | `/api/containers/{containerId}/files/{fileId}` | `knowledge:write` | Delete a file and all associated chunks and vectors |
| `POST` | `/api/containers/{containerId}/files/bulk-delete` | `knowledge:write` | Delete up to 100 files in one call. |
| `GET` | `/api/containers/{containerId}/files/{fileId}/content` | `knowledge:read` | Get the full text content of a file. |
| `GET` | `/api/containers/{containerId}/files/{fileId}/reindex-check` | `knowledge:read` | Check if a specific file needs reindexing and why |
| `POST` | `/api/containers/{containerId}/files/{fileId}/retry` | `knowledge:write` | Queue one file's ingestion again, with a fresh attempt budget. |

### Folders

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `POST` | `/api/containers/{containerId}/folders` | `knowledge:write` | Create an empty folder in a container |
| `DELETE` | `/api/containers/{containerId}/folders` | `knowledge:write` | Delete a folder, optionally cascading to nested files and subfolders |

### Batches

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `GET` | `/api/batches/{id}/status` | `knowledge:read` | Get the status of a batch upload operation |

### Settings

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `GET` | `/api/settings/{category}` | `admin:settings` | Get settings for a specific category. |
| `PUT` | `/api/settings/{category}` | `admin:settings` | Update settings for a specific category. |
| `POST` | `/api/settings/test-connection` | `admin:settings` | Test connectivity to external services (Ollama, MinIO, etc.). |
| `GET` | `/api/settings/embedding-models` | `admin:settings` | Get all embedding models with vectors across all containers |
| `POST` | `/api/settings/reindex` | `admin:settings` | Start re-embedding in the background: without force, only documents whose embedding or chunking settings changed; with force, every document. |
| `GET` | `/api/settings/reindex/status` | `admin:settings` | Get current re-embedding/reindex queue status |
| `DELETE` | `/api/v1/settings/{category}` | `admin:settings` | Reset a settings category to its configured defaults by removing what is stored for it. |
| `POST` | `/api/v1/settings/summaries/clear` | `admin:settings` | Clear every container's summary and every document summary; the next sweep writes them again. |

### Providers

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| `GET` | `/api/v1/providers` | `admin:settings` | List each built-in provider (aws, azure, github, atlassian) with its requirements and their status. |
| `GET` | `/api/v1/providers/aws/access` | `admin:settings` | Read Connapse's stored IAM Roles Anywhere credential: certificate, ARNs and region, with when it was saved and last verified. |
| `PUT` | `/api/v1/providers/aws/access` | `admin:settings` | Set Connapse's AWS access to an IAM Roles Anywhere credential: certificate, trust anchor, profile and role ARNs, region and private key. |
| `DELETE` | `/api/v1/providers/aws/access` | `admin:settings` | Remove Connapse's stored AWS credential. |
| `PUT` | `/api/v1/providers/aws/identity-center` | `admin:settings` | Record the IAM Identity Center instance per-user AWS permissions are resolved against: its region, instance ARN and identity store id. |
| `DELETE` | `/api/v1/providers/aws/identity-center` | `admin:settings` | Clear the recorded IAM Identity Center instance. |
| `PUT` | `/api/v1/providers/aws/permissions` | `admin:settings` | Save the IAM Identity Center SAML application people sign in through: Connapse's entityId and acsUrl, and Identity Center's idpEntityId, idpSingleSignOnUrl and idpSigningCertificate. |
| `DELETE` | `/api/v1/providers/aws/permissions` | `admin:settings` | Clear the recorded SAML application, for one removed in AWS. |
| `PUT` | `/api/v1/providers/azure/access` | `admin:settings` | Set Connapse's Azure access identity: a certificate app (clientId and clientCertificatePath, a file on this host), a user-assigned managed identity, or the host's own (useHostManagedIdentity), with its tenantId and the subscriptionId per-user filtering reads. |
| `DELETE` | `/api/v1/providers/azure/access` | `admin:settings` | Clear Connapse's Azure access identity. |
| `PUT` | `/api/v1/providers/azure/permissions` | `admin:settings` | Save the Entra application people sign in through: tenantId, clientId, redirectUri and clientCertificatePath. |
| `DELETE` | `/api/v1/providers/azure/permissions` | `admin:settings` | Clear the recorded sign-in application, for one removed in Entra. |
| `GET` | `/api/v1/providers/custom` | `admin:settings` | List the custom providers, each with the connections that check permissions with it. |
| `POST` | `/api/v1/providers/custom` | `admin:settings` | Add a custom provider. |
| `PATCH` | `/api/v1/providers/custom/{providerId}` | `admin:settings` | Change a custom provider's name, address or token; what is left out stays. |
| `DELETE` | `/api/v1/providers/custom/{providerId}` | `admin:settings` | Delete a custom provider with its token and link secret. |
| `POST` | `/api/v1/providers/custom/{providerId}/check` | `admin:settings` | Check a custom provider's source server, with its saved address and token or the ones given, without saving anything. |
| `PUT` | `/api/v1/providers/custom/{providerId}/link-secret` | `admin:settings` | Generate a new link secret for a custom provider, so its users can link their accounts. |
| `GET` | `/api/v1/providers/github/app` | `admin:settings` | Read the stored GitHub App: its id, slug, client id, owner and address, with when it was saved and last verified. |
| `PUT` | `/api/v1/providers/github/app` | `admin:settings` | Store a GitHub App registered by hand: appId, privateKeyPem and, for people to link their GitHub accounts, clientSecret. |
| `DELETE` | `/api/v1/providers/github/app` | `admin:settings` | Remove the GitHub App's key and secret from Connapse. |
| `POST` | `/api/v1/providers/github/manifest` | `admin:settings` | Start creating Connapse's GitHub App through GitHub's manifest flow, owned by the caller's account or by organisation, which GitHub must confirm is one. |
| `GET` | `/api/v1/providers/github/manifest/callback` | `admin:settings` | Where GitHub returns after creating the GitHub App from its manifest. Opened by the browser. |
| `GET` | `/api/v1/providers/github/installed` | `admin:settings` | Where GitHub returns after the GitHub App is installed. Opened by the browser. |
| `POST` | `/api/v1/atlassian/sites/resolve` | `admin:sources` | Look up an Atlassian site's cloud id from its URL. |
| `POST` | `/api/v1/atlassian/sites` | Administrator | Add an Atlassian site connection, checking the service account's credential first. |
| `GET` | `/api/v1/atlassian/sites` | `admin:sources` | List Atlassian site connections. |
| `GET` | `/api/v1/atlassian/sites/{connectionId}` | `admin:sources` | Get one Atlassian site connection. |
| `PATCH` | `/api/v1/atlassian/sites/{connectionId}` | `admin:sources` | Rename an Atlassian site connection or change its settings. |
| `DELETE` | `/api/v1/atlassian/sites/{connectionId}` | `admin:sources` | Delete an Atlassian site connection. Refused while sources use it. |
| `POST` | `/api/v1/atlassian/sites/{connectionId}/pause` | `admin:sources` | Pause syncing for every source on the site. |
| `POST` | `/api/v1/atlassian/sites/{connectionId}/resume` | `admin:sources` | Resume syncing for the site's sources. |
| `POST` | `/api/v1/atlassian/sites/{connectionId}/test` | `admin:sources` | Test the site's credential, step by step. |
| `PUT` | `/api/v1/atlassian/sites/{connectionId}/credentials` | `admin:sources` | Replace the service account's credential. |
| `GET` | `/api/v1/atlassian/sites/{connectionId}/spaces` | Administrator | List the Confluence spaces the service account can read. |
| `GET` | `/api/v1/atlassian/sites/{connectionId}/projects` | Administrator | List the Jira projects the service account can read. |
| `GET` | `/api/v1/atlassian/linking-app` | Administrator | Get the Atlassian linking app. See [Atlassian linking app](#atlassian-linking-app). |
| `PUT` | `/api/v1/atlassian/linking-app` | Administrator | Save or replace the Atlassian linking app. |
| `DELETE` | `/api/v1/atlassian/linking-app` | Administrator | Remove the Atlassian linking app. |
| `POST` | `/api/v1/atlassian/linking-app/check` | Administrator | Check the stored linking app with Atlassian again. |
| `PUT` | `/api/v1/atlassian/linking-app/sign-in` | Administrator | Turn Atlassian sign-in on or off. |

## Atlassian linking app

Base path: `/api/v1/atlassian/linking-app`. Administrator only. The app people sign in through to link their Atlassian accounts, at parity with the Linking app card on the Providers page: the same check with Atlassian before saving, the same typed confirmation to remove it, and the same audit events. The client secret is accepted on the way in and never returned.

| Method | Route | Purpose |
|--------|-------|---------|
| GET | `/` | `{ clientId, permission, signInEnabled, lastCheckedAt, lastCheckAccepted, lastUsedForSignInAt, peopleLinked }`. `lastCheckAccepted` is `null` before a check; `peopleLinked` is `null` if it can't be counted. `404` when no app is stored |
| PUT | `/` | `{ "clientId", "clientSecret", "replaceExisting"? }`. Asks Atlassian whether it accepts the pair and stores it only if it does. Saving turns sign-in on. When an app is already stored, `replaceExisting: true` is required, as the page asks you to choose **Check and replace**: every new sign-in then goes through the new app, and people who linked keep their links. Without it the answer is `409` with `{ error, field: "replaceExisting" }`, before Atlassian is asked and with nothing changed. `200` with the app as `GET` returns it. `400` if a field is missing, `422` with `{ error, troubleshootingUrl, detail }` if Atlassian rejects the pair, `502` with `{ error, troubleshootingUrl, detail }` if Atlassian can't be reached or gives no clear answer. `detail` is Atlassian's own answer, as the card's **Details** shows it: the status and Atlassian's error code (such as `HTTP 401, access_denied`), or why there was none; never the secret. Nothing is stored on a failure. Audited as `provider.credential.check_started` and `provider.credential.checked` (sharing an attempt id, with the time the check took), then `provider.credential.saved` |
| POST | `/check` | Checks the stored pair with Atlassian again, as **Recheck** on the card does, and records the answer as the last check: `200` with `{ result, message, troubleshootingUrl, detail }`, where `result` is `accepted`, `rejected` or `unknown` (couldn't reach Atlassian; nothing recorded), `troubleshootingUrl` is the guide entry (`null` when accepted) and `detail` is Atlassian's own answer as on the card (`null` when accepted). `404` when no app is stored. Audited as `provider.credential.check_started` and `provider.credential.checked` |
| PUT | `/sign-in` | `{ "signInEnabled": false }` turns Atlassian sign-in off: nobody new can link, and people who linked keep their links. `true` turns it back on. `200` with the app. `400` without `signInEnabled`, `404` when no app is stored. Audited as `provider.sign_in.disabled` or `provider.sign_in.enabled` |
| DELETE | `/?confirmClientId=<client ID>` | Removes the app. `confirmClientId` must equal the stored client ID, the value the page asks you to type, or the answer is `400`. People who linked keep their links, but nobody can link until an app is added again. `204` when removed, `404` when none is stored. Audited as `provider.credential.deleted` |

## Related

- [Automate Connapse with the API](../guides/automate-with-the-api.md)
- [Problem types](../problems.md)
- [Roles and scopes](roles-and-scopes.md)
- [MCP tools](mcp-tools.md)
