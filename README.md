# Connapse

> Open-source AI-powered knowledge management platform. Transform documents into searchable knowledge for AI agents.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Build](https://img.shields.io/github/actions/workflow/status/yourusername/Connapse/ci.yml?branch=main&label=build)](https://github.com/yourusername/Connapse/actions)
[![Tests](https://img.shields.io/badge/tests-171%20passing-success)](https://github.com/yourusername/Connapse/actions)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)
[![GitHub Issues](https://img.shields.io/github/issues/yourusername/Connapse)](https://github.com/yourusername/Connapse/issues)
[![GitHub Stars](https://img.shields.io/github/stars/yourusername/Connapse?style=social)](https://github.com/yourusername/Connapse/stargazers)
[![Docker](https://img.shields.io/badge/Docker-ready-2496ED?logo=docker)](https://github.com/yourusername/Connapse#-quick-start)

---

## ⚠️ Security Notice

**This project is in pre-alpha development (v0.1.0-alpha) and NOT production-ready.**

- ❌ **No authentication or authorization**
- ❌ **No rate limiting**
- ❌ **Default development credentials included**
- ✅ **Suitable for local development and testing only**

**DO NOT** deploy to public networks without implementing authentication first. See [SECURITY.md](SECURITY.md) for details.

Authentication and access control are the **#1 priority** for v0.2.0.

---

## 🚀 Features

- **🗂️ Container-Based Organization**: Isolated projects with S3-like folder hierarchies
- **🔍 Hybrid Search**: Vector similarity + keyword full-text search with RRF fusion
- **📄 Multi-Format Support**: PDF, Office documents, Markdown, plain text
- **⚡ Real-Time Ingestion**: Background processing with live progress updates (SignalR)
- **🎛️ Runtime Configuration**: Change chunking, embeddings, search settings without restart
- **🌐 Multiple Interfaces**:
  - Web UI (Blazor Server)
  - REST API
  - Command-line interface
  - MCP server (for Claude Desktop integration)
- **🐳 Fully Dockerized**: PostgreSQL + pgvector, MinIO (S3), optional Ollama
- **🧪 Tested**: 171 passing tests (unit + integration)

---

## 📦 Quick Start

### Prerequisites

- [Docker](https://docs.docker.com/get-docker/) & [Docker Compose](https://docs.docker.com/compose/install/)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (for development)
- (Optional) [Ollama](https://ollama.ai/) for local embeddings

### Run with Docker Compose

```bash
# Clone the repository
git clone https://github.com/yourusername/Connapse.git
cd Connapse

# Start all services (PostgreSQL, MinIO, Web App)
docker-compose up -d

# Access the web UI
# Open http://localhost:5001 in your browser
```

The first run will:
1. Pull Docker images (~2-5 minutes)
2. Initialize PostgreSQL with pgvector extension
3. Create MinIO buckets
4. Start the web application

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

```bash
# Build the CLI
dotnet build src/Connapse.CLI

# Create a container (project)
connapse container create my-project --description "My knowledge base"

# Upload files
connapse upload ./documents --container my-project

# Search
connapse search "your query" --container my-project

# Interactive chat
connapse chat --container my-project
```

### Using with Claude Desktop (MCP)

Connapse includes a Model Context Protocol (MCP) server for integration with Claude Desktop:

1. Start the MCP server: `connapse serve --mcp`
2. Configure Claude Desktop to connect to the server
3. Use natural language to manage your knowledge base

See [docs/mcp-integration.md](docs/mcp-integration.md) for setup details.

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

### Current Status (v0.1.0-alpha)
- ✅ Document ingestion pipeline (PDF, Office, Markdown, text)
- ✅ Hybrid search (vector + keyword)
- ✅ Container-based file browser with folders
- ✅ Web UI with real-time progress
- ✅ REST API
- ✅ CLI tool
- ✅ MCP server for Claude Desktop
- ✅ 171 passing tests

### Next Release (v0.2.0 - Q2 2026)
**Focus**: Production readiness and security

- 🔐 **Authentication & Authorization**
  - Password-based auth (ASP.NET Core Identity)
  - API key support for CLI/MCP
  - Role-based access control (Admin, User, Read-Only)
- 🔒 **Security Enhancements**
  - Rate limiting on all endpoints
  - CORS configuration
  - Audit logging
  - Secure credential management
- 📊 **Observability**
  - Usage analytics
  - Performance monitoring
  - Health check endpoints

### Future Releases
- **v0.3.0**: Multi-user workspaces and collaboration
- **v0.4.0**: Advanced RAG features (reranking, query expansion)
- **v0.5.0**: OAuth/SSO integration
- **v1.0.0**: Production-ready stable release

See [docs/roadmap.md](docs/roadmap.md) for detailed feature planning.

---

## 💼 Commercial Hosting

While Connapse is **open source and free to self-host**, we plan to offer a **managed cloud service** for teams who want:

- ✨ Zero-ops deployment (no Docker, no infrastructure)
- 🔄 Automatic backups and scaling
- 🛟 Priority support with SLA guarantees
- 🔒 Enterprise security and compliance
- 👥 Multi-user workspaces with advanced permissions

**Interested in hosted version?** Join the waitlist at [https://your-domain.com](https://your-domain.com) *(coming soon)*

The hosted service will help fund continued development of the open-source project.

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

**Good first issues**: Check [issues labeled `good-first-issue`](https://github.com/yourusername/Connapse/labels/good-first-issue)

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
- 🐛 **Bug Reports**: [GitHub Issues](https://github.com/yourusername/Connapse/issues)
- 💡 **Feature Requests**: [GitHub Discussions](https://github.com/yourusername/Connapse/discussions)
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
