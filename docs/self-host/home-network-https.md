---
title: Add https to a home server
description: "Which sign-ins need https, and how to give a Connapse server on your home network a real certificate without opening it to the internet."
type: how-to
audience: operator
---

# Add https to a home server

Use this when Connapse runs on a machine on your home network, such as `http://homeserver:5001`, and you want https for it without forwarding any port from the internet.

## Which sign-ins need https

| Sign-in | Over plain http on your network |
|---------|---------------------------------|
| The web UI in a browser | Works. Passwords and tokens cross your network unencrypted. |
| `connapse auth login` from [connapse-cli](https://github.com/Destrayon/connapse-cli) | Works. The server recognizes its own CLI and sends the sign-in back to the CLI on your computer. |
| An app on your computer that signs in through the browser and listens on `localhost`, `127.0.0.1` or `[::1]` for the answer, such as an MCP client | Connapse accepts it. Some clients refuse an http server by themselves, because the [MCP authorization spec](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization) requires https for its sign-in endpoints. Use https for those. |
| Claude on the web, desktop or mobile, added as a custom connector | Never works from a home network. Claude reaches a connector from Anthropic's servers, so the server needs a public https address. |
| An app whose sign-in answer goes to an http address other than your own computer | Refused. Only https or a loopback address (one that points back at the same computer) can receive a sign-in. |

If a client refuses the server, or you want the UI and tokens encrypted on your network, follow the steps below.

## Prerequisites

- A working install from [Install Connapse](install.md), with its compose file in a folder.
- A domain you own, at a DNS provider with an API that your proxy supports, such as Cloudflare. These steps use `connapse.home.example.com` and Cloudflare.
- An API token from the DNS provider that can edit that domain's records.
- The server's address on your network, such as `192.168.1.20`. Give it a fixed address in your router so it doesn't change.

## Steps

1. Point a DNS name at the server's private address. At your DNS provider, add an `A` record for `connapse.home.example.com` with the value `192.168.1.20`. With Cloudflare, leave the proxy off (DNS only). Anyone can look the name up, but a `192.168.x.x` address only leads somewhere from inside your network.

2. Don't forward any port. The certificate comes from a DNS-01 challenge: the proxy proves it controls the domain by writing a DNS record through the provider's API, so Let's Encrypt never has to connect to your server.

3. Build Caddy with your DNS provider's plugin. Caddy is a web server that gets and renews certificates by itself. Next to `docker-compose.yml`, create `caddy/Dockerfile`:

   ```dockerfile
   FROM caddy:2-builder AS builder
   RUN xcaddy build --with github.com/caddy-dns/cloudflare

   FROM caddy:2
   COPY --from=builder /usr/bin/caddy /usr/bin/caddy
   ```

   and `caddy/Caddyfile`:

   ```text
   connapse.home.example.com {
       tls {
           dns cloudflare {env.CLOUDFLARE_API_TOKEN}
       }
       request_body {
           max_size 257MiB
       }
       reverse_proxy web:8080
   }
   ```

   Caddy passes WebSocket upgrades and sets `X-Forwarded-Proto`, which Connapse reads to know it was reached over https. `max_size` is Connapse's 256 MiB upload limit plus 1 MiB for the form around the file. Write `MiB`: Caddy reads `MB` as millions of bytes, and `257MB` is smaller than 256 MiB.

4. Add Caddy and stop publishing the web port. Put `CLOUDFLARE_API_TOKEN=...` in `.env`, then create `docker-compose.https.yml`:

   ```yaml
   services:
     caddy:
       build: ./caddy
       ports:
         - "443:443"
       environment:
         CLOUDFLARE_API_TOKEN: ${CLOUDFLARE_API_TOKEN}
       volumes:
         - ./caddy/Caddyfile:/etc/caddy/Caddyfile:ro
         - caddydata:/data
       networks:
         - frontend
       restart: unless-stopped
     web:
       ports: !reset []

   volumes:
     caddydata:
   ```

   Start with both files:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.https.yml up -d --build
   ```

   `caddydata` keeps the certificate and its key, so a restart doesn't request a new one. If `docker compose logs caddy` shows `API token appears invalid`, the token in `.env` is mistyped or missing: the Cloudflare plugin checks its format before Caddy starts.

5. If the name doesn't resolve at home, add it to the hosts file. Many routers refuse DNS answers that point at a private address, as a guard against DNS rebinding, so `nslookup connapse.home.example.com` returns nothing. On each computer that uses Connapse, add this line to `/etc/hosts` on Linux and macOS, or `C:\Windows\System32\drivers\etc\hosts` on Windows:

   ```text
   192.168.1.20  connapse.home.example.com
   ```

   Phones can't edit a hosts file. For them, add the name as a local DNS entry in your router or in a network DNS server such as Pi-hole.

6. Check it:

   ```bash
   curl -s https://connapse.home.example.com/.well-known/oauth-authorization-server
   ```

   `issuer` reads `https://connapse.home.example.com`. If it reads `http://`, the proxy isn't sending `X-Forwarded-Proto`. Then sign the CLI in again against `https://connapse.home.example.com`, and change the address in each MCP client.

### Use nginx-proxy instead of Caddy

If you already run [nginx-proxy](https://github.com/nginx-proxy/nginx-proxy) with [acme-companion](https://github.com/nginx-proxy/acme-companion), switch acme-companion to the DNS-01 challenge with `ACME_CHALLENGE: DNS-01` and an `ACMESH_DNS_API_CONFIG` for your provider, as its DNS-01 documentation shows. Then, in place of steps 3 and 4:

1. Create `docker-compose.https.yml` next to `docker-compose.yml`. nginx-proxy only reaches containers on a network it shares, so attach `web` to nginx-proxy's network. This example assumes that network is named `proxy`; `docker network ls` shows its real name.

   ```yaml
   services:
     web:
       environment:
         VIRTUAL_HOST: connapse.home.example.com
         VIRTUAL_PORT: "8080"
         LETSENCRYPT_HOST: connapse.home.example.com
       ports: !reset []
       networks:
         - frontend
         - proxy

   networks:
     proxy:
       external: true
   ```

2. Raise the upload limit for this name only. In the folder that nginx-proxy mounts at `/etc/nginx/vhost.d`, create a file named exactly `connapse.home.example.com` that holds one line:

   ```text
   client_max_body_size 257m;
   ```

   Without it, nginx refuses any upload over 1 MiB. nginx's `m` means MiB, so `257m` is above Connapse's 256 MiB limit.

3. Start with both files: `docker compose -f docker-compose.yml -f docker-compose.https.yml up -d`.

Steps 1, 2, 5 and 6 are the same. nginx-proxy passes WebSocket upgrades and `X-Forwarded-Proto` by default.

## Do it with the API

None of these steps has an API equivalent. They configure DNS, the proxy and the hosts that reach Connapse.

## Related

- [Run Connapse in production](production.md), for a server other people reach over the internet
- [Install Connapse](install.md)
- [Troubleshoot a self-hosted server](troubleshooting.md)
