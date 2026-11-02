---
title: Run Connapse in production
type: how-to
audience: operator
---

# Run Connapse in production

Use this when you want to put a Connapse server on a network other people can reach: behind TLS, with your own secrets, and with nothing exposed but the web app.

## Prerequisites

- A working install from [Install Connapse](install.md), and shell access to its host.
- Docker Compose 2.24 or later, for the `!override` tag in step 5.
- A DNS name for the server and a TLS certificate for it, or a reverse proxy that gets one for you, such as Caddy or Traefik with Let's Encrypt.
- Somewhere to keep secrets outside git: a secrets manager, or at least a `.env` file readable only by the account that runs Docker.

## Steps

1. Replace every default credential. In `.env`, set `POSTGRES_PASSWORD`, `MINIO_ROOT_USER` and `MINIO_ROOT_PASSWORD` to random values of 20 characters or more, and never reuse one across services:

   ```bash
   openssl rand -base64 32
   ```

2. Set the JWT signing secret. In `.env`, set `Identity__Jwt__Secret` to a random value of at least 32 characters. Without it, Connapse generates one into the `appdata` volume, which works, but a secret you manage survives a lost volume and can be rotated on purpose:

   ```bash
   openssl rand -base64 64
   ```

3. Protect the Owner password. If you create the Owner from `CONNAPSE_ADMIN_EMAIL` and `CONNAPSE_ADMIN_PASSWORD`, use a strong random password, because Connapse has no page to change it later. Connapse only reads the two values when no account has that email yet, so delete both lines from `.env` after the first start.

4. Put a reverse proxy in front of the web app for TLS. Connapse reads `X-Forwarded-For` and `X-Forwarded-Proto`, so it knows the original client and scheme. The UI is Blazor Server, which keeps a WebSocket open to `/_blazor`, so the proxy must pass WebSocket upgrades. A minimal nginx site:

   ```nginx
   server {
       listen 443 ssl http2;
       server_name connapse.example.com;

       ssl_certificate     /etc/letsencrypt/live/connapse.example.com/fullchain.pem;
       ssl_certificate_key /etc/letsencrypt/live/connapse.example.com/privkey.pem;

       client_max_body_size 256m;

       location / {
           proxy_pass http://127.0.0.1:5001;
           proxy_http_version 1.1;
           proxy_set_header Upgrade $http_upgrade;
           proxy_set_header Connection "upgrade";
           proxy_set_header Host $host;
           proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
           proxy_set_header X-Forwarded-Proto $scheme;
           proxy_read_timeout 300s;
       }
   }
   ```

   `client_max_body_size` matches Connapse's default upload limit of 256 MiB. Raise both together if you raise one.

5. Bind the web port to the proxy only. Create `docker-compose.prod.yml` next to `docker-compose.yml`:

   ```yaml
   services:
     web:
       ports: !override
         - "127.0.0.1:5001:8080"
       restart: unless-stopped
     postgres:
       restart: unless-stopped
     minio:
       restart: unless-stopped
   ```

   Start with both files, adding any profiles you use:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.prod.yml --profile with-ollama up -d
   ```

   PostgreSQL, MinIO, Ollama and the reranker already sit on an internal network with no published ports. Don't load `docker-compose.dev.yml` on a server, because it publishes them all.

   The web app only receives the variables listed under `web.environment` in `docker-compose.yml`, so put any other setting from the [configuration reference](../reference/configuration.md) in this file too, under `web.environment`. Steps 6, 8 and 9 add to it.

6. Allow cross-origin callers only if you need them. By default, Connapse accepts browser requests from its own origin only, and `/mcp` accepts browser-origin requests only from loopback. To let a web app on another site call the API or `/mcp`, list its origin in `docker-compose.prod.yml`:

   ```yaml
   services:
     web:
       environment:
         Cors__AllowedOrigins__0: "https://app.example.com"
   ```

7. Expose only ports 80 and 443 on the host firewall.

8. Optional: tune rate limits. Each window is 60 seconds by default, allowing 10 sign-in requests, 200 API requests and 600 MCP requests per client. Set `RateLimiting__ApiPermitLimit`, `RateLimiting__McpPermitLimit` and the matching `...WindowSeconds` keys under `web.environment` in `docker-compose.prod.yml` to change them.

9. Optional: connect to an external PostgreSQL over TLS. Point the connection string at it in `docker-compose.prod.yml`, with your own password:

   ```yaml
   services:
     web:
       environment:
         ConnectionStrings__DefaultConnection: "Host=db.example.com;Database=connapse;Username=connapse;Password=your_password;SSL Mode=Require"
   ```

   The database needs the `vector` extension, and the account needs permission to create tables, because migrations run on every start.

10. Set up backups before you load real data. See [Back up and restore Connapse](backup-and-restore.md).

## Security checklist

Work through this list before you open the server to other people.

- [ ] Every default credential in `.env` is replaced with a random value of 20 characters or more.
- [ ] `Identity__Jwt__Secret` is set to at least 32 random characters.
- [ ] `.env` and any secret files are outside version control.
- [ ] A reverse proxy terminates TLS, and only ports 80 and 443 are open.
- [ ] `Cors:AllowedOrigins` lists only origins you trust, or is unset.
- [ ] The database and storage volumes are on encrypted disks.
- [ ] Cloud sources use IAM roles, managed identities or Roles Anywhere. Never put cloud access keys in Connapse.
- [ ] Personal access tokens and agent keys are rotated periodically, and any that may have leaked are revoked.
- [ ] The audit log is reviewed for unexpected sign-ins.
- [ ] You read the [CHANGELOG](../../CHANGELOG.md) for security fixes before each upgrade.

## Do it with the API

None of these steps has an API equivalent. They configure the host, the proxy and the environment Connapse starts with.

## Related

- [Upgrade Connapse](upgrade.md)
- [Configuration reference](../reference/configuration.md)
- [Security policy](../../SECURITY.md)
