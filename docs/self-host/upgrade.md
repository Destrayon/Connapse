---
title: Upgrade Connapse
type: how-to
audience: operator
---

# Upgrade Connapse

Use this when you want to move a running Connapse server to a newer release, and to get back to the old one if the upgrade goes wrong.

## Prerequisites

- Shell access to the Docker host, in the folder that holds `docker-compose.yml`.
- A backup from the last hour, made with [Back up and restore Connapse](backup-and-restore.md). Rolling back means restoring it.
- The [CHANGELOG](../../CHANGELOG.md) entries between your version and the new one, read for breaking changes and security fixes.

## Steps

1. Back up the server, following [Back up and restore Connapse](backup-and-restore.md).

2. Get the new version.

   If you run the prebuilt image, set the release in `.env` and pull it:

   ```bash
   # in .env
   CONNAPSE_VERSION=v0.4.0
   ```

   ```bash
   docker compose pull web
   ```

   If you build from source, check out the release tag:

   ```bash
   git fetch --tags
   git checkout v0.4.0
   ```

3. Restart with the new version, with the profiles you normally use. Add `--build` when you build from source:

   ```bash
   docker compose --profile with-ollama up -d --build
   ```

   Database migrations run automatically when the web app starts, before it serves requests. Your volumes are kept.

4. Watch the start-up log until the app is listening:

   ```bash
   docker compose logs -f web
   ```

5. Check the server:

   ```bash
   curl -i http://localhost:5001/health
   ```

   `/health` returns `200 OK`. Then sign in and run a search in a container you know.

## Roll back

Migrations only go forward. An older release can't run against a database a newer release has migrated, so rolling back means restoring the backup from step 1.

1. Stop the server and delete its volumes:

   ```bash
   docker compose down -v
   ```

2. Go back to the old version: set `CONNAPSE_VERSION` back in `.env` and run `docker compose pull web`, or `git checkout` the old tag.

3. Restore the backup, following the restore steps in [Back up and restore Connapse](backup-and-restore.md).

Anything written to Connapse between the backup and the rollback is lost. Sources catch up on their next sync.

## Do it with the API

Upgrades have no API equivalent. `GET /api/v1/info` returns the version a server runs, so a script can confirm the upgrade took:

```bash
curl http://localhost:5001/api/v1/info
```

## Related

- [Back up and restore Connapse](backup-and-restore.md)
- [Troubleshoot a self-hosted server](troubleshooting.md)
