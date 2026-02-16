# Public Release Preparation Checklist

**Branch**: `feature/public-release-prep`
**Target**: Open-source repository with future commercial cloud hosting
**License Strategy**: Permissive (MIT or Apache 2.0) to allow adoption while building hosted service

---

## 🎯 Release Strategy

### Business Model
- ✅ **Open Source Core**: Full codebase under permissive license
- 🎯 **Commercial Hosting**: Managed cloud service (future revenue stream)
- 📖 **Community Building**: GitHub stars, contributors, feedback loop

### Similar Successful Models
- Supabase (Apache 2.0 + hosted)
- GitLab (MIT + hosted)
- Sentry (BSL + hosted)
- PostHog (MIT + hosted)

---

## 📋 Critical Tasks (MUST DO)

### 1. License Selection ⚠️ BLOCKER
**Status**: [ ] Not started

Choose between:
- **MIT License** (Recommended)
  - ✅ Most permissive, encourages adoption
  - ✅ Allows commercial use (users can self-host or modify)
  - ✅ You retain rights to offer commercial hosted version
  - ✅ Simple, well-understood

- **Apache 2.0**
  - ✅ Same benefits as MIT
  - ✅ Includes explicit patent grant (more protection)
  - ⚠️ Slightly more complex

**Action**: Create `LICENSE` file with chosen license

---

### 2. Security Documentation ⚠️ CRITICAL
**Status**: [ ] Not started

#### A. Create SECURITY.md
Document:
- ⚠️ **NO AUTHENTICATION** - Project is pre-alpha, development only
- ⚠️ **NOT PRODUCTION READY** - Anyone with network access can read/modify/delete data
- How to report vulnerabilities (email or GitHub Security tab)
- Roadmap for auth implementation

#### B. Update README.md with Security Warnings
Add prominent warning at top:
```markdown
## ⚠️ Security Notice

**This project is in pre-alpha development and NOT production-ready.**

- ❌ No authentication or authorization
- ❌ No rate limiting
- ❌ Default development credentials included
- ✅ Suitable for local development and testing only

**DO NOT** deploy to public networks without implementing authentication first.
```

#### C. Update appsettings.json
Add comment at top:
```json
{
  "_comment": "⚠️ DEVELOPMENT CREDENTIALS - Change these for any deployment!",
  ...
}
```

#### D. Create .env.example
Template for users to copy and customize:
```bash
# Database
POSTGRES_PASSWORD=change_me_in_production
# MinIO
MINIO_ROOT_USER=change_me
MINIO_ROOT_PASSWORD=change_me_strong_password
```

---

### 3. README.md Expansion ⚠️ CRITICAL
**Status**: [ ] Not started

Current README is 13 lines. Expand to include:

