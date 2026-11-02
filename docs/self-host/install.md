---
title: Install Connapse
type: how-to
audience: operator
---

# Install Connapse

Use this when you want to choose how Connapse runs on your own hardware: from a prebuilt image or from source, with or without a local embedding model, a reranker or a GPU. For the shortest path to a working server, follow the [quickstart](../get-started/quickstart.md) instead.

## Prerequisites

- Shell access to the machine, with permission to run Docker.
- Docker 24 or later with the Compose plugin 2.20 or later.
- At least 8 GB of memory and 20 GB of disk with Ollama, or 4 GB without it. The reranker adds 2 GB.
- One free port for the web app. Compose maps it to 5001 on the host.

## What runs

Connapse runs as a set of Compose services. Only the web app publishes a port; the other services sit on an internal network that has no internet access.

| Service | Image | Starts | Memory limit | Purpose |
|---|---|---|---|---|
| `web` | built from the repo, or `ghcr.io/destrayon/connapse` | always | 3 GB | The UI at `/`, the REST API at `/api`, and the MCP server at `/mcp` |
| `postgres` | `pgvector/pgvector:pg17` | always | 512 MB | Documents, chunks, vectors, settings and accounts |
| `minio` | `cgr.dev/chainguard/minio` | always | 256 MB | Uploaded files, in the `connapse-files` bucket |
| `ollama` | `ollama/ollama` | `--profile with-ollama` | 4 GB | The default embedding model |
| `reranker` | `ghcr.io/huggingface/text-embeddings-inference:cpu-1.9` | `--profile with-reranker` | 2 GB | An optional cross-encoder that re-scores search results |

Data lives in the named volumes `pgdata`, `miniodata`, `ollamadata`, `rerankerdata` and `appdata`. They survive `docker compose down`; `docker compose down -v` deletes them.

## Steps

1. Get the files. To build from source, clone the repo:

   ```bash
   git clone https://github.com/Destrayon/Connapse.git
   cd Connapse
   cp .env.example .env
   ```

   To run the prebuilt image instead, copy `docker-compose.yml` and `.env.example` from the repo's [`release/`](../../release/) folder into an empty folder, then run `cp .env.example .env` there. Set `CONNAPSE_VERSION` in `.env` to pin a release such as `v0.3.2`; it defaults to `latest`.

2. Edit `.env`. Set `POSTGRES_PASSWORD`, `MINIO_ROOT_USER` and `MINIO_ROOT_PASSWORD`. Generate each password with `openssl rand -base64 32`.

3. Optional: set `CONNAPSE_ADMIN_EMAIL` and `CONNAPSE_ADMIN_PASSWORD` in `.env` to create the Owner account on first start. Leave them empty to create the Owner through the setup form at `/register` instead.

4. Optional: set `Identity__Jwt__Secret` to a value of at least 32 characters, generated with `openssl rand -base64 64`. If you leave it empty, Connapse generates one on first start and keeps it in the `appdata` volume.

5. Start the services. Pick the line that matches what you want to run:

   ```bash
   # Ollama for embeddings (the default embedding provider)
   docker compose --profile with-ollama up -d

   # Ollama and the CPU reranker
   docker compose --profile with-ollama --profile with-reranker up -d

   # Ollama and the reranker on an NVIDIA GPU
   docker compose -f docker-compose.yml -f docker-compose.reranker-gpu.yml --profile with-ollama --profile with-reranker up -d

   # No local models: you'll point Connapse at a hosted embedding provider in step 7
   docker compose up -d
   ```

   The GPU overlay defaults to the RTX 50-series build. For other GPUs, set `RERANKER_GPU_IMAGE` in `.env` to `ghcr.io/huggingface/text-embeddings-inference:turing-1.9` (RTX 20), `86-1.9` (RTX 30), `89-1.9` (RTX 40), `1.9` (A100) or `hopper-1.9` (H100), using the same image prefix.

6. If you started Ollama, download the embedding model:

   ```bash
   docker compose exec ollama ollama pull nomic-embed-text
   ```

7. If you didn't start Ollama, sign in as the Owner, open **Admin** → **Settings** → **Embedding**, choose OpenAI, Azure OpenAI or another provider, enter its key and model, and save.

8. Optional: if you started the reranker, open **Admin** → **Settings** → **Search** and set **Reranker** to **Cross-Encoder**. The model downloads from Hugging Face on the reranker's first start.

9. Check the server:

   ```bash
   docker compose ps
   curl -i http://localhost:5001/health
   ```

   `postgres` and `minio` show `healthy`, and `/health` returns `200 OK`.

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
