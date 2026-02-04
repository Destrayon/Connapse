# Initialize Project

Run when setting up AIKnowledgePlatform for the first time or onboarding a new environment.

## Steps

### 1. Create State Files (if missing)

Create each `.claude/state/` file only if it doesn't already exist.

### 2. Create Source Structure

```bash
mkdir -p src/AIKnowledge.Web/{Components,Pages,Services}
mkdir -p src/AIKnowledge.Core/{Models,Interfaces,Extensions}
mkdir -p src/AIKnowledge.Ingestion/{Parsers,Chunking,Pipeline}
mkdir -p src/AIKnowledge.Search/{Vector,Hybrid,Web}
mkdir -p src/AIKnowledge.Agents/{Tools,Memory,Orchestration}
mkdir -p src/AIKnowledge.Storage/{Documents,Vectors,Files}
mkdir -p src/AIKnowledge.CLI/Commands
mkdir -p tests/{AIKnowledge.Core.Tests,AIKnowledge.Ingestion.Tests,AIKnowledge.Integration.Tests}
mkdir -p docs
```

### 3. Create Solution (if missing)

```bash
dotnet new sln -n AIKnowledgePlatform
dotnet new blazor -n AIKnowledge.Web -o src/AIKnowledge.Web -f net10.0
dotnet new classlib -n AIKnowledge.Core -o src/AIKnowledge.Core -f net10.0
dotnet new classlib -n AIKnowledge.Ingestion -o src/AIKnowledge.Ingestion -f net10.0
dotnet new classlib -n AIKnowledge.Search -o src/AIKnowledge.Search -f net10.0
dotnet new classlib -n AIKnowledge.Agents -o src/AIKnowledge.Agents -f net10.0
dotnet new classlib -n AIKnowledge.Storage -o src/AIKnowledge.Storage -f net10.0
dotnet new console -n AIKnowledge.CLI -o src/AIKnowledge.CLI -f net10.0

# Add projects to solution
dotnet sln add src/AIKnowledge.Web
dotnet sln add src/AIKnowledge.Core
dotnet sln add src/AIKnowledge.Ingestion
dotnet sln add src/AIKnowledge.Search
dotnet sln add src/AIKnowledge.Agents
dotnet sln add src/AIKnowledge.Storage
dotnet sln add src/AIKnowledge.CLI

# Add test projects
dotnet new xunit -n AIKnowledge.Core.Tests -o tests/AIKnowledge.Core.Tests -f net10.0
dotnet new xunit -n AIKnowledge.Ingestion.Tests -o tests/AIKnowledge.Ingestion.Tests -f net10.0
dotnet new xunit -n AIKnowledge.Integration.Tests -o tests/AIKnowledge.Integration.Tests -f net10.0
dotnet sln add tests/AIKnowledge.Core.Tests
dotnet sln add tests/AIKnowledge.Ingestion.Tests
dotnet sln add tests/AIKnowledge.Integration.Tests
```

### 4. Add Project References

```bash
# Core is referenced by everything
dotnet add src/AIKnowledge.Web reference src/AIKnowledge.Core
dotnet add src/AIKnowledge.Ingestion reference src/AIKnowledge.Core
dotnet add src/AIKnowledge.Search reference src/AIKnowledge.Core
dotnet add src/AIKnowledge.Agents reference src/AIKnowledge.Core
dotnet add src/AIKnowledge.Storage reference src/AIKnowledge.Core
dotnet add src/AIKnowledge.CLI reference src/AIKnowledge.Core

# Web references feature projects
dotnet add src/AIKnowledge.Web reference src/AIKnowledge.Ingestion
dotnet add src/AIKnowledge.Web reference src/AIKnowledge.Search
dotnet add src/AIKnowledge.Web reference src/AIKnowledge.Agents
dotnet add src/AIKnowledge.Web reference src/AIKnowledge.Storage

# CLI references feature projects
dotnet add src/AIKnowledge.CLI reference src/AIKnowledge.Ingestion
dotnet add src/AIKnowledge.CLI reference src/AIKnowledge.Search
dotnet add src/AIKnowledge.CLI reference src/AIKnowledge.Agents
dotnet add src/AIKnowledge.CLI reference src/AIKnowledge.Storage

# Ingestion needs storage
dotnet add src/AIKnowledge.Ingestion reference src/AIKnowledge.Storage

# Search needs storage
dotnet add src/AIKnowledge.Search reference src/AIKnowledge.Storage

# Agents need search and storage
dotnet add src/AIKnowledge.Agents reference src/AIKnowledge.Search
dotnet add src/AIKnowledge.Agents reference src/AIKnowledge.Storage
```

### 5. Create .gitignore

```gitignore
# Build
bin/
obj/
publish/

# IDE
.vs/
.vscode/
*.user
*.suo
.idea/

# Secrets
appsettings.*.json
!appsettings.json
!appsettings.Development.json.template

# Data
*.db
*.db-shm
*.db-wal
uploads/
knowledge-data/

# Logs
logs/
*.log

# Test
TestResults/
coverage/

# OS
.DS_Store
Thumbs.db
```

### 6. Report Completion

List what was created and suggest next steps:
1. Define core interfaces in AIKnowledge.Core
2. Implement basic Blazor shell with file upload
3. Create ingestion pipeline
4. Add vector storage