```markdown
# AIKnowledgePlatform

> Open-source AI-powered knowledge management platform. Transform documents into searchable knowledge for AI agents.

⚠️ **Pre-Alpha**: Development only. No authentication. See [SECURITY.md](SECURITY.md).

## Features

- 🗂️ **Container-Based Organization**: Isolated projects with folder hierarchies
- 🔍 **Hybrid Search**: Vector similarity + keyword FTS with RRF fusion
- 📄 **Multi-Format Support**: PDF, Office docs, Markdown, plain text
- 🚀 **Real-Time Ingestion**: Background processing with SignalR progress
- 🎛️ **Runtime Settings**: Configure chunking, embeddings, search without restart
- 🌐 **Multiple Interfaces**: Web UI, REST API, CLI, MCP server

## Quick Start

### Prerequisites
- Docker & Docker Compose
- .NET 10 SDK (for development)
- (Optional) Ollama for local embeddings

### Run with Docker
\`\`\`bash
# Clone and start
git clone https://github.com/yourusername/AIKnowledgePlatform.git
cd AIKnowledgePlatform
docker-compose up -d

# Access UI
open http://localhost:5001
\`\`\`

### Development Setup
\`\`\`bash
# Start infrastructure
docker-compose up -d postgres minio

# Run web app
dotnet run --project src/AIKnowledge.Web

# Run tests
dotnet test
\`\`\`

## Architecture

[Include simplified version of the architecture diagram from docs/architecture.md]

## Documentation

- [Architecture Guide](docs/architecture.md)
- [API Reference](docs/api.md)
- [Development Guide](CLAUDE.md)
- [Security Notice](SECURITY.md)

## Roadmap

### Current Status (v0.1.0-alpha)
- ✅ Document ingestion pipeline
- ✅ Hybrid search
- ✅ Container-based file browser
- ✅ Web UI, CLI, MCP server
- ✅ 171 passing tests

### Upcoming (v0.2.0)
- 🔐 **Authentication & Authorization** (in progress)
  - Password-based auth
  - API key support
  - Role-based access control
- 🔒 Rate limiting
- 📊 Usage analytics
- 🌐 CORS configuration

### Future
- Multi-user workspaces
- Real-time collaboration
- Advanced RAG features
- Cloud deployment guides

## Commercial Hosting

While AIKnowledgePlatform is open source and free to self-host, we will offer a **managed cloud service** for teams who want:
- Zero-ops deployment
- Automatic backups & scaling
- Enterprise support
- SLA guarantees

Interested? Join the waitlist at [your-domain.com]

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## License

[MIT/Apache 2.0] - See [LICENSE](LICENSE) for details.

## Support

- 📖 [Documentation](docs/)
- 🐛 [Issue Tracker](https://github.com/yourusername/AIKnowledgePlatform/issues)
- 💬 [Discussions](https://github.com/yourusername/AIKnowledgePlatform/discussions)
\`\`\`
```

---

### 4. CONTRIBUTING.md ⚠️ IMPORTANT
**Status**: [ ] Not started

Create contribution guidelines:
- Code style (already documented in CLAUDE.md)
- Testing requirements (unit + integration tests)
- PR process
- Commit message format
- Development setup
- How to report bugs vs feature requests

**Template**: Use GitHub's standard Contributing template and customize

---

### 5. CODE_OF_CONDUCT.md
**Status**: [ ] Not started

**Action**: Use Contributor Covenant v2.1 (industry standard)

```bash
# Auto-generate
curl https://www.contributor-covenant.org/version/2/1/code_of_conduct/code_of_conduct.txt > CODE_OF_CONDUCT.md
```

---

## 🔧 Secondary Tasks (SHOULD DO)

### 6. GitHub Repository Configuration
**Status**: [ ] Not started

- [ ] Add repository description
- [ ] Add topics/tags (dotnet, blazor, ai, knowledge-management, rag, vector-search, pgvector)
- [ ] Enable GitHub Discussions
- [ ] Configure Issue Templates
  - Bug report template
  - Feature request template
  - Question template
- [ ] Add PR template
- [ ] Enable Security tab for vulnerability reporting
- [ ] Add status badges to README (build, tests, license)

---

### 7. CI/CD Setup
**Status**: [ ] Not started

Create `.github/workflows/`:
- **ci.yml**: Build and test on PR
- **release.yml**: Docker image publishing
- **codeql.yml**: Security scanning

**Priority**: Medium (can add post-launch)

---

### 8. Docker Image Publishing
**Status**: [ ] Not started

- [ ] Create optimized Dockerfile (multi-stage build)
- [ ] Publish to Docker Hub or GHCR
- [ ] Add tags (latest, semantic versions)
- [ ] Document image usage in README

**Priority**: Medium (helps adoption)

---

### 9. Demo / Screenshots
**Status**: [ ] Not started

Add to README:
- [ ] Screenshot of container list page
- [ ] Screenshot of file browser
- [ ] Screenshot of search results
- [ ] (Optional) GIF showing upload → search workflow

**Priority**: Low (nice to have)

---

### 10. Documentation Improvements
**Status**: [ ] Not started

- [ ] Add deployment guide for common scenarios:
  - Local development
  - Docker Compose (single machine)
  - (Future) Kubernetes
  - (Future) Cloud platforms
