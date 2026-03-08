# Getting Started

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) and [Docker Compose](https://docs.docker.com/compose/install/)
- (Optional) [.NET 10 SDK](https://dotnet.microsoft.com/download) for development or CLI installation via `dotnet tool`
- (Optional) [Ollama](https://ollama.ai/) for local embeddings and LLM

## Quick Start with Docker Compose

```bash
# Clone the repository
git clone https://github.com/Destrayon/Connapse.git
cd Connapse

# Set required environment variables (or create a .env file)
export CONNAPSE_ADMIN_EMAIL=admin@example.com
export CONNAPSE_ADMIN_PASSWORD=YourSecurePassword123!
export Identity__Jwt__Secret=$(openssl rand -base64 64)

# Start all services (PostgreSQL, MinIO, Web App)
docker-compose up -d
```

The application will be available at **http://localhost:5001**.

### What happens on first run

1. Docker images are pulled (~2-5 minutes)
2. PostgreSQL initializes with the pgvector extension and runs EF Core migrations
3. MinIO buckets are created
4. The admin account is seeded from environment variables
5. The web application starts

### Including Ollama

To start with the optional Ollama service for local embeddings:

```bash
docker-compose --profile with-ollama up -d
```

## First Walkthrough

### 1. Log in to the Web UI

Open http://localhost:5001 and log in with the admin email and password you set above.

### 2. Configure Embeddings

Navigate to **Settings** and configure your embedding provider. If using Ollama, set the base URL to `http://ollama:11434` (already configured in Docker Compose) and pull an embedding model (e.g., `nomic-embed-text`).

### 3. Create a Container

From the **Home** page, click **Create Container**. Give it a name (lowercase, alphanumeric, hyphens allowed) and an optional description.

### 4. Upload Files

Click into your container and upload files. Supported formats include PDF, DOCX, PPTX, Markdown, plain text, CSV, JSON, XML, and YAML. Files are parsed, chunked, embedded, and indexed in the background.

### 5. Search Your Knowledge

Use the **Search** page to query your container. Choose a search mode:

- **Semantic** -- Vector similarity search using embeddings
- **Keyword** -- Full-text search using PostgreSQL tsvector
- **Hybrid** -- Both combined with Reciprocal Rank Fusion (recommended)

### Using the CLI

Install the CLI and authenticate:

```bash
# Install via .NET global tool (requires .NET 10)
dotnet tool install -g Connapse.CLI

# Or download a native binary from GitHub Releases
# https://github.com/Destrayon/Connapse/releases

# Authenticate (opens browser)
connapse auth login --url http://localhost:5001

# Create a container
connapse container create my-project --description "My knowledge base"

# Upload files
connapse upload ./documents --container my-project

# Search
connapse search "your query" --container my-project
```

See [[CLI Reference]] for the full command reference.

### Using with Claude Desktop (MCP)

See [[MCP Integration]] for setup instructions.

## Development Setup

```bash
# Start infrastructure only
docker-compose up -d postgres minio

# Run the web app locally
dotnet run --project src/Connapse.Web

# Run all tests
dotnet test

# Run just unit tests
dotnet test --filter "Category=Unit"
```
