---
title: Connapse documentation
type: landing
audience: all
---

# Connapse documentation

Connapse is a self-hosted knowledge platform: it indexes your documents and the systems you already use, and lets people, scripts and AI agents search them. Pick the part that matches what you're doing.

## Get started

**For anyone trying Connapse for the first time.** Run it on your machine, upload a document and search it, then connect an AI agent.

- [Quickstart](get-started/quickstart.md)
- [Tutorial: connect Claude to Connapse](get-started/connect-an-agent.md)

## Self-host

**For the person who runs the server.** Install it, make it safe to expose, keep it backed up and up to date, and fix it when it breaks.

- [Install Connapse](self-host/install.md)
- [Run Connapse in production](self-host/production.md)
- [Back up and restore Connapse](self-host/backup-and-restore.md)
- [Upgrade Connapse](self-host/upgrade.md)
- [Troubleshoot a self-hosted server](self-host/troubleshooting.md)

## Connect your data

**For administrators adding content.** Start with how containers, connections and sources fit together, then set up each provider.

- [Containers, connections and sources](sources/index.md)
- [Use uploads and MinIO](sources/uploads-and-minio.md)
- [Index a filesystem or SFTP server](sources/filesystem-and-sftp.md)
- [AWS setup](sources/aws-setup.md) and [buckets outside the Identity Center region](sources/aws-multi-region.md)
- [Azure setup](sources/azure-setup.md)
- [GitHub setup](sources/github-setup.md)
- [Atlassian setup](sources/atlassian-setup.md) for Confluence and Jira
- [Custom source setup](sources/custom-source-setup.md)

## Guides

**For a task you want done.** Each guide is a set of steps, with the same steps over the API at the end.

- [Manage users and invites](guides/users-and-invites.md)
- [Create tokens and agent keys](guides/tokens-and-agents.md)
- [Connect an MCP client](guides/mcp-clients.md)
- [Automate Connapse with the API](guides/automate-with-the-api.md)
- [Tune search](guides/tune-search.md)
- [Change the embedding model](guides/change-embedding-model.md)

## Concepts

**For understanding why Connapse behaves the way it does.** No steps, only explanation.

- [How Connapse works](concepts/how-connapse-works.md)
- [About search](concepts/search.md)
- [About ingestion](concepts/ingestion.md)
- [About permissions](concepts/permissions.md)

## Reference

**For looking up an exact name, value or limit.**

- [REST API](reference/rest-api.md) and the [OpenAPI document](api/openapi.json)
- [MCP tools](reference/mcp-tools.md)
- [Configuration](reference/configuration.md)
- [Roles and scopes](reference/roles-and-scopes.md)
- [File types](reference/file-types.md)
- [Error types](problems.md)
- [Custom source protocol](protocols/custom-source-v1.md)
- [Command-line tool](reference/cli.md)

## Contributing

**For people changing Connapse itself.** Start with [Set up a development environment](contributing/development.md) and [Architecture internals](contributing/architecture.md). The `plans/`, `research/` and `superpowers/` folders are internal working notes, not user documentation.
