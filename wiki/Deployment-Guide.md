# Deployment Guide

## Services

Connapse runs as a set of Docker containers orchestrated by Docker Compose.

| Service | Image | Purpose | Default Port |
|---------|-------|---------|-------------|
| `web` | Custom (Dockerfile) | Connapse application (Blazor Server + API + MCP) | 5001 (mapped to container 8080) |
| `postgres` | `pgvector/pgvector:pg17` | PostgreSQL 17 with pgvector extension | Internal only |
| `minio` | `minio/minio` | S3-compatible object storage for files | Internal only (console: 9001) |
| `ollama` | `ollama/ollama` | Local LLM/embedding inference (optional, requires `--profile with-ollama`) | Internal only |

### Networks

- **frontend** -- Bridge network; exposes the web service to the host
- **backend** -- Internal bridge network; database, MinIO, and Ollama are not exposed to the host

## Environment Variables

### Required

| Variable | Description | Example |
|----------|-------------|---------|
| `CONNAPSE_ADMIN_EMAIL` | Admin account email (seeded on first run) | `admin@example.com` |
| `CONNAPSE_ADMIN_PASSWORD` | Admin account password | `YourSecurePassword123!` |
| `CONNAPSE_JWT_SECRET` | JWT signing secret (HS256). **Must be set in production.** | Output of `openssl rand -base64 64` |

> **Note:** `CONNAPSE_JWT_SECRET` can also be set via `Identity__Jwt__Secret`.

### Optional

| Variable | Default | Description |
|----------|---------|-------------|
| `ASPNETCORE_ENVIRONMENT` | `Production` | ASP.NET Core environment |
| `POSTGRES_PASSWORD` | `connapse_dev` | PostgreSQL password |
| `MINIO_ROOT_USER` | `connapse_dev` | MinIO root username |
| `MINIO_ROOT_PASSWORD` | `connapse_dev_secret` | MinIO root password |
| `ConnectionStrings__DefaultConnection` | (set in compose) | Full PostgreSQL connection string |
| `Knowledge__Storage__MinIO__Endpoint` | `minio:9000` | MinIO endpoint |
| `Knowledge__Storage__MinIO__AccessKey` | (from MINIO_ROOT_USER) | MinIO access key |
| `Knowledge__Storage__MinIO__SecretKey` | (from MINIO_ROOT_PASSWORD) | MinIO secret key |
| `Knowledge__Storage__MinIO__UseSSL` | `false` | MinIO SSL toggle |
| `Knowledge__Embedding__BaseUrl` | `http://ollama:11434` | Ollama embedding endpoint |

### Cloud Identity (Optional)

| Variable | Description |
|----------|-------------|
| `Identity__AzureAd__ClientId` | Azure AD application client ID |
| `Identity__AzureAd__TenantId` | Azure AD tenant ID |
| `Identity__AzureAd__ClientSecret` | Azure AD client secret |
| `Identity__AwsSso__IssuerUrl` | AWS IAM Identity Center issuer URL |
| `Identity__AwsSso__Region` | AWS region for SSO |

## Volumes

| Volume | Mount Point | Purpose |
|--------|-------------|---------|
| `pgdata` | `/var/lib/postgresql/data` | PostgreSQL database files |
| `miniodata` | `/data` | MinIO object storage |
| `ollamadata` | `/root/.ollama` | Ollama model cache |
| `appdata` | `/app/appdata` | Application data (filesystem connector roots, DataProtection keys) |

## Resource Limits

Default resource limits from docker-compose.yml:

| Service | Memory | CPUs |
|---------|--------|------|
| postgres | 512 MB | 1.0 |
| minio | 256 MB | 0.5 |
| ollama | 4 GB | 2.0 |
| web | 1 GB | 2.0 |

Adjust these in `docker-compose.yml` under `deploy.resources.limits` based on your workload.

## Production Hardening

1. **Set a strong JWT secret** -- Use `openssl rand -base64 64` and set via `CONNAPSE_JWT_SECRET` or `Identity__Jwt__Secret`. Never use the default.

2. **Change default passwords** -- Set `POSTGRES_PASSWORD`, `MINIO_ROOT_USER`, and `MINIO_ROOT_PASSWORD` to strong, unique values.

3. **Use a `.env` file** -- Store secrets in a `.env` file (not committed to version control) rather than exporting them in your shell.

4. **Enable HTTPS** -- Place a reverse proxy (nginx, Caddy, Traefik) in front of the `web` service to terminate TLS. The web service listens on HTTP internally (port 8080).

5. **Restrict network exposure** -- By default, only port 5001 is exposed. Keep the backend network internal. Do not expose PostgreSQL or MinIO ports to the host in production.

6. **Set `ASPNETCORE_ENVIRONMENT=Production`** -- This is the default in docker-compose.yml. Ensures detailed errors are not exposed.

7. **Rate limiting** -- Built-in ASP.NET Core rate limiting middleware is enabled with per-user and per-IP policies.

## Backup and Restore

### Database

```bash
# Backup
docker-compose exec postgres pg_dump -U connapse connapse > backup.sql

# Restore
docker-compose exec -T postgres psql -U connapse connapse < backup.sql
```

### MinIO (Object Storage)

```bash
# Backup the MinIO volume
docker run --rm -v connapse_miniodata:/data -v $(pwd):/backup alpine \
  tar czf /backup/minio-backup.tar.gz -C /data .

# Restore
docker run --rm -v connapse_miniodata:/data -v $(pwd):/backup alpine \
  tar xzf /backup/minio-backup.tar.gz -C /data
```

### Full Backup

Back up both the `pgdata` and `miniodata` Docker volumes together for a consistent snapshot. Stop services first if consistency is critical:

```bash
docker-compose stop
# Back up volumes...
docker-compose start
```
