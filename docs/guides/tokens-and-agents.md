---
title: Create tokens and agent keys
type: how-to
audience: admin
---

# Create tokens and agent keys

Scripts and AI agents reach Connapse with a key sent in the `X-Api-Key` header. There are two kinds:

- A **personal access token** acts as you. It can do only what its scopes allow, and never more than your role does. Use it for your own scripts and for calling the REST API.
- An **agent key** belongs to an agent: an identity for software rather than a person, which an administrator creates. Agent keys reach Connapse over MCP only, and revoking them never touches anyone's account. Use one for an AI agent that runs on its own.

Both kinds start with `cnp_` and are shown once, when you create them.

## Prerequisites

- For a personal access token: any signed-in account.
- For an agent key: you are the Owner or an Admin.

## Steps

### Create a personal access token

1. Open **API Tokens** in the navigation (`/settings/tokens`) and choose **New Token**.
2. Enter a **Name**, such as `backup-script`, and optionally an **Expires** date.
3. Tick the **Scopes** the token needs. `knowledge:read` is ticked to start with; the [scope table](#scopes) says what each one allows.
4. Leave **Ask me before it deletes anything over MCP** ticked unless the token will never be used by an agent.
5. Choose **Create Token** and copy the token.

To revoke it, choose **Revoke** on its row, then **Confirm Revoke**. Only you can see and revoke your tokens, and only while signed in yourself: a token can't create or revoke tokens.

### Create an agent and its key

1. Open **Agents** in the navigation (`/admin/agents`).
2. Under **New Agent**, enter a **Name** of 2 to 64 letters, digits, hyphens and underscores, and optionally a **Description**. Choose **Create Agent**.
3. On the agent's row, choose **API Keys**, enter a **Key name**, and tick its scopes. An agent key can hold only `knowledge:read`, `knowledge:write` and `agent:ingest`.
4. Leave **Ask before it deletes anything over MCP** ticked, then choose **Add Key** and copy the key.

**Disable** on the agent's row stops all of its keys until you choose **Enable**. **Delete** removes the agent and revokes every key it has.

### Scopes

A token passes a check only with a scope it was issued with, and a scope never lets it do more than its owner's role allows.

| Scope | Allows |
|-------|--------|
| `knowledge:read` | Search, list and read containers and documents |
| `knowledge:write` | Create and delete containers, and upload and delete files |
| `agent:ingest` | Nothing yet: no operation checks it. Give `knowledge:write` to upload and delete |
| `admin:users` | Manage users, invites and agents |
| `admin:audit` | Read the audit log |
| `admin:oauth-clients` | List and revoke OAuth clients |
| `admin:settings` | Read and change settings and providers |
| `admin:sources` | Manage connections and sources |

The `admin:` scopes do something only on a token of an Owner or Admin. The full table, per operation, is in [Roles and scopes](../reference/roles-and-scopes.md).

### Approve a held delete

When a key has **Ask before it deletes anything over MCP**, a delete it asks for over MCP is held rather than run. It appears on **Approvals** (`/approvals`), where you choose **Approve** or **Deny**. Once approved, the agent repeats the same call within 15 minutes and it runs once. You see requests from your own tokens; an administrator sees everyone's.

## Do it with the API

Creating a personal access token needs a signed-in session, so do it in the browser. Agents and their keys can be managed with an Owner's or Admin's token holding `admin:users`:

```bash
curl -X POST http://localhost:5001/api/v1/agents \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{ "name": "nightly-ingest", "description": "Uploads the build reports" }'

curl -X POST http://localhost:5001/api/v1/agents/AGENT_ID/keys \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{ "name": "ci", "scopes": ["knowledge:read", "knowledge:write"], "requiresApproval": true }'
```

The second answer holds the key, which is never shown again. `PUT /api/v1/agents/{id}/active` with `{ "isActive": false }` disables an agent, `DELETE /api/v1/agents/{id}/keys/{keyId}` revokes a key, and `DELETE /api/v1/agents/{id}` deletes the agent.

## Related

- [Connect an MCP client](mcp-clients.md)
- [Tutorial: connect Claude to Connapse](../get-started/connect-an-agent.md)
- [Roles and scopes](../reference/roles-and-scopes.md)
