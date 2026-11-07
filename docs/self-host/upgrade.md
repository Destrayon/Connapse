---
title: Upgrade Connapse
description: Upgrade a self-hosted Connapse server, including the steps from v0.3.x to v0.4.0, and roll back if it goes wrong.
type: how-to
audience: operator
---

# Upgrade Connapse

Use this when you want to move a running Connapse server to a newer release, and to get back to the old one if the upgrade goes wrong.

## Prerequisites

- Shell access to the Docker host, in the folder you start Connapse from.
- A backup from the last hour, made with [Back up and restore Connapse](backup-and-restore.md). Rolling back means restoring it.
- The [CHANGELOG](../../CHANGELOG.md) entries between your version and the new one, read for breaking changes and security fixes.
- From v0.3.x, start with [From v0.3.x to v0.4.0](#from-v03x-to-v040), which uses the steps below. That upgrade removes the documents of every S3, Azure Blob and filesystem container, so save their settings first.
- If you use Filesystem sources, the upgrade deletes them, their connections and everything indexed from them, and that can't be undone. Filesystem sources no longer exist, so note their folders first and [add them again over SFTP](../sources/sftp.md) afterwards.

## Steps

1. Back up the server, following [Back up and restore Connapse](backup-and-restore.md).

2. Get the new version.

   If you started from the published file, run the same command again. It always follows the newest stable release, so go to step 4 afterwards:

   ```bash
   docker compose -f oci://ghcr.io/destrayon/connapse:compose up -d -y
   ```

   To move to one release instead, use its tag, such as `oci://ghcr.io/destrayon/connapse:compose-v0.4.0`, and leave `CONNAPSE_VERSION` out of `.env` or set it to the same release.

   If you run the prebuilt image from a downloaded file, set the release in `.env` and pull it:

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

3. Restart with the new version. Add `--build` when you build from source:

   ```bash
   docker compose up -d
   ```

   Database migrations run automatically when the web app starts, before it serves requests. Your volumes are kept.

4. Watch the start-up log until the app is listening:

   ```bash
   docker compose logs -f web
   ```

   If you started from the published file, name the project: `docker compose -p connapse logs -f web`.

5. Check the server:

   ```bash
   curl -i http://localhost:5001/health
   ```

   `/health` returns `200 OK`. Then sign in and run a search in a container you know.

## Moving to the one-command start

Your passwords and data don't change. On the first start of the new compose file, the `setup` service sees your existing data and keeps your passwords: the ones in `.env` if you have one, otherwise the old `connapse_dev` defaults. If you were using those defaults, set your own passwords now, because they are public:

- PostgreSQL keeps the password its data was created with, so change it there first, with `docker compose exec postgres psql -U connapse -c "ALTER USER connapse PASSWORD 'new-password'"`, then put the same value in `.env` as `POSTGRES_PASSWORD`.
- MinIO reads its password on each start, so only put the new values in `.env` as `MINIO_ROOT_USER` and `MINIO_ROOT_PASSWORD`.

Then run `docker compose up -d`. `setup` saves the new values, and MinIO and the app restart to pick them up.

Keep the same Compose project, because the volumes belong to it. A downloaded file or a clone takes its project name from its folder, and the published file always uses `connapse`. `docker compose ls` lists the projects on the host. To move a folder install to the published file, pass the old name: `docker compose -p <old-name> -f oci://ghcr.io/destrayon/connapse:compose up -d -y`, and use `-p <old-name>` wherever these docs say `-p connapse`. To move from the published file to a folder, put `COMPOSE_PROJECT_NAME=connapse` in that folder's `.env`. With a new project name, the server starts empty, and the old data waits in the old volumes.

Three things behave differently:

- The `with-ollama` profile is gone, and Ollama starts by default. If you use a hosted provider, set `CONNAPSE_LOCAL_MODEL=off` in `.env` before you upgrade, or Ollama starts with no use. Connapse only downloads the model when Ollama is the provider.
- To keep the reranker on with a plain `docker compose up -d`, set `COMPOSE_PROFILES=with-reranker` in `.env`. `--profile with-reranker` still works.
- A clone now runs the prebuilt image. Add `--build` to run the code you checked out.

## From v0.3.x to v0.4.0

v0.4.0 replaces a container's connector with sources and removes several old interfaces, so this upgrade needs more than the steps above. The [CHANGELOG](../../CHANGELOG.md) lists every breaking change. Do these in order.

1. Let uploads that are still indexing finish. v0.3.x keeps queued jobs in memory, so they are lost when it stops.

2. Back up, as in step 1 above.

3. While v0.3.x is still running, save each S3, Azure Blob or filesystem container's settings. The upgrade deletes them, and you need them to set those containers up again:

   ```bash
   curl -H "X-Api-Key: cnp_your_key" "http://localhost:5001/api/containers?skip=0&take=200" > containers-v0.3-page1.json
   ```

   A page holds at most 200 containers. While a page ends with `"hasMore":true`, fetch the next one with `skip=200`, then `skip=400`, and so on, and check that every external container appears in the files. Each container's `connectorType` and `connectorConfig` hold its bucket, prefix or root path. Keep the files private, because a config can hold credentials.

4. Take the new `docker-compose.yml` from the release package. The `minio/minio` image the old file names is no longer published, so the old file can't start MinIO. If you keep your own compose file, change MinIO's image to `cgr.dev/chainguard/minio` and add `user: "0"` to it. Reading PDFs by their layout needs about 3 GB for `web`; with less, PDFs are read in plain text order and the log says so.

5. Set `CONNAPSE_VERSION=v0.4.0` in `.env`, then pull and start as in steps 2 to 5 above.

   On first start, the upgrade removes every document and folder from each old S3, Azure Blob or filesystem container, because without the record of where they came from Connapse could no longer check them against the cloud's permissions. This removes only Connapse's copies, never the files in your bucket or folder. The containers themselves stay, empty; delete them or leave them. Search also refuses any S3, Azure Blob or Atlassian document that is missing its cloud address, so no such document is shown to everyone who can read its container.

6. Set each of those up again as a source in a new container, using the settings you saved in step 3. The source indexes the files afresh, with per-user permissions. [Containers, connections and sources](../sources/index.md) explains the new model.

7. Re-issue every personal access token and agent key that has no scopes, and check that tokens used for admin work carry the scope they need. See [Create tokens and agent keys](../guides/tokens-and-agents.md).

8. Move MCP clients that use the SSE transport to Streamable HTTP at `/mcp`, as in [Connect an MCP client](../guides/mcp-clients.md). MCP clients that signed in with OAuth sign in once more.

9. The command-line tool is now separate. Install [connapse-cli](https://github.com/Destrayon/connapse-cli) and run `connapse auth login`.

10. Ask everyone who linked an AWS or Azure account to link it again under **Profile** → **Integrations**.

    An address set in the deployment now beats one saved on the **Settings** page, as [Where settings come from](../reference/configuration.md#where-settings-come-from) explains. If you saved an Ollama or TEI address of your own on a Compose server, move it to `docker-compose.override.yml`, or the server goes back to the Compose address.

11. Re-read the files whose parser changed, which after this upgrade is most PDF, Word, PowerPoint and text files. To see how many a container has first, with a token that has `knowledge:write`:

    ```bash
    curl -X POST -H "X-Api-Key: cnp_your_key" -H "Content-Type: application/json" \
      -d '{"dryRun": true}' http://localhost:5001/api/containers/<container-id>/reindex
    ```

    Then start it for every container, with a token that has the `admin:settings` scope:

    ```bash
    curl -X POST -H "X-Api-Key: cnp_your_key" -H "Content-Type: application/json" \
      -d '{}' http://localhost:5001/api/settings/reindex
    ```

    Only documents whose parser or settings changed are re-read.

## Scripts that read error codes

From v0.4.0, every REST API error is a [problem](../problems.md): its `type` ends in a slug that names the problem. File and folder errors still put the code older releases answered with in `error`, beside the new `type`, so a script that branches on `error` keeps working. Move scripts to `type` when you can.

| Old code in `error` | New `type` slug | Status |
|---|---|---|
| `unsupported_file_type` | `unsupported-file-type` | 400 |
| `filename_too_long` | `invalid-file-request`, with `field` set to `fileName` | 400 |
| `document_not_ready` | `file-not-ready` | 400 |
| `document_failed` | `file-ingestion-failed` | 400 |
| `no_parser` | `no-parser` | 400 |
| `file_not_readable` | `file-not-readable` | 404 |
| `write_denied` | `container-not-writable` | 400 |
| `path_too_deep` | `invalid-folder`, with `field` set to `path` | 400 |

Two old file errors behave differently now:

- A bulk upload no longer fails as a whole when one file has an unsupported type or a name that is too long. It succeeds, and that file's entry carries the reason in its own `error`.
- `cloud_access_denied` is gone. Only containers with Connapse-managed storage take uploads, and an upload to any other container gets `write_denied`.

Unlinking a cloud identity with `DELETE /api/v1/auth/cloud/{provider}` also still puts the old code in `error`:

| Old code in `error` | New `type` slug | Status |
|---|---|---|
| `invalid_provider` | `invalid-link-provider` | 400 |
| `link_changed` | `link-changed` | 409 |

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
