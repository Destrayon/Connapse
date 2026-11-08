---
title: Install Connapse
description: "Install Connapse with Docker Compose: requirements, the prebuilt image or a source build, and the switches for the local model, reranker and GPU."
type: how-to
audience: operator
---

# Install Connapse

Use this when you want to choose how Connapse runs on your own hardware: from a prebuilt image or from source, with or without a local embedding model, a reranker or a GPU. For the shortest path to a working server, follow the [quickstart](../get-started/quickstart.md) instead.

## Prerequisites

- Shell access to the machine, with permission to run Docker.
- Docker 24 or later with the Compose plugin 2.34 or later. Compose 2.24 to 2.33 works from a downloaded compose file; see [Steps](#steps).
- At least 8 GB of memory and 20 GB of disk with Ollama, or 4 GB without it. The reranker adds 2 GB.
- One free port for the web app. Compose maps it to 5001 on the host.

## What runs

Connapse runs as a set of Compose services. Only the web app publishes a port; the other services sit on an internal network that has no internet access.

| Service | Image | Starts | Memory limit | Purpose |
|---|---|---|---|---|
| `setup` | `pgvector/pgvector:pg17` | once, on every start, then exits | 64 MB | Makes the random passwords on first start and keeps them in the `secrets` volume |
| `web` | `ghcr.io/destrayon/connapse`, or built from the repo | always | 3 GB | The UI at `/`, the REST API at `/api`, and the MCP server at `/mcp` |
| `postgres` | `pgvector/pgvector:pg17` | always | 512 MB | Documents, chunks, vectors, settings and accounts |
| `minio` | `cgr.dev/chainguard/minio`, pinned by digest to MinIO `RELEASE.2026-10-23T20-06-51Z` | always | 256 MB | Uploaded files, in the `connapse-files` bucket |
| `ollama` | `ollama/ollama:0.40.15` | unless `CONNAPSE_LOCAL_MODEL=off` | 4 GB | The default embedding model |
| `reranker` | `ghcr.io/huggingface/text-embeddings-inference:cpu-1.9` | with `COMPOSE_PROFILES=with-reranker` | 2 GB | An optional cross-encoder that re-scores search results |

Every service except `setup` has `restart: unless-stopped`, so the stack starts again on its own after a reboot or a crash, as long as Docker starts at boot. A service you stop with `docker compose stop` stays stopped until you start it.

Data lives in the named volumes `pgdata`, `miniodata`, `ollamadata`, `rerankerdata`, `appdata` and `secrets`. They survive `docker compose down`; `docker compose down -v` deletes them.

## Steps

1. Choose where the compose file comes from. Every path below uses the prebuilt image unless it says otherwise.

   ```bash
   # Published with each release (Compose 2.34 or later). Run it from any folder:
   docker compose -f oci://ghcr.io/destrayon/connapse:compose up -d -y

   # Downloaded (Compose 2.24 or later). Run these in an empty folder:
   curl -LO https://github.com/Destrayon/Connapse/releases/latest/download/docker-compose.yml
   docker compose up -d

   # From source. A clone uses the prebuilt image too, until you add --build:
   git clone https://github.com/Destrayon/Connapse.git
   cd Connapse
   docker compose up -d --build
   ```

   Nothing else is needed for a working server. The first start makes every password, and the model downloads on its own.

2. Optional: to change something, create a file named `.env` in the folder you run the command from, then run the same command again. [`.env.example`](../../release/.env.example) lists every value. These are the ones people change:

   | In `.env` | What it does |
   |---|---|
   | `CONNAPSE_LOCAL_MODEL=off` | Never downloads or starts Ollama. Set a hosted provider, as in step 3. |
   | `COMPOSE_PROFILES=with-reranker` | Starts the reranker and turns reranking on. The model downloads from Hugging Face on the first start. |
   | `CONNAPSE_VERSION=v0.4.0` | Pins a release. With the published file, pin the file as well, by its release tag: `-f oci://ghcr.io/destrayon/connapse:compose-v0.4.0`. The plain `compose` tag always follows the newest release. |
   | `CONNAPSE_ADMIN_EMAIL` and `CONNAPSE_ADMIN_PASSWORD` | Create the Owner account on first start. Leave them out to use the setup form at `/register`. |
   | `Identity__Jwt__Secret` | Sets the token signing key, at least 32 characters. Left out, Connapse keeps a generated one in the `appdata` volume. |
   | `POSTGRES_PASSWORD`, `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD` | Choose your own passwords. Set these before the first start: PostgreSQL keeps the password its data was created with. |

3. If you switched the local model off, set a hosted provider. Put it in `.env`:

   ```bash
   CONNAPSE_LOCAL_MODEL=off
   Knowledge__Embedding__Provider=OpenAI
   Knowledge__Embedding__Model=text-embedding-3-small
   Knowledge__Embedding__Dimensions=1536
   Knowledge__Embedding__OpenAiApiKey=sk-...
   ```

   Or sign in as the Owner, open **Admin** → **Settings** → **Embedding**, choose the provider, enter its key and model, and save.

4. Optional: to run the reranker on an NVIDIA GPU, clone the repo and add the GPU overlay:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.reranker-gpu.yml up -d
   ```

   The overlay only changes the reranker, so set `COMPOSE_PROFILES=with-reranker` in `.env` as well, or the reranker doesn't start.

   The GPU overlay defaults to the RTX 50-series build. For other GPUs, set `RERANKER_GPU_IMAGE` in `.env` to `ghcr.io/huggingface/text-embeddings-inference:turing-1.9` (RTX 20), `86-1.9` (RTX 30), `89-1.9` (RTX 40), `1.9` (A100) or `hopper-1.9` (H100), using the same image prefix.

5. Check the server:

   ```bash
   docker compose ps
   curl -i http://localhost:5001/health/ready
   ```

   `postgres` and `minio` show `healthy`, and `/health/ready` returns `200 OK`. It returns `503` with the download's progress until the embedding model is ready, and that's normal on a first start.

   If you started from the published file, there's no compose file in your folder, so name the project instead: `docker compose -p connapse ps`. That works for `ps`, `logs`, `exec`, `stop` and `down` throughout these docs. Commands that need the services' definitions, such as `up`, keep `-f oci://ghcr.io/destrayon/connapse:compose`.

   The volumes belong to the project. The published file always names its project `connapse`, while a downloaded file or a clone takes the name of its folder. To move an existing server from one to the other, keep its project name, as [Upgrade Connapse](upgrade.md#moving-to-the-one-command-start) shows, or it starts empty.

If you saved search settings before turning the reranker on, the saved choice stands: open **Admin** → **Settings** → **Search** and set **Reranker** to **Cross-Encoder**.

## Choose the reranker build

A reranker scores each of the top search candidates against the query and reorders them. It was the largest search-quality gain in Connapse's retrieval evaluation. For 30 candidates it takes about 45 ms per search on a GPU, but about 9 seconds and 10 GB of memory on a CPU, so use the CPU build to try it out and a GPU for daily use. If the reranker stops, searches skip it for a minute at a time and return results in their original order.

## Do it with the API

Embedding and search settings can be saved over the REST API by an Admin with a token that holds `admin:settings`. A save replaces the whole category, and any field you leave out goes back to its default, so read the category, change it, and send all of it back. Send the read's `ETag` as `If-Match` so the save fails if someone else changed the settings in between. These commands use `jq` to edit the JSON:

```bash
curl -s -D headers.txt -H "X-Api-Key: $TOKEN" http://localhost:5001/api/settings/search > search.json
ETAG=$(grep -i '^etag:' headers.txt | cut -d' ' -f2 | tr -d '\r')
jq '.reranker = "CrossEncoder"' search.json > search-new.json
curl -X PUT http://localhost:5001/api/settings/search \
  -H "X-Api-Key: $TOKEN" -H "If-Match: $ETAG" \
  -H "Content-Type: application/json" --data @search-new.json
```

A save against an outdated read returns `412` with the [`settings-changed`](../problems.md#settings-changed) problem. See [Automate Connapse with the API](../guides/automate-with-the-api.md) for the full pattern.

## Related

- [Run Connapse in production](production.md)
- [Upgrade Connapse](upgrade.md)
- [Configuration reference](../reference/configuration.md)
- [Troubleshoot a self-hosted server](troubleshooting.md)
