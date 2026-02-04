# AIKnowledgePlatform

Open-source knowledge management platform that empowers AI agents with organizational knowledge. Built with .NET 10 Blazor WebApp. Users upload files → files become instantly searchable agent knowledge. No technical expertise required.

## Quick Reference

```bash
# Development
dotnet build                              # Build all projects
dotnet run --project src/AIKnowledge.Web  # Run web app (https://localhost:5001)
dotnet test                               # Run all tests
dotnet test --filter "Category=Unit"      # Unit tests only

# CLI (src/AIKnowledge.CLI)
aikp ingest <path>            # Add file/folder to knowledge base
aikp search "<query>"         # Search knowledge base
aikp chat                     # Interactive agent session
aikp serve                    # Start local web server
aikp config set <key> <value> # Update settings
```

## Architecture

```
src/
├── AIKnowledge.Web/          # Blazor WebApp (UI + API endpoints)
├── AIKnowledge.Core/         # Domain models, interfaces, shared logic
├── AIKnowledge.Ingestion/    # Document parsing, chunking, embedding pipeline
├── AIKnowledge.Search/       # Vector search, hybrid search, web search
├── AIKnowledge.Agents/       # Agent orchestration, tool definitions, memory
├── AIKnowledge.Storage/      # Vector DB, document store, file storage adapters
└── AIKnowledge.CLI/          # Command-line interface

tests/
├── AIKnowledge.Core.Tests/
├── AIKnowledge.Ingestion.Tests/
└── AIKnowledge.Integration.Tests/
```

## Agent Persistent State

**IMPORTANT**: This project uses `.claude/state/` to maintain context across sessions. Check relevant state files before major work; update them after.

| File | Purpose |
|------|---------|
| `.claude/state/decisions.md` | Architectural decisions with rationale |
| `.claude/state/conventions.md` | Code patterns and style choices for this project |
| `.claude/state/progress.md` | Current tasks, blockers, session history |
| `.claude/state/issues.md` | Known bugs, tech debt, workarounds |
| `.claude/state/api-surface.md` | Public interfaces, breaking change log |

### State Rules

1. **Before implementing**: Read `decisions.md` and `conventions.md`
2. **After completing work**: Update `progress.md` with what changed
3. **Made a trade-off?**: Document in `decisions.md`
4. **New pattern?**: Add to `conventions.md`
5. **Found a bug?**: Log in `issues.md`

## Code Conventions

### C# / .NET 10

- File-scoped namespaces, nullable enabled globally
- Records for DTOs and immutable types
- Primary constructors where appropriate
- `IOptions<T>` for all configuration
- Async everywhere—never block with `.Result` or `.Wait()`

### Blazor

- Interactive Server mode for real-time features
- Components in `Components/`, pages in `Pages/`
- Use `@inject`, not constructor injection in components
- Extract logic to services—components stay thin

### Testing

- xUnit + FluentAssertions
- Naming: `MethodName_Scenario_ExpectedResult`
- Mock external services (LLM, vector DB) in unit tests
- Integration tests use `WebApplicationFactory<Program>`

## Core Interfaces

```csharp
IKnowledgeIngester      // File → parsed → chunked → embedded → stored
IKnowledgeSearch        // Query → retrieve relevant chunks
IEmbeddingProvider      // Text → vector (swappable: Ollama, OpenAI, Azure)
IVectorStore            // Vector storage (swappable: SQLite-vec, Qdrant, Pinecone)
IWebSearchProvider      // External search (Brave, Serper, Tavily)
IAgentTool              // Tool interface for agent capabilities
IAgentMemory            // Persistent notes/memory for agents
```

## Knowledge Pipeline

```
[Upload/URL] → [Parse] → [Chunk] → [Embed] → [Store] → [Available to Agents]
                 ↓
         [Extract Metadata]
                 ↓
         [Document Store]
```

**Target**: < 30 seconds from upload to searchable.

## Configurable Strategies

All swappable via `appsettings.json` or UI settings:

- **Chunking**: `Semantic` | `FixedSize` | `Recursive` | `DocumentAware`
- **Embedding**: `Ollama` | `OpenAI` | `AzureOpenAI` | `Anthropic`
- **Vector Store**: `SqliteVec` | `Qdrant` | `Pinecone` | `AzureAISearch`
- **LLM**: `Anthropic` | `OpenAI` | `AzureOpenAI` | `Ollama`
- **Web Search**: `Brave` | `Serper` | `Tavily` | `None`

## Local-First Design

Default runs entirely local:
- SQLite + sqlite-vec for storage
- Ollama for embeddings and LLM
- File system for uploads

Cloud deployment swaps in managed services via DI—no code changes.

## Agent Tool Design

Every feature asks: "How would an AI agent use this?"

- Return structured JSON alongside human-readable text
- Include confidence scores where applicable
- Make errors actionable: "File not found at X. Did you mean Y?"
- Support streaming for long operations
- Tools self-describe via JSON Schema

## Initialization

When asked to initialize, create this structure:

```
AIKnowledgePlatform/
├── .claude/
│   ├── state/
│   │   ├── decisions.md
│   │   ├── conventions.md
│   │   ├── progress.md
│   │   ├── issues.md
│   │   └── api-surface.md
│   └── commands/
│       └── init.md
├── src/
│   ├── AIKnowledge.Web/
│   ├── AIKnowledge.Core/
│   ├── AIKnowledge.Ingestion/
│   ├── AIKnowledge.Search/
│   ├── AIKnowledge.Agents/
│   ├── AIKnowledge.Storage/
│   └── AIKnowledge.CLI/
├── tests/
├── docs/
├── .gitignore
└── AIKnowledgePlatform.sln
```

See `.claude/commands/init.md` for full initialization script.

## Critical Rules

- **NEVER** commit secrets—use user-secrets or environment variables
- **NEVER** store uploaded files in git
- **ALWAYS** validate uploads (type, size, content scanning)
- **ALWAYS** sanitize queries before RAG (prompt injection risk)
- **ALWAYS** check `.claude/state/decisions.md` before architectural changes

## Settings Hierarchy

```
appsettings.json (defaults)
  └── appsettings.{Environment}.json
       └── User secrets (dev only)
            └── Environment variables
                 └── CLI arguments (highest priority)
```

## Getting Help

- `docs/architecture.md` — Detailed system design
- `docs/api.md` — API reference  
- `docs/deployment.md` — Deployment guides
- `.claude/state/` — Project-specific decisions and context