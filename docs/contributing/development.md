---
title: Set up a development environment
type: how-to
audience: contributor
---

# Set up a development environment

Use this when you want to build Connapse from source, run its tests and debug it on your own machine.

## Prerequisites

- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- Docker with the Compose plugin, for the backing services and the integration tests.
- Git, and an editor with C# support: Visual Studio 2022, JetBrains Rider or VS Code with C# Dev Kit.
- Optional: [Ollama](https://ollama.com/download) on the host, for the ingestion integration tests and for embeddings when you run outside Docker.

## Steps

1. Fork the repository on GitHub, then clone your fork and add the upstream remote:

   ```bash
   git clone https://github.com/YOUR-USERNAME/Connapse.git
   cd Connapse
   git remote add upstream https://github.com/Destrayon/Connapse.git
   ```

2. Start PostgreSQL and MinIO with the development overlay, which publishes their ports on localhost:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d postgres minio
   ```

   The base `docker-compose.yml` keeps every service on an internal network. The overlay adds PostgreSQL on 5432, MinIO on 9000 and 9001, Ollama on 11434 and the reranker on 8080. Its Ollama service reserves an NVIDIA GPU, so start Ollama natively instead if you don't have one.

3. Download the embedding model into Ollama:

   ```bash
   ollama pull nomic-embed-text
   ```

4. Build the solution:

   ```bash
   dotnet build
   ```

5. Run the web app:

   ```bash
   dotnet run --project src/Connapse.Web
   ```

   `appsettings.json` already points at the overlay's localhost ports and development passwords. The app listens on `http://localhost:5247`, or on `https://localhost:7247` with `--launch-profile https`. To rebuild on every change, use `dotnet watch --project src/Connapse.Web` instead.

6. Open `http://localhost:5247` and create the Owner account in the setup form.

## Run the tests

```bash
# Unit tests only, no Docker needed
dotnet test --filter "Category=Unit"

# Integration tests, which start PostgreSQL and MinIO with Testcontainers
dotnet test --filter "Category=Integration"

# Everything
dotnet test
```

Integration tests share one PostgreSQL and MinIO pair per test run. About 50 ingestion tests also need Ollama on `localhost:11434` with `nomic-embed-text`, and time out without it.

## Check your change the way CI does

CI builds in Release with code-style enforcement and warnings as errors, then checks formatting. Run the same commands before you push:

```bash
dotnet build --configuration Release -p:EnforceCodeStyleInBuild=true -p:TreatWarningsAsErrors=true -p:SquiggleCop_AutoBaseline=false
dotnet format --verify-no-changes
```

Run `dotnet format` without `--verify-no-changes` to fix what it reports.

## Add a database migration

Connapse has two migration histories, one per database context. Migrations run automatically when the app starts.

```bash
# Documents, containers, sources and settings
dotnet ef migrations add MigrationName --project src/Connapse.Storage --startup-project src/Connapse.Web

# Users, tokens, agents and the audit log
dotnet ef migrations add MigrationName --project src/Connapse.Identity --startup-project src/Connapse.Web
```

Install the tool first if you don't have it, with `dotnet tool install -g dotnet-ef`.

## Run without Docker

To run every backing service natively instead:

1. Install PostgreSQL 17 and the pgvector extension, then create the database:

   ```sql
   CREATE DATABASE connapse;
   CREATE USER connapse WITH PASSWORD 'connapse_dev';
   GRANT ALL PRIVILEGES ON DATABASE connapse TO connapse;
   \c connapse
   CREATE EXTENSION IF NOT EXISTS vector;
   ALTER SCHEMA public OWNER TO connapse;
   ```

2. Install MinIO and start it with the development credentials:

   ```bash
   export MINIO_ROOT_USER=connapse_dev
   export MINIO_ROOT_PASSWORD=connapse_dev_secret
   minio server ~/minio/data --console-address ":9001"
   ```

   Connapse creates the `connapse-files` bucket on first start.

3. Install Ollama, pull `nomic-embed-text`, then run `dotnet run --project src/Connapse.Web`.

## Keep your fork up to date

```bash
git fetch upstream
git checkout main
git merge upstream/main
git push origin main
```

## Related

- [Contributing guide](../../CONTRIBUTING.md)
- [Architecture](architecture.md)
- [Repository setup](repo-setup.md)
- The [connapse CLI](https://github.com/Destrayon/connapse-cli), which has its own repository and setup
