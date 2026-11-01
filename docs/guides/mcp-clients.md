---
title: Connect an MCP client
type: how-to
audience: user
---

# Connect an MCP client

Connapse serves MCP (the Model Context Protocol, the standard way AI agents call tools) at `/mcp`, over Streamable HTTP. This guide shows how to point Claude Code, Claude Desktop, VS Code and Cursor at it. For a guided first run, follow the [tutorial](../get-started/connect-an-agent.md) instead.

## Prerequisites

- Connapse running, here at `http://localhost:5001`.
- A way to sign in to `/mcp`. It admits two kinds of caller:
  - **A key in the `X-Api-Key` header.** Use an [agent key](tokens-and-agents.md#create-an-agent-and-its-key), or a personal access token of an Owner or Admin. Tokens of Editors and Viewers are refused at `/mcp`.
  - **OAuth.** A client that supports MCP's OAuth sign-in registers itself, opens Connapse's **Authorization Request** page in your browser, and gets a token for your account with `knowledge:read`, `knowledge:write` or both. You must be an Owner or Admin.

The tools a client sees are the ones its scopes allow: a `knowledge:read` key gets the search and read tools only. The [MCP tools reference](../reference/mcp-tools.md) lists them.

## Steps

### Claude Code

With a key:

```bash
claude mcp add --transport http connapse http://localhost:5001/mcp --header "X-Api-Key: cnp_your_key"
```

With OAuth, leave the header out, then run `/mcp` in Claude Code and choose **Authenticate** on `connapse`.

### Claude Desktop

Claude Desktop's custom connectors are reached from Anthropic's servers, so they need Connapse at a public HTTPS address. Add one under **Settings** → **Connectors** → **Add custom connector** with `https://connapse.example.com/mcp`, and sign in through OAuth when asked.

For a Connapse on your own machine, run the client through `mcp-remote` instead. In `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "connapse": {
      "command": "npx",
      "args": ["mcp-remote", "http://localhost:5001/mcp", "--header", "X-Api-Key:${CONNAPSE_KEY}"],
      "env": { "CONNAPSE_KEY": "cnp_your_key" }
    }
  }
}
```

### VS Code

In `.vscode/mcp.json`. VS Code asks for the key the first time and keeps it out of the file:

```json
{
  "inputs": [
    { "type": "promptString", "id": "connapse-key", "description": "Connapse key", "password": true }
  ],
  "servers": {
    "connapse": {
      "type": "http",
      "url": "http://localhost:5001/mcp",
      "headers": { "X-Api-Key": "${input:connapse-key}" }
    }
  }
}
```

### Cursor

In `~/.cursor/mcp.json`, or `.cursor/mcp.json` in a project:

```json
{
  "mcpServers": {
    "connapse": {
      "url": "http://localhost:5001/mcp",
      "headers": { "X-Api-Key": "cnp_your_key" }
    }
  }
}
```

### Other clients

Any client that speaks Streamable HTTP works with the address and header above. A client that runs in a web page sends an `Origin` header, and Connapse refuses `/mcp` requests from any origin other than `localhost` unless an administrator lists it in `Cors:AllowedOrigins` (for example `Cors__AllowedOrigins__0=https://app.example.com`). Desktop and command-line clients send no `Origin`, so this never affects them.

## Approve deletes

A key created with **Ask before it deletes anything over MCP** has each delete held. The client reports that the call needs approval; approve or deny it on **Approvals** (`/approvals`), and the agent repeats the call within 15 minutes. See [Approve a held delete](tokens-and-agents.md#approve-a-held-delete).

## Revoke access

Revoke a key on **API Tokens** or **Agents**. An OAuth client is revoked with `DELETE /api/v1/oauth-clients/{clientId}` (an administrator, with `admin:oauth-clients`): its refresh tokens stop working at once, and the access tokens it holds expire within their short lifetime.

## Related

- [Tokens and agents](tokens-and-agents.md)
- [MCP tools](../reference/mcp-tools.md)
- [Tutorial: connect Claude to Connapse](../get-started/connect-an-agent.md)
