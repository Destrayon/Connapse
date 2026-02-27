# Connapse

> Open-source AI-powered knowledge management platform. Transform documents into searchable knowledge for AI agents.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Build](https://img.shields.io/github/actions/workflow/status/Destrayon/Connapse/ci.yml?branch=main&label=build)](https://github.com/Destrayon/Connapse/actions)
[![Tests](https://img.shields.io/badge/tests-256%20passing-success)](https://github.com/Destrayon/Connapse/actions)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)
[![GitHub Issues](https://img.shields.io/github/issues/Destrayon/Connapse)](https://github.com/Destrayon/Connapse/issues)
[![GitHub Stars](https://img.shields.io/github/stars/Destrayon/Connapse?style=social)](https://github.com/Destrayon/Connapse/stargazers)
[![Docker](https://img.shields.io/badge/Docker-ready-2496ED?logo=docker)](https://github.com/Destrayon/Connapse#-quick-start)

---

## 🎬 Demo

https://github.com/user-attachments/assets/db93c576-3a51-4b17-a56e-5b67ea8b847c

> *Upload documents, search your knowledge base, and chat with your data — all in under 30 seconds.*

---

## ⚠️ Security Notice

**This project is in active development (v0.2.0) and approaching production-readiness.**

v0.2.0 ships a complete three-tier authentication system (Cookie + PAT + JWT), role-based access control, invite-only user registration, agent identity management, and audit logging.

- ✅ **Authentication and authorization** (v0.2.0)
- ✅ **Role-based access control** (Admin / Editor / Viewer / Agent)
- ✅ **Audit logging**
- ⚠️ **Rate limiting** — not yet implemented (not planned for self-hosted deployments)
- ⚠️ **Set a strong `Identity__Jwt__Secret`** in production — see [deployment guide](docs/deployment.md)

See [SECURITY.md](SECURITY.md) for the full security policy.

---

## 🚀 Features

- **🗂️ Container-Based Organization**: Isolated projects with S3-like folder hierarchies
- **🔍 Hybrid Search**: Vector similarity + keyword full-text search with RRF fusion
- **📄 Multi-Format Support**: PDF, Office documents, Markdown, plain text
- **⚡ Real-Time Ingestion**: Background processing with live progress updates (SignalR)
- **🎛️ Runtime Configuration**: Change chunking, embeddings, search settings without restart
- **🔐 Three-Tier Auth**: Cookie sessions + Personal Access Tokens + JWT — role-based access control
- **👥 Invite-Only Users**: Admin controls access; agent identities managed separately
- **🤖 Agent Management**: Dedicated agent entities with API key lifecycle management
- **📋 Audit Logging**: Structured audit trail for uploads, deletes, and container operations
- **🌐 Multiple Interfaces**:
  - Web UI (Blazor Server)
  - REST API (`/api/v1/auth/`, `/api/v1/agents/`, `/api/containers/`)
  - Command-line interface (`connapse auth login`, `connapse upload`, `connapse search`)
  - MCP server (for Claude Desktop integration — agent API key auth)
- **🐳 Fully Dockerized**: PostgreSQL + pgvector, MinIO (S3), optional Ollama
- **📦 CLI Distribution**: Native self-contained binaries (win/linux/osx) + .NET global tool
- **🧪 Tested**: 256 passing tests (unit + integration)

---

## 📦 Quick Start

### Prerequisites

- [Docker](https://docs.docker.com/get-docker/) & [Docker Compose](https://docs.docker.com/compose/install/)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (for development)
- (Optional) [Ollama](https://ollama.ai/) for local embeddings

### Run with Docker Compose

```bash
# Clone the repository
git clone https://github.com/Destrayon/Connapse.git
cd Connapse

# Set required auth environment variables (or use a .env file)
export CONNAPSE_ADMIN_EMAIL=admin@example.com
export CONNAPSE_ADMIN_PASSWORD=YourSecurePassword123!
export Identity__Jwt__Secret=$(openssl rand -base64 64)

# Start all services (PostgreSQL, MinIO, Web App)
docker-compose up -d

# Open http://localhost:5001 — log in with the admin credentials above
```

The first run will:
1. Pull Docker images (~2-5 minutes)
2. Initialize PostgreSQL with pgvector extension and run EF Core migrations
3. Create MinIO buckets
4. Seed the admin account (from env vars) and start the web application

### Development Setup

```bash
# Start infrastructure only (database + object storage)
docker-compose up -d postgres minio

# Run the web app locally
dotnet run --project src/Connapse.Web

# Run all tests
dotnet test

# Run just unit tests
dotnet test --filter "Category=Unit"
```

### Using the CLI

Install the CLI (choose one option):

```bash
# Option A: .NET Global Tool (requires .NET 10)
dotnet tool install -g Connapse.CLI

# Option B: Download native binary from GitHub Releases (no .NET required)
# https://github.com/Destrayon/Connapse/releases
```

Basic usage:

```bash
# Authenticate first
connapse auth login --url https://localhost:5001

# Create a container (project)
connapse container create my-project --description "My knowledge base"

# Upload files
connapse upload ./documents --container my-project

# Search
connapse search "your query" --container my-project
```

### Using with Claude Desktop (MCP)

Connapse includes a Model Context Protocol (MCP) server for integration with Claude Desktop.

**Setup**:
1. Create an Agent in the Connapse UI (`/admin/agents`) and generate an API key
2. Configure Claude Desktop to send requests to your Connapse instance with the agent's `X-Api-Key`

The MCP server exposes 7 tools: `container_create`, `container_list`, `container_delete`, `upload_file`, `list_files`, `delete_file`, `search_knowledge`.

---

## 🏗️ Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     Access Surfaces                         │
│  Web UI (Blazor)  │  REST API  │  CLI  │  MCP Server       │
└────────────┬────────────────────────────────────────────────┘
             │
┌────────────▼────────────────────────────────────────────────┐
│                   Core Services Layer                        │
│  Document Store  │  Vector Store  │  Search  │  Ingestion  │
└────────────┬────────────────────────────────────────────────┘
             │
┌────────────▼────────────────────────────────────────────────┐
│                    Infrastructure                            │
│  PostgreSQL+pgvector  │  MinIO (S3)  │  Ollama (optional)  │
└─────────────────────────────────────────────────────────────┘
```

### Data Flow: Upload → Search

```
[Upload] → [Parse] → [Chunk] → [Embed] → [Store] → [Searchable]
              ↓
         [Metadata]
              ↓
        [Document Store]
```

**Target**: < 30 seconds from upload to searchable.

**Key Technologies**:
- **Database**: PostgreSQL 17 + pgvector for vector embeddings
- **Object Storage**: MinIO (S3-compatible) for original files
- **Backend**: ASP.NET Core 10 Minimal APIs
- **Frontend**: Blazor Server (interactive mode)
- **Embeddings**: Ollama (default), OpenAI, Azure OpenAI (configurable)
- **Search**: Hybrid vector + keyword with Reciprocal Rank Fusion

---

## 📚 Documentation

- [Architecture Guide](docs/architecture.md) - System design and component overview
- [API Reference](docs/api.md) - REST API endpoints and examples
- [Development Guide](CLAUDE.md) - Code conventions and patterns
- [Security Policy](SECURITY.md) - Security limitations and roadmap
- [Contributing Guidelines](CONTRIBUTING.md) - How to contribute

---

## 🗺️ Roadmap

Connapse is pre-1.0. Major design work is tracked in [Discussions](https://github.com/Destrayon/Connapse/discussions).

### v0.1.0 — Foundation (Complete)
- ✅ Document ingestion pipeline (PDF, Office, Markdown, text)
- ✅ Hybrid search (vector + keyword with RRF fusion)
- ✅ Container-based file browser with folders
- ✅ Web UI, REST API, CLI, MCP server

### v0.2.0 — Security & Auth (Complete)
- ✅ Three-tier auth: Cookie + Personal Access Tokens + JWT (HS256)
- ✅ Role-based access control (Admin / Editor / Viewer / Agent)
- ✅ Invite-only user registration (admin-controlled)
- ✅ First-class agent entities with API key lifecycle
- ✅ Agent management UI + PAT management UI
- ✅ Audit logging (uploads, deletes, container operations)
- ✅ CLI auth commands (`auth login`, `auth whoami`, `auth pat`)
- ✅ GitHub Actions release pipeline (native binaries + NuGet tool)
- ✅ 256 passing tests (unit + integration)

### v0.3.0 — Connector Architecture
- Pluggable connector system for multi-source search — [Design Discussion](https://github.com/Destrayon/Connapse/discussions/8)
- Scope-based filtering (folders, channels, repos per connector)
- Local filesystem and S3 connectors

### Future
- **v0.4.0**: Communication connectors (Slack, Discord)
- **v0.5.0**: Knowledge platform connectors (Notion, Confluence, GitHub)
- **v1.0.0**: Production-ready stable release

---

## 🤝 Contributing

We welcome contributions! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

**Quick contribution checklist**:
- Fork the repo and create a feature branch
- Follow code conventions in [CLAUDE.md](CLAUDE.md)
- Write tests for new features (xUnit + FluentAssertions)
- Ensure all tests pass: `dotnet test`
- Update documentation if needed
- Submit a pull request

**Good first issues**: Check [issues labeled `good-first-issue`](https://github.com/Destrayon/Connapse/labels/good-first-issue)

---

## 📄 License

This project is licensed under the **MIT License** - see [LICENSE](LICENSE) for details.

You are free to:
- ✅ Use commercially
- ✅ Modify
- ✅ Distribute
- ✅ Sublicense
- ✅ Use privately

The only requirement is to include the copyright notice and license in any substantial portions of the software.

---

## 💬 Support & Community

- 📖 **Documentation**: [docs/](docs/)
- 🐛 **Bug Reports**: [GitHub Issues](https://github.com/Destrayon/Connapse/issues)
- 💡 **Feature Requests**: [GitHub Discussions](https://github.com/Destrayon/Connapse/discussions)
- 🔒 **Security Issues**: See [SECURITY.md](SECURITY.md)

---

## 🙏 Acknowledgments

Built with:
- [.NET](https://dotnet.microsoft.com/) - Application framework
- [Blazor](https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor) - Web UI
- [PostgreSQL](https://www.postgresql.org/) + [pgvector](https://github.com/pgvector/pgvector) - Vector database
- [MinIO](https://min.io/) - S3-compatible object storage
- [Ollama](https://ollama.ai/) - Local LLM inference

---

**⭐ If you find this project useful, please star the repository to show your support!**
