---
title: Troubleshoot a self-hosted server
type: troubleshooting
audience: operator
---

# Troubleshoot a self-hosted server

Each section below starts with the error text as Connapse shows it. Most of them appear in the web app's log, which you read with:

```bash
docker compose logs --tail 200 web
```

Errors from a source's connection test are covered on that source's setup page, under **Troubleshooting**. API errors are listed in the [error reference](../problems.md).

Several fixes below change a setting. The web app only receives the variables listed under `web.environment` in `docker-compose.yml`, so a setting added to `.env` alone does nothing. Add it to a `docker-compose.override.yml` next to `docker-compose.yml` instead:

```yaml
services:
  web:
    environment:
      RateLimiting__ApiPermitLimit: "1000"
```

`docker compose` reads the override file on its own. If you start with `-f` flags, add `-f docker-compose.override.yml` to them. In production, use the `docker-compose.prod.yml` from [Run Connapse in production](production.md).

## Error: Failed to connect to Ollama embedding service at http://ollama:11434/

You might see: a document's status turns to **Failed** after upload, and its details show this message, ending with `Ensure Ollama is running and the model 'nomic-embed-text' is available.`

**Cause:** This happens when the embedding provider is Ollama, the default, but the `ollama` service isn't running or doesn't have the model. The service only starts with `--profile with-ollama`, and Connapse doesn't download models.

**Fix:**
1. Start Ollama with the rest of the stack:

   ```bash
   docker compose --profile with-ollama up -d
   ```

2. Download the model:

   ```bash
   docker compose exec ollama ollama pull nomic-embed-text
   ```

3. In the container, choose the failed file and reindex it, or upload it again.

If you use a hosted embedding provider instead, open **Admin** → **Settings** → **Embedding**, choose it, and save. Then reindex.

## Error: Failed to connect to 127.0.0.1:5432

You might see: `dotnet run --project src/Connapse.Web` stops at start-up with `Npgsql.NpgsqlException` and this message.

**Cause:** This happens when you run the web app outside Docker but started the backing services without the development overlay. The base `docker-compose.yml` publishes no database port.

**Fix:**
1. Restart the backing services with the overlay:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d postgres minio
   ```

2. Run the web app again.

Under Docker Compose, `web` waits for `postgres` to report healthy, so this error there means PostgreSQL itself failed. Check `docker compose logs postgres`.

## Error: read without the layout model

You might see: a PDF indexes, but its warnings say `read without the layout model: the parser has N MB and the model needs about M MB; give Connapse more memory and reindex to read it by layout`.

**Cause:** This happens when the `web` container has too little memory for the PDF layout model. Parsing gets the container's memory limit less 1 GB, which covers the web app and the files waiting to be parsed. From about 2.7 GB, the PDF models run in one shared process beside the parsers. Below about 1.9 GB, each PDF is read in content order instead, which handles headers, footers and multi-column reading order less well.

**Fix:**
1. Give `web` at least 3 GB, the compose default, under `deploy.resources.limits.memory` in the override file.
2. Restart it with `docker compose up -d web`.
3. Check the start-up log line `ParserPool runs up to N parser hosts of at most M MB each`, which shows what each parser now gets.
4. Reindex the affected documents. Documents read without the layout model are read again once the parser has more memory than they had.

## Error: the file is over the 256 MB limit [file_too_large]

You might see: an upload fails with `the file is N MB, over the 256 MB limit [file_too_large]`.

**Cause:** This happens when a file is larger than the upload limit, 256 MiB by default.

**Fix:**
1. Open **Admin** → **Settings** → **Upload** and raise the maximum file size.
2. If a reverse proxy sits in front of Connapse, raise its body size limit to match, such as `client_max_body_size` in nginx.

## Error: Rate limit exceeded. Try again later.

You might see: a `429 Too Many Requests` response from the API, `/mcp` or sign-in.

**Cause:** This happens when one client sends more requests than the rate limit allows in a window. By default, each 60-second window allows 10 sign-in requests, 200 API requests and 600 MCP requests.

**Fix:**
1. Wait for the window to pass, then retry.
2. If the traffic is expected, such as a bulk import script, raise the limit in the override file, for example `RateLimiting__ApiPermitLimit: "1000"`, then run `docker compose up -d web`.

## Error: Origin not allowed. Add it to Cors:AllowedOrigins to call /mcp from that site.

You might see: `403 Forbidden` from `/mcp`, with JSON-RPC error code `-32600`.

**Cause:** This happens when a browser-based MCP client on another site calls `/mcp`. Connapse refuses requests whose `Origin` header names a site other than loopback or one you allowed, so a web page can't drive your agent tools. Clients that send no `Origin`, such as Claude Code and Claude Desktop, aren't affected.

**Fix:**
1. Add the client's origin to the override file, for example `Cors__AllowedOrigins__0: "https://app.example.com"`.
2. Restart with `docker compose up -d web`.

## Error: web exited with code 137

You might see: `docker compose ps` shows `web` restarting or exited with code 137 during a large upload or a big sync.

**Cause:** This happens when the container hits its memory limit and Docker kills it.

**Fix:**
1. Raise the `web` memory limit in the override file, under `deploy.resources.limits.memory`, for example to `4G`.
2. Optional: lower **Parallel workers** under **Admin** → **Settings** → **Upload**, so fewer files are parsed at once.
3. Restart with `docker compose up -d web`.

## Turn on debug logging

To see more detail for any of these errors, raise the log level in the override file and restart `web`:

```yaml
services:
  web:
    environment:
      Logging__LogLevel__Default: "Debug"
      Logging__LogLevel__Connapse: "Debug"
```

Set them back afterwards. Debug logs are large and can include document names.

## Check each service

```bash
docker compose ps
docker compose exec postgres pg_isready -U connapse -d connapse
docker compose exec ollama ollama list
curl -i http://localhost:5001/health
```

`pg_isready` prints `accepting connections`, `ollama list` includes `nomic-embed-text`, and `/health` returns `200 OK`.