- [ ] API examples with curl commands
- [ ] CLI usage examples
- [ ] MCP integration guide for Claude Desktop

---

## 🚀 Launch Checklist

When ready to make repository public:

- [x] Clean `.claude/` directory (settings.local.json gitignored, state/ documented)
- [ ] All critical tasks complete (1-5)
- [ ] LICENSE file exists
- [ ] README has security warnings
- [ ] SECURITY.md exists
- [ ] No secrets in commit history (`git log --all --full-history -- '*secret*' '*password*'`)
- [ ] .gitignore covers all sensitive files
- [ ] Default credentials documented as "change me"
- [ ] Tests passing (171/171)
- [ ] Documentation links work
- [ ] Create GitHub Release v0.1.0-alpha
- [ ] Tag commit: `git tag -a v0.1.0-alpha -m "Pre-alpha public release"`
- [ ] Push tags: `git push --tags`
- [ ] Repository visibility: Public
- [ ] Announce (optional): Reddit, Hacker News, Twitter/X

### Note on `.claude/` Directory

The `.claude/state/` documentation is **intentionally public** as it provides valuable architectural context for contributors. See [.claude/README.md](../.claude/README.md) for details. No sensitive information is included.

---

## 💰 Commercial Hosting Prep (Future)

### Domain & Branding
- [ ] Register domain (e.g., aikp.cloud, yourbrand.ai)
- [ ] Design logo/branding
- [ ] Landing page

### Business Infrastructure
- [ ] Payment processor (Stripe)
- [ ] Billing system
- [ ] Usage tracking/metering
- [ ] Customer portal

### Technical Infrastructure
- [ ] Multi-tenant architecture design
- [ ] Deploy to cloud (AWS/Azure/GCP)
- [ ] Monitoring & alerting
- [ ] Backup & disaster recovery
- [ ] Support ticket system

**Timeline**: Post-launch, once community validates product-market fit

---

## 📝 Notes

### Authentication Implementation (v0.2.0)
When implementing auth, consider:
- ASP.NET Core Identity for password auth
- API keys for programmatic access (CLI, MCP)
- Optional OAuth/OIDC (Google, GitHub, Microsoft)
- Settings: `AuthenticationMode: None | Password | OAuth`
- Backward compat: Keep "None" mode for local dev

### Open Source + Commercial Hosting Model
This model works when:
✅ Open source builds community & trust
✅ Self-hosting is possible but requires effort
✅ Hosted service adds value (convenience, support, SLA)
✅ Clear boundaries (core features = free, premium features = paid, OR hosting = paid)

Your model: **Core features free, hosting paid** (like Supabase, GitLab)

---

## ✅ Progress Tracking

**Phase 1: Critical Documentation** ✅ **COMPLETE**
- [x] Task 1: LICENSE (MIT)
- [x] Task 2: SECURITY.md
- [x] Task 3: README.md expansion (13 → 275+ lines)
- [x] Task 4: CONTRIBUTING.md
- [x] Task 5: CODE_OF_CONDUCT.md (Contributor Covenant v2.1)

**Phase 2: Repository Polish** ✅ **COMPLETE**
- [x] Task 6: GitHub config (issue templates, PR template, status badges, CI/CD, docs/GITHUB_SETUP.md)
- [x] Task 7: .env.example
- [x] Task 8: appsettings.json warnings

**Phase 3: Go Public** (Est: 1 hour)
- [ ] Final review
- [ ] Make public
- [ ] Create release
- [ ] Announce

**Total Estimated Time**: 7-11 hours of focused work

---

## 🎉 Success Criteria

Repository is ready when:
- ✅ License clarifies usage rights
- ✅ Security warnings are prominent
- ✅ README explains what it does, how to run it, and what's coming
- ✅ Contributing guidelines exist
- ✅ New users can `git clone` → `docker-compose up` → access UI
- ✅ Tests pass
- ✅ No secrets exposed

---

*Created: 2026-02-08*
*Branch: feature/public-release-prep*
*Target: Open source release with commercial hosting business model*
