# Connapse — Quick Start

This directory contains everything you need to run Connapse using Docker Compose.
No source code or .NET SDK required.

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) 24+ with the Compose plugin 2.24+ (`docker compose`)
- Internet access to pull images from ghcr.io, cgr.dev and Docker Hub on first run

## Quick Start

**1. Start Connapse.**

```bash
docker compose up -d
```

That's the whole setup, with no file to edit. This starts PostgreSQL, MinIO (object storage),
Ollama and the Connapse web application. The first start makes random passwords, keeps them in
a Docker volume, and downloads the embedding model, which turns your documents into vectors.
Connapse shows the download's progress, and uploads wait for it.

**2. Create the Owner account.**

Open [http://localhost:5001](http://localhost:5001). A new server has no accounts, so it shows
the setup form. Enter your email, a display name and a password. The first account becomes the
**Owner**, and everyone else joins through invites.

**3. Create your first container and upload documents.**

- On the **Files** page, choose **New Container**, give it a name and create it. Uploads go to
  the MinIO storage that ships with this package.
- Open the container and upload files. Each one shows **Ready** once it is indexed.
- Use the **Search** page to query it.

To index Amazon S3, Azure Blob Storage, GitHub, Confluence, Jira or an SFTP server
instead, add it as a source. See [Containers, connections and sources](https://github.com/Destrayon/Connapse/blob/main/docs/sources/index.md).

## Changing what runs

Copy `.env.example` to `.env`, change what you want, and run `docker compose up -d` again.

- `CONNAPSE_LOCAL_MODEL=off` never downloads or starts Ollama.
- `COMPOSE_PROFILES=with-reranker` starts the reranker and turns reranking on.

## Using a different embedding provider

To use an Ollama you already run, or OpenAI or Azure OpenAI, set `CONNAPSE_LOCAL_MODEL=off` in
`.env`, then set the provider there too, or choose it under **Admin** → **Settings** →
**Embedding**. For your own Ollama, set `Knowledge__Embedding__BaseUrl` in `.env` to its address.

## Pinning to a specific version

Set `CONNAPSE_VERSION` in your `.env` file:

```bash
CONNAPSE_VERSION=v0.4.0
```

Then re-pull and restart:

```bash
docker compose pull web
docker compose up -d
```

## Upgrading

This file is pinned to the release it came with, so pulling alone keeps that release. Back up first, then set the
new release in `.env` (or download the new release's `docker-compose.yml` over this one), and pull and restart:

```bash
# in .env
CONNAPSE_VERSION=v0.4.1
```

```bash
docker compose pull web
docker compose up -d
```

Migrations run automatically on startup and only go forward. Your data volumes (`pgdata`,
`miniodata`, `appdata`, `secrets`) are preserved across upgrades, and your passwords don't change.
If you use a hosted embedding provider, set `CONNAPSE_LOCAL_MODEL=off` first. Upgrading from v0.3.x needs extra steps;
see [Upgrade Connapse](https://github.com/Destrayon/Connapse/blob/main/docs/self-host/upgrade.md).

## Stopping and removing

```bash
# Stop without removing data
docker compose down

# Stop and remove all data volumes (destructive)
docker compose down -v
```

## Port reference

| Service   | Host port | Purpose                  |
|-----------|-----------|--------------------------|
| web       | 5001      | Connapse web UI and API  |

MinIO, PostgreSQL and Ollama are only accessible within the internal Docker network.

## Further reading

- [Documentation](https://github.com/Destrayon/Connapse/blob/main/docs/index.md)
- [Run Connapse in production](https://github.com/Destrayon/Connapse/blob/main/docs/self-host/production.md)
- [Full project repository](https://github.com/Destrayon/Connapse)
