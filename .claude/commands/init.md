# Initialize Project

Run when setting up Connapse for the first time or onboarding a new environment.

## Steps

### 1. Create State Files (if missing)

Create each `.claude/state/` file only if it doesn't already exist.

### 2. Create Source Structure

```bash
mkdir -p src/Connapse.Web/{Components,Pages,Services}
mkdir -p src/Connapse.Core/{Models,Interfaces,Extensions}
mkdir -p src/Connapse.Ingestion/{Parsers,Chunking,Pipeline}
mkdir -p src/Connapse.Search/{Vector,Hybrid,Web}
mkdir -p src/Connapse.Agents/{Tools,Memory,Orchestration}
mkdir -p src/Connapse.Storage/{Documents,Vectors,Files}
mkdir -p src/Connapse.CLI/Commands
mkdir -p tests/{Connapse.Core.Tests,Connapse.Ingestion.Tests,Connapse.Integration.Tests}
mkdir -p docs
```

### 3. Create Solution (if missing)

```bash
dotnet new sln -n Connapse
dotnet new blazor -n Connapse.Web -o src/Connapse.Web -f net10.0
dotnet new classlib -n Connapse.Core -o src/Connapse.Core -f net10.0
dotnet new classlib -n Connapse.Ingestion -o src/Connapse.Ingestion -f net10.0
dotnet new classlib -n Connapse.Search -o src/Connapse.Search -f net10.0
dotnet new classlib -n Connapse.Agents -o src/Connapse.Agents -f net10.0
dotnet new classlib -n Connapse.Storage -o src/Connapse.Storage -f net10.0
dotnet new console -n Connapse.CLI -o src/Connapse.CLI -f net10.0

# Add projects to solution
dotnet sln add src/Connapse.Web
dotnet sln add src/Connapse.Core
dotnet sln add src/Connapse.Ingestion
dotnet sln add src/Connapse.Search
dotnet sln add src/Connapse.Agents
dotnet sln add src/Connapse.Storage
dotnet sln add src/Connapse.CLI

# Add test projects
dotnet new xunit -n Connapse.Core.Tests -o tests/Connapse.Core.Tests -f net10.0
dotnet new xunit -n Connapse.Ingestion.Tests -o tests/Connapse.Ingestion.Tests -f net10.0
dotnet new xunit -n Connapse.Integration.Tests -o tests/Connapse.Integration.Tests -f net10.0
dotnet sln add tests/Connapse.Core.Tests
dotnet sln add tests/Connapse.Ingestion.Tests
dotnet sln add tests/Connapse.Integration.Tests
```

### 4. Add Project References

```bash
# Core is referenced by everything
dotnet add src/Connapse.Web reference src/Connapse.Core
dotnet add src/Connapse.Ingestion reference src/Connapse.Core
dotnet add src/Connapse.Search reference src/Connapse.Core
dotnet add src/Connapse.Agents reference src/Connapse.Core
dotnet add src/Connapse.Storage reference src/Connapse.Core
dotnet add src/Connapse.CLI reference src/Connapse.Core

# Web references feature projects
dotnet add src/Connapse.Web reference src/Connapse.Ingestion
dotnet add src/Connapse.Web reference src/Connapse.Search
dotnet add src/Connapse.Web reference src/Connapse.Agents
dotnet add src/Connapse.Web reference src/Connapse.Storage

# CLI references feature projects
dotnet add src/Connapse.CLI reference src/Connapse.Ingestion
dotnet add src/Connapse.CLI reference src/Connapse.Search
dotnet add src/Connapse.CLI reference src/Connapse.Agents
dotnet add src/Connapse.CLI reference src/Connapse.Storage

# Ingestion needs storage
dotnet add src/Connapse.Ingestion reference src/Connapse.Storage

# Search needs storage
dotnet add src/Connapse.Search reference src/Connapse.Storage

# Agents need search and storage
dotnet add src/Connapse.Agents reference src/Connapse.Search
dotnet add src/Connapse.Agents reference src/Connapse.Storage
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
1. Define core interfaces in Connapse.Core
2. Implement basic Blazor shell with file upload
3. Create ingestion pipeline
4. Add vector storage