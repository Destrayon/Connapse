# Connapse — Quick Start

This directory contains everything you need to run Connapse using Docker Compose.
No source code or .NET SDK required.

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) 24+ with the Compose plugin (`docker compose`)
- Internet access to pull images from ghcr.io, cgr.dev and Docker Hub on first run

## Quick Start

**1. Copy the example environment file and edit it.**

```bash
cp .env.example .env
```

Open `.env` in a text editor and set every value marked `change_me_*`.
Generate a secure JWT secret with:

```bash
openssl rand -base64 64
```

**2. Start Connapse with Ollama.**

Ollama runs the embedding model that turns your documents into vectors.

```bash
docker compose --profile with-ollama up -d
```

This starts PostgreSQL, MinIO (object storage), Ollama and the Connapse web application.
Database migrations run automatically when the web app starts.

**3. Download the embedding model.**

```bash
docker compose exec ollama ollama pull nomic-embed-text
```

`nomic-embed-text` is the default embedding model. Connapse doesn't download it for you.

**4. Create the Owner account.**

Open [http://localhost:5001](http://localhost:5001). A new server has no accounts, so it shows
the setup form. Enter your email, a display name and a password. The first account becomes the
**Owner**, and everyone else joins through invites.

**5. Create your first container and upload documents.**

- On the **Files** page, choose **New Container**, give it a name and create it. Uploads go to
  the MinIO storage that ships with this package.
- Open the container and upload files. Each one shows **Ready** once it is indexed.
- Use the **Search** page to query it.

To index Amazon S3, Azure Blob Storage, GitHub, Confluence, Jira, a filesystem or an SFTP server
instead, add it as a source. See [Containers, connections and sources](https://github.com/Destrayon/Connapse/blob/main/docs/sources/index.md).

## Using a different embedding provider

To use an Ollama you already run, or OpenAI or Azure OpenAI, skip `--profile with-ollama` and
step 3, then choose the provider under **Admin** → **Settings** → **Embedding**. For your own
Ollama, set `Knowledge__Embedding__BaseUrl` in `.env` to its address.

## Pinning to a specific version

Set `CONNAPSE_VERSION` in your `.env` file:

```bash
CONNAPSE_VERSION=v0.4.0
```

Then re-pull and restart:

```bash
docker compose pull web
docker compose --profile with-ollama up -d
```

## Upgrading

Back up first, then pull and restart with the profiles you use:

```bash
docker compose pull web
docker compose --profile with-ollama up -d
```

Migrations run automatically on startup and only go forward. Your data volumes (`pgdata`,
`miniodata`, `appdata`) are preserved across upgrades. Upgrading from v0.3.x needs extra steps;
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
