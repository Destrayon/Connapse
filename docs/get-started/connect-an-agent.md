---
title: "Tutorial: connect Claude to Connapse"
type: tutorial
audience: user
---

# Tutorial: connect Claude to Connapse

In this tutorial we'll give Claude Code its own key to Connapse, connect it over MCP (the Model Context Protocol, the standard way AI agents call tools), and have it build and search a container for us. You need a running Connapse server where you are the Owner or an Admin; the [quickstart](quickstart.md) gets you one.

## Before you begin

- Connapse running at `http://localhost:5001`, signed in as the Owner or an Admin.
- [Claude Code](https://docs.claude.com/en/docs/claude-code) installed, so `claude --version` prints a version.

## Create an agent

An agent is an identity for software rather than a person. It gets its own keys, which you can revoke without touching your own account.

1. In the navigation, open **Admin** → **Agents**.
2. Under **New Agent**, enter `claude-code` as the name, then choose **Create Agent**.

Notice that `claude-code` now appears in the list with no API keys.

## Give the agent a key

1. On the `claude-code` row, choose **API Keys**.
2. Enter `laptop` as the key name.
3. Tick `knowledge:write` beside the already ticked `knowledge:read`, so the agent can create containers and upload files.
4. Leave **Ask before it deletes anything over MCP** ticked.
5. Choose **Add Key**, then copy the key. It starts with `cnp_`.

Notice that Connapse shows the key only once. If you lose it, revoke it and add another.

## Connect Claude Code

Run this in a terminal, with your key in place of `cnp_your_key`:

```bash
claude mcp add --transport http connapse http://localhost:5001/mcp --header "X-Api-Key: cnp_your_key"
```

Then start Claude Code and list its servers:

```bash
claude
```

```text
/mcp
```

Notice that `connapse` is listed as connected. Its tools are the ones the key's scopes allow, so a read-only key would show only the search and read tools.

## Build a container

Ask Claude:

```text
Create a Connapse container called agent-notes, then upload a Markdown file named retries.md to it that explains exponential backoff with jitter in three short paragraphs.
```

Claude calls `container_create`, then `upload_file`. Notice that the file appears in the `agent-notes` container on the **Files** page, and its status turns to **Ready** within a minute.

## Search it

Ask Claude:

```text
Search the agent-notes container for why jitter matters, and quote the passage you found.
```

Claude calls `search_knowledge` and answers from the passage, naming `retries.md` as the source.

## Approve a delete

Ask Claude:

```text
Delete retries.md from the agent-notes container.
```

Notice that Claude doesn't delete anything yet. Connapse holds the call and Claude tells you to approve it at `/approvals`. Open **Approvals** in the navigation, approve the request, then tell Claude to try again. The second identical call goes through.

## What you built

Claude Code now reaches Connapse with its own revocable key, scoped to reading and writing knowledge, and every delete it attempts waits for a person to approve it.

## Next steps

- [Connect other MCP clients](../guides/mcp-clients.md), such as Claude Desktop, VS Code and Cursor, or sign in with OAuth instead of a key.
- [Manage tokens and agent keys](../guides/tokens-and-agents.md).
- [MCP tools reference](../reference/mcp-tools.md) lists every tool and the scope each one needs.
