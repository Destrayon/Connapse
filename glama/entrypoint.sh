#!/usr/bin/env bash
# Glama MCP discovery entrypoint
# Starts PostgreSQL (with pgvector) + Connapse locally,
# then runs mcp-proxy as a stdio-to-HTTP bridge so Glama can discover tools.
set -e

PG_VERSION=17

# Initialize and start PostgreSQL
su - postgres -c "/usr/lib/postgresql/${PG_VERSION}/bin/initdb -D /var/lib/postgresql/data" 2>/dev/null || true
su - postgres -c "/usr/lib/postgresql/${PG_VERSION}/bin/pg_ctl -D /var/lib/postgresql/data -l /var/lib/postgresql/logfile start" 2>/dev/null

# Wait for PostgreSQL
for i in $(seq 1 30); do
    if su - postgres -c "pg_isready -q" 2>/dev/null; then break; fi
    sleep 0.5
done

# Create database, user, and enable pgvector
su - postgres -c "psql -c \"CREATE USER connapse WITH PASSWORD 'connapse';\"" 2>/dev/null || true
su - postgres -c "psql -c \"CREATE DATABASE connapse OWNER connapse;\"" 2>/dev/null || true
su - postgres -c "psql -d connapse -c \"CREATE EXTENSION IF NOT EXISTS vector;\"" 2>/dev/null || true

# Configure Connapse for local discovery
export ConnectionStrings__DefaultConnection="Host=localhost;Database=connapse;Username=connapse;Password=connapse"
export Mcp__AllowAnonymousDiscovery=true
export Knowledge__Storage__MinIO__Endpoint="localhost:9000"
export Knowledge__Storage__MinIO__AccessKey="minioadmin"
export Knowledge__Storage__MinIO__SecretKey="minioadmin"
export Knowledge__Storage__MinIO__UseSSL=false
export Identity__Jwt__Secret="glama-discovery-only-not-for-production-use-minimum-32-chars"
export ASPNETCORE_URLS="http://localhost:8080"
export ASPNETCORE_ENVIRONMENT="Production"

# Start Connapse in background (redirect all output to log file so it doesn't
# pollute the stdio pipe that mcp-proxy uses for JSON-RPC communication)
dotnet /opt/connapse/Connapse.Web.dll >/var/log/connapse.log 2>&1 &

# Wait for Connapse to be healthy
for i in $(seq 1 60); do
    if curl -sf http://localhost:8080/health >/dev/null 2>&1; then break; fi
    sleep 1
done

# Run mcp-proxy as stdio bridge (main process — Glama connects to this)
exec mcp-proxy http://localhost:8080/mcp
