---
title: Back up and restore Connapse
type: how-to
audience: operator
---

# Back up and restore Connapse

Use this when you want a copy of a Connapse server you can restore after a lost disk, a bad upgrade or a move to new hardware.

## Prerequisites

- Shell access to the Docker host, in the folder that holds `docker-compose.yml`.
- Enough free disk for a copy of the database and the uploaded files.

## What to back up

A complete backup has four parts. Restoring without any one of them loses data.

| Part | Where it lives | What it holds |
|---|---|---|
| Database | the `postgres` service | Accounts, containers, documents, chunks, vectors, settings, sources and audit log |
| Files | the `minio` service, bucket `connapse-files` | The original uploaded files |
| App data | the `appdata` volume, at `/app/appdata` in `web` | The keys that encrypt stored identity data, the generated JWT secret, GitHub mirrors and Atlassian sync state |
| `.env` | next to `docker-compose.yml` | Passwords and settings the services start with |

Without the app data volume, Connapse can't decrypt the cloud identities and connection secrets it stored, and everyone must sign in again.

## Back up

1. Dump the database while Connapse runs:

   ```bash
   mkdir -p backup
   docker compose exec -T postgres pg_dump -U connapse -Fc connapse > backup/connapse.dump
   ```

2. Stop the web app and MinIO so the files and app data don't change during the copy:

   ```bash
   docker compose stop web minio
   ```

3. Copy the file store and the app data:

   ```bash
   docker run --rm --volumes-from "$(docker compose ps -aq minio)" -v "$PWD/backup":/backup alpine tar czf /backup/minio.tgz -C /data .
   docker run --rm --volumes-from "$(docker compose ps -aq web)" -v "$PWD/backup":/backup alpine tar czf /backup/appdata.tgz -C /app/appdata .
   ```

4. Start everything again, with the profiles you normally use:

   ```bash
   docker compose --profile with-ollama up -d
   ```

5. Copy `.env` into `backup/`, then move the `backup` folder off the host.

To back up nightly, put steps 1 to 5 in a script and run it from cron at a quiet hour. Expect the web app to be down for as long as the copy in step 3 takes.

## Restore

These steps restore onto a host with the same compose files and an empty set of volumes.

1. Put the backed-up `.env` next to `docker-compose.yml`.

2. Start only the database and the file store:

   ```bash
   docker compose up -d postgres minio
   ```

3. Restore the database:

   ```bash
   docker compose exec -T postgres pg_restore -U connapse -d connapse --clean --if-exists < backup/connapse.dump
   ```

4. Create the web container without starting it, stop MinIO, then restore the file store and the app data:

   ```bash
   docker compose create web
   docker compose stop minio
   docker run --rm --volumes-from "$(docker compose ps -aq minio)" -v "$PWD/backup":/backup alpine tar xzf /backup/minio.tgz -C /data
   docker run --rm --volumes-from "$(docker compose ps -aq web)" -v "$PWD/backup":/backup alpine tar xzf /backup/appdata.tgz -C /app/appdata
   ```

5. Start everything:

   ```bash
   docker compose --profile with-ollama up -d
   ```

6. Sign in and run a search in a container you know. If you use Ollama, pull the embedding model again first with `docker compose exec ollama ollama pull nomic-embed-text`.

Sources such as S3 or GitHub don't need their content restored. Their next sync brings the index up to date with the system they mirror.

## Do it with the API

Backups have no API equivalent. They run against the host's volumes and the database directly.

## Related

- [Upgrade Connapse](upgrade.md)
- [Run Connapse in production](production.md)
