# Connapse — Quick Start

This directory contains everything you need to run Connapse using Docker Compose.
No source code or .NET SDK required.

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) 24+ with the Compose plugin (`docker compose`)
- Internet access to pull images from ghcr.io and Docker Hub on first run

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

**2. Start Connapse.**

```bash
docker compose up -d
```

This starts three services: PostgreSQL, MinIO (object storage), and the Connapse web application.
On first startup, database migrations run automatically and the admin account is created.

**3. Open the browser.**

Navigate to [http://localhost:5001](http://localhost:5001) and log in with the
`CONNAPSE_ADMIN_EMAIL` and `CONNAPSE_ADMIN_PASSWORD` values you set in `.env`.

**4. Create your first container and upload documents.**

- Click **New Container** and choose a connector type (MinIO is pre-configured).
- Upload files — Connapse will ingest and embed them automatically.
- Use the **Search** page to query your knowledge base.

## Optional: Local Ollama (self-hosted embeddings)

If you want to run Ollama locally for embeddings instead of a remote provider:

```bash
docker compose --profile with-ollama up -d
```

After Ollama starts, pull a model:

```bash
docker compose exec ollama ollama pull nomic-embed-text
```

Then in the Connapse Settings > Embedding page, set the model to `nomic-embed-text`
and the base URL to `http://ollama:11434` (already the default in `.env.example`).

## Pinning to a specific version

Set `CONNAPSE_VERSION` in your `.env` file:

```bash
CONNAPSE_VERSION=v0.3.0
```

Then re-pull and restart:

```bash
docker compose pull web
docker compose up -d
```

## Upgrading

```bash
docker compose pull web
docker compose up -d
```

Migrations run automatically on startup. Your data volumes (`pgdata`, `miniodata`, `appdata`)
are preserved across upgrades.

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

MinIO and PostgreSQL are only accessible within the internal Docker network.

## Further reading

- [Full project repository](https://github.com/Destrayon/Connapse)
- [Connectors documentation](https://github.com/Destrayon/Connapse/blob/main/docs/connectors.md)
- [AWS SSO setup](https://github.com/Destrayon/Connapse/blob/main/docs/aws-sso-setup.md)
- [Azure identity setup](https://github.com/Destrayon/Connapse/blob/main/docs/azure-identity-setup.md)
