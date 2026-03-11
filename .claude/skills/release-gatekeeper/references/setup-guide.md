# Setup Guide — Isolated Test Instance

This guide covers deploying a Connapse test instance that is completely isolated from the user's production instance.

## Table of Contents
1. [Pre-flight Checks](#pre-flight-checks)
2. [Download Release](#download-release)
3. [Configure Isolation](#configure-isolation)
4. [Deploy](#deploy)
5. [Verify Health](#verify-health)
6. [Install CLI](#install-cli)
7. [Teardown](#teardown)

## Pre-flight Checks

Before anything, understand the current environment:

```bash
# What Docker containers are running?
docker ps --format "table {{.Names}}\t{{.Ports}}\t{{.Status}}"

# What Compose projects exist?
docker compose ls

# What ports are in use?
# The production instance typically uses: 5001 (web), 5432 (postgres), 9000-9001 (minio), 11434 (ollama)
```

**Port allocation for the test instance:**

| Service | Production Port | Test Port |
|---------|----------------|-----------|
| Web UI | 5001 | 6001 |
| PostgreSQL | 5432 | 6432 |
| MinIO API | 9000 | 9100 |
| MinIO Console | 9001 | 9101 |
| Ollama | 11434 | — (skip unless needed) |

## Download Release

```bash
# Identify the latest alpha
TAG=$(gh release list --repo Destrayon/Connapse --limit 1 --json tagName --jq '.[0].tagName')
echo "Testing release: $TAG"

# Download the Docker zip
gh release download "$TAG" --repo Destrayon/Connapse \
  --pattern "connapse-docker-*.zip" \
  --dir "$WORKSPACE"

# Extract
cd "$WORKSPACE"
unzip connapse-docker-*.zip -d deploy/
cd deploy/
```

If the zip contains a nested directory, navigate into it.

## Configure Isolation

### First-Time Setup Test (No Seeded Admin)

First, deploy WITHOUT admin credentials to test what a brand-new user experiences. This catches bugs in the registration/setup flow.

```bash
cat > .env << 'ENVEOF'
# First-time setup test — NO admin seeded
POSTGRES_PASSWORD=test_postgres_secret
MINIO_ROOT_USER=test_minio_admin
MINIO_ROOT_PASSWORD=test_minio_secret_key
Identity__Jwt__Secret=test-jwt-secret-that-is-at-least-64-characters-long-for-hmac-sha256-signing-key
ASPNETCORE_ENVIRONMENT=Production
ENVEOF
```

Deploy, navigate to the UI, and document what happens:
- Is there a registration page?
- Can you create the first account?
- What roles does the first account get?
- What happens if you try the API with no users in the system?

Capture evidence (screenshots, API responses), then tear down this instance (`docker compose -p connapse-e2e-test down -v`) before proceeding.

### Main Test Instance (Seeded Admin)

Now deploy with admin credentials for the full test suite:

```bash
cat > .env << 'ENVEOF'
# Test instance credentials — not real, just for testing
CONNAPSE_ADMIN_EMAIL=admin@test.local
CONNAPSE_ADMIN_PASSWORD=TestPassword123!
POSTGRES_PASSWORD=test_postgres_secret
MINIO_ROOT_USER=test_minio_admin
MINIO_ROOT_PASSWORD=test_minio_secret_key
Identity__Jwt__Secret=test-jwt-secret-that-is-at-least-64-characters-long-for-hmac-sha256-signing-key
ASPNETCORE_ENVIRONMENT=Production
ENVEOF
```

**Remap ports by editing docker-compose.yml directly:**

Docker Compose override files **add** ports, they don't replace them. If the base file has `"5001:8080"` and the override has `"6001:8080"`, Docker maps BOTH ports, causing a conflict. Edit the base file instead:

```bash
cd "$WORKSPACE/deploy"

# Replace web port
sed -i 's/"5001:8080"/"6001:8080"/' docker-compose.yml

# Replace postgres port (if exposed)
sed -i 's/"5432:5432"/"6432:5432"/' docker-compose.yml

# Replace minio ports (if exposed)
sed -i 's/"9000:9000"/"9100:9000"/' docker-compose.yml
sed -i 's/"9001:9001"/"9101:9001"/' docker-compose.yml
```

If you also need to add environment variables (e.g., pointing embedding to production Ollama), use a `docker-compose.override.yml` for **environment-only** overrides (no port remapping):

```yaml
# docker-compose.override.yml — environment overrides only
services:
  web:
    environment:
      Knowledge__Embedding__BaseUrl: "http://host.docker.internal:11434"
```

## Deploy

```bash
cd "$WORKSPACE/deploy"

# Start with isolated project name — this is the key to isolation
docker compose -p connapse-e2e-test up -d

# Watch logs for startup
docker compose -p connapse-e2e-test logs -f --tail 50 2>&1 | tee "$WORKSPACE/logs/startup.log" &
LOGPID=$!

# Give it time to initialize (DB migrations, MinIO bucket creation, admin seeding)
sleep 15

# Kill the log tail
kill $LOGPID 2>/dev/null
```

## Embedding Provider (Ollama)

The test instance needs an embedding provider for semantic search to work. The base docker-compose.yml includes an Ollama service behind a profile.

**Option A: Share the production Ollama** (recommended — saves download time)
If the production instance is already running Ollama with the model pulled, you can point the test web container at the production Ollama instead of starting a new one. Add this to `docker-compose.override.yml`:
```yaml
  web:
    environment:
      Knowledge__Embedding__BaseUrl: "http://host.docker.internal:11434"
```
This works because Ollama is stateless for inference — sharing it is safe.

**Option B: Start a fresh Ollama**
If no production Ollama is running, start one in the test stack. You'll need to modify the compose command:
```bash
docker compose -p connapse-e2e-test --profile with-ollama up -d
```
Then add Ollama port mapping to the override:
```yaml
  ollama:
    ports:
      - "11535:11434"
```
After startup, pull the embedding model:
```bash
docker compose -p connapse-e2e-test exec ollama ollama pull nomic-embed-text
```
This may take a few minutes depending on download speed.

**Option C: Skip semantic search testing**
If neither option works, the instance will still function for keyword search and all other features. Mark semantic search tests as "SKIPPED — no embedding provider" and test keyword/hybrid (which falls back gracefully).

## Verify Health

```bash
# Check all containers are running
docker compose -p connapse-e2e-test ps

# Check health status
docker inspect --format='{{.Name}}: {{.State.Health.Status}}' \
  $(docker compose -p connapse-e2e-test ps -q) 2>/dev/null

# Test web UI is responding
curl -s -o /dev/null -w "%{http_code}" http://localhost:6001
# Should return 200 or 302

# Test API endpoint
curl -s http://localhost:6001/api/containers | head -100
# Should return JSON (possibly 401 if auth required before any data)

# Save health evidence
docker compose -p connapse-e2e-test ps > "$WORKSPACE/evidence/container-status.txt"
docker compose -p connapse-e2e-test logs --tail 100 > "$WORKSPACE/evidence/startup-logs.txt" 2>&1
```

**If health checks fail:**
1. Check logs: `docker compose -p connapse-e2e-test logs web`
2. Check if ports are actually in use: `netstat -an | grep 6001`
3. Check if the DB migration ran: look for "Applied migration" in postgres logs
4. If MinIO is unhealthy, check its logs — sometimes it needs a moment

## Install CLI (Isolated)

Download the native binary — this does NOT affect the user's existing `dotnet tool` installation:

```bash
# Determine platform
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) PLATFORM="win-x64.exe" ;;
  Linux) PLATFORM="linux-x64" ;;
  Darwin)
    case "$(uname -m)" in
      arm64) PLATFORM="osx-arm64" ;;
      *) PLATFORM="osx-x64" ;;
    esac ;;
esac

# Download
gh release download "$TAG" --repo Destrayon/Connapse \
  --pattern "connapse-$PLATFORM" \
  --dir "$WORKSPACE"

# Make executable (Linux/macOS)
chmod +x "$WORKSPACE/connapse-$PLATFORM" 2>/dev/null

# Create a convenient alias
CLI="$WORKSPACE/connapse-$PLATFORM"

# Verify
"$CLI" --help
```

On Windows in Git Bash, the binary is `connapse-win-x64.exe` and can be run directly.

**Authenticate the CLI against the test instance — Credential Pre-Seeding:**

The CLI's `auth login` command uses `Console.ReadKey()` which throws `InvalidOperationException` when stdin is redirected. This means interactive login doesn't work from automated tools. The solution is to **pre-seed credentials directly**.

### Step 1: Get a PAT via the API

**Important:** Connapse uses cookie-based Blazor auth, not REST JWT login. There is no `POST /api/v1/auth/token` endpoint. To get a PAT programmatically, use Python (since `jq` may not be available on Windows):

```python
#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# auth_setup.py — Get a PAT for API testing
import urllib.request, json, sys

BASE_URL = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:6001"
ADMIN_EMAIL = "admin@test.local"
ADMIN_PASSWORD = "TestPassword123!"

# Step 1: Login via cookie auth to get a session
login_data = json.dumps({"email": ADMIN_EMAIL, "password": ADMIN_PASSWORD}).encode()
req = urllib.request.Request(f"{BASE_URL}/api/v1/auth/token",
    data=login_data, headers={"Content-Type": "application/json"})
try:
    resp = urllib.request.urlopen(req)
    token_data = json.loads(resp.read())
    token = token_data.get("accessToken", "")
except Exception:
    # If /api/v1/auth/token doesn't exist, the app uses cookie-only auth.
    # In that case, create a PAT via Playwright browser_evaluate instead.
    print("REST auth endpoint not available. Use Playwright to create PAT.")
    print("Alternative: Use the seeded admin PAT from the admin UI.")
    sys.exit(1)

# Step 2: Create PAT
pat_data = json.dumps({"name": "e2e-test-pat"}).encode()
req = urllib.request.Request(f"{BASE_URL}/api/v1/auth/pats",
    data=pat_data, headers={
        "Content-Type": "application/json",
        "Authorization": f"Bearer {token}"
    })
resp = urllib.request.urlopen(req)
pat_info = json.loads(resp.read())
print(f"PAT: {pat_info['token']}")
print(f"PAT_ID: {pat_info['id']}")
```

**If REST auth fails (404):** Use Playwright to log in via the UI, navigate to Profile > PATs, create a PAT through the form, and capture the token value.

**Alternative for curl users on Windows (no jq):** Replace `jq` with Python:
```bash
# Parse JSON from curl using Python instead of jq
TOKEN=$(curl -s ... | python3 -c "import sys,json; print(json.load(sys.stdin)['accessToken'])")
```

### Step 2: Isolate CLI credentials

Override `USERPROFILE` (Windows) to redirect `~/.connapse/` away from the user's production credentials:

```bash
export USERPROFILE="$WORKSPACE/cli-home"
mkdir -p "$USERPROFILE/.connapse"
```

### Step 3: Write credentials file

```bash
cat > "$USERPROFILE/.connapse/credentials.json" << EOF
{
  "ApiKey": "$PAT",
  "ApiBaseUrl": "http://localhost:6001",
  "UserEmail": "admin@test.local",
  "PatId": "$PAT_ID"
}
EOF
```

### Step 4: Test CLI commands (now works non-interactively)

```bash
# These should all work without interactive prompts:
"$CLI" --help
"$CLI" --version
"$CLI" container list
"$CLI" container create test-cli-container --description "CLI e2e test"
"$CLI" search "test query" --container test-cli-container
"$CLI" auth whoami
"$CLI" auth pat list
```

### Why this works

The CLI stores credentials at `~/.connapse/credentials.json`. By overriding `USERPROFILE`, we redirect this to an isolated directory within the workspace. The user's production credentials are never touched.

### Known bug: USERPROFILE override fails on Windows native binary

As of v0.3.2-alpha, the native Windows binary reads `USERPROFILE` from the Windows registry via `Environment.GetFolderPath(SpecialFolder.UserProfile)`, NOT from the process environment variable. This means `export USERPROFILE="$WORKSPACE/cli-home"` has no effect.

**Workaround:** Test CLI commands against the production instance for non-destructive operations:
- `connapse --version` — verify version matches release
- `connapse --help` — verify help output
- `connapse auth whoami` — verify identity (uses production credentials)
- `connapse container list` — verify list works (against production data)

For destructive commands (upload, delete, reindex), test via the API instead. Document the isolation bug as a known issue.

### After testing

Reset `USERPROFILE` to its original value or simply close the shell. The isolated credentials are cleaned up with the workspace directory.

## Teardown

Run this ONLY after the user confirms they're done reviewing:

```bash
# Stop and remove all test containers, networks, and volumes
docker compose -p connapse-e2e-test down -v

# Verify production is still running
docker compose -p connapse ps

# Remove test CLI binary
rm -f "$CLI"

# Keep workspace for evidence
echo "Test artifacts preserved at: $WORKSPACE"
```

The workspace directory contains all evidence and should be kept until the user no longer needs it.
