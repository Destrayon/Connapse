# GitHub Repository Setup Guide

This guide walks through the manual GitHub repository configuration needed to complete Phase 2 of the public release preparation.

---

## 📋 Overview

The following items require manual configuration through the GitHub web interface:

1. ✅ **Issue Templates** - Created as files (automated)
2. ✅ **PR Template** - Created as file (automated)
3. ✅ **Status Badges** - Added to README.md (automated)
4. ⚠️ **Repository Settings** - Requires manual configuration (see below)

---

## ⚙️ Manual Configuration Steps

### 1. Repository Description & Topics

**Location**: Repository home page → "About" section (gear icon)

#### Repository Description
```
Open-source AI-powered knowledge management platform for AI agents. Transform documents into searchable knowledge with hybrid vector + keyword search. Built with .NET 10 Blazor.
```

#### Topics (Tags)
Add the following topics to improve discoverability:
```
dotnet
dotnet10
blazor
ai
artificial-intelligence
knowledge-management
rag
retrieval-augmented-generation
vector-search
pgvector
postgresql
minio
s3
ollama
embeddings
hybrid-search
mcp
model-context-protocol
document-processing
semantic-search
```

**How to add**:
1. Click the gear icon next to "About" on the repository home page
2. Add the description in the "Description" field
3. Add topics in the "Topics" field (comma-separated or space-separated)
4. Check "Releases" if you want to show releases in the sidebar
5. Click "Save changes"

---

### 2. Enable GitHub Discussions

**Location**: Settings → Features → Discussions

**Steps**:
1. Go to repository Settings
2. Scroll to "Features" section
3. Check "Discussions"
4. Click "Set up discussions"
5. GitHub will create a default discussion structure

**Categories to create** (after enabling):
- 📣 Announcements (format: Announcement)
- 💡 Ideas (format: Discussion)
- 🙏 Q&A (format: Q&A)
- 🎉 Show and Tell (format: Discussion)
- 💬 General (format: Discussion)

---

### 3. Enable GitHub Security Tab

**Location**: Settings → Security → Code security and analysis

**Steps**:
1. Go to repository Settings
2. Click "Security" in the left sidebar
3. Enable the following:
   - ✅ **Dependency graph** (shows project dependencies)
   - ✅ **Dependabot alerts** (security vulnerability alerts)
   - ✅ **Dependabot security updates** (automated security PRs)
   - ⚠️ **Dependabot version updates** (optional - creates PRs for dependency updates)
   - ✅ **Secret scanning** (detects committed secrets)
   - ✅ **Private vulnerability reporting** (allows secure vulnerability disclosure)

**Private Vulnerability Reporting**:
- This enables the "Security Advisories" feature
- Users can privately report security issues
- You receive email notifications

---

### 4. Configure Branch Protection Rules

**Location**: Settings → Branches → Add branch protection rule

**For `main` branch**:

#### Rule name
```
main
```

#### Protection settings (recommended):
- ✅ **Require pull request before merging**
  - ✅ Require approvals: 1 (if you have collaborators)
  - ✅ Dismiss stale pull request approvals when new commits are pushed
- ✅ **Require status checks to pass before merging**
  - ✅ Require branches to be up to date before merging
  - Select: "build", "test" (after CI workflow is created)
- ✅ **Require conversation resolution before merging**
- ❌ **Require signed commits** (optional, more strict)
- ❌ **Require linear history** (optional, prevents merge commits)
- ✅ **Include administrators** (apply rules to admins too)
- ✅ **Allow force pushes** - **OFF** (prevent force push to main)
- ✅ **Allow deletions** - **OFF** (prevent branch deletion)

---

### 5. Configure Issue Labels

**Location**: Issues → Labels

**Add custom labels** (in addition to defaults):

| Label | Color | Description |
|-------|-------|-------------|
| `needs-triage` | `#d73a4a` | Needs initial review and categorization |
| `good first issue` | `#7057ff` | Good for newcomers |
| `help wanted` | `#008672` | Extra attention is needed |
| `priority: critical` | `#b60205` | Critical priority |
| `priority: high` | `#d93f0b` | High priority |
| `priority: medium` | `#fbca04` | Medium priority |
| `priority: low` | `#0e8a16` | Low priority |
| `type: bug` | `#d73a4a` | Something isn't working |
| `type: feature` | `#a2eeef` | New feature or request |
| `type: docs` | `#0075ca` | Documentation improvements |
| `type: refactor` | `#d4c5f9` | Code refactoring |
| `type: test` | `#bfd4f2` | Testing improvements |
| `area: web-ui` | `#c2e0c6` | Web UI/Blazor components |
| `area: api` | `#c2e0c6` | REST API endpoints |
| `area: cli` | `#c2e0c6` | Command-line interface |
| `area: mcp` | `#c2e0c6` | MCP server |
| `area: ingestion` | `#c2e0c6` | Document ingestion pipeline |
| `area: search` | `#c2e0c6` | Search functionality |
| `area: database` | `#c2e0c6` | Database/PostgreSQL |
| `area: storage` | `#c2e0c6` | MinIO/S3 storage |
| `security` | `#ee0701` | Security-related |
| `breaking-change` | `#b60205` | Introduces breaking changes |
| `dependencies` | `#0366d6` | Dependency updates |

---

### 6. Repository Settings (General)

**Location**: Settings → General

#### Features
- ✅ **Wikis** - Optional (if you want a wiki)
- ✅ **Issues** - Required (enabled by default)
- ✅ **Sponsorships** - Optional (GitHub Sponsors)
- ✅ **Projects** - Optional (project boards)
- ✅ **Discussions** - Enabled in step 2

#### Pull Requests
- ✅ **Allow merge commits** - Optional
- ✅ **Allow squash merging** - Recommended (cleaner history)
- ✅ **Allow rebase merging** - Optional
- ✅ **Always suggest updating pull request branches**
- ✅ **Automatically delete head branches** (cleanup merged branches)

#### Archives
- ❌ **Do not archive this repository** (keep it active)

---

### 7. Update Badge URLs in README.md

**After pushing to GitHub**, update the badge URLs in [README.md](../README.md):

Replace `yourusername` with your actual GitHub username/organization:
```markdown
[![Build](https://img.shields.io/github/actions/workflow/status/yourusername/Connapse/ci.yml?branch=main&label=build)](https://github.com/yourusername/Connapse/actions)
[![Tests](https://img.shields.io/badge/tests-171%20passing-success)](https://github.com/yourusername/Connapse/actions)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)
[![GitHub Issues](https://img.shields.io/github/issues/yourusername/Connapse)](https://github.com/yourusername/Connapse/issues)
[![GitHub Stars](https://img.shields.io/github/stars/yourusername/Connapse?style=social)](https://github.com/yourusername/Connapse/stargazers)
```

Also update URLs in:
- [.github/ISSUE_TEMPLATE/config.yml](../.github/ISSUE_TEMPLATE/config.yml)
- Any CI/CD workflows that reference the repository

---

## 🔄 CI/CD Workflow (Optional but Recommended)

Create `.github/workflows/ci.yml` to enable the build badge:

```yaml
name: CI

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main, develop ]

jobs:
  build:
    runs-on: ubuntu-latest

    steps:
    - uses: actions/checkout@v4

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '10.0.x'

    - name: Restore dependencies
      run: dotnet restore

    - name: Build
      run: dotnet build --no-restore --configuration Release

    - name: Test
      run: dotnet test --no-build --configuration Release --verbosity normal
```

This will:
- ✅ Run builds on every push to main/develop
- ✅ Run builds on every pull request
- ✅ Execute all tests
- ✅ Update the build badge status

---

## ✅ Verification Checklist

After completing the manual configuration:

- [ ] Repository description is set
- [ ] Topics/tags are added (for discoverability)
- [ ] GitHub Discussions is enabled
- [ ] Security tab is enabled with alerts
- [ ] Branch protection rules are configured for `main`
- [ ] Custom issue labels are created
- [ ] Pull request settings are configured
- [ ] Badge URLs in README.md are updated with correct username
- [ ] (Optional) CI/CD workflow is created and running
- [ ] Test all issue templates by creating test issues
- [ ] Test PR template by creating a test PR

---

## 📝 Notes

### About "yourusername" Placeholders

The following files contain `yourusername` placeholders that need to be replaced with your actual GitHub username or organization name:

1. **README.md** - Badge URLs, repository links
2. **.github/ISSUE_TEMPLATE/config.yml** - Contact links
3. **docs/GITHUB_SETUP.md** (this file) - Example URLs

You can use find-and-replace to update all occurrences:

```bash
# Linux/macOS
find . -type f -name "*.md" -o -name "*.yml" | xargs sed -i 's/yourusername/ACTUAL_USERNAME/g'

# Windows (PowerShell)
Get-ChildItem -Recurse -Include *.md,*.yml | ForEach-Object {
    (Get-Content $_.FullName) -replace 'yourusername', 'ACTUAL_USERNAME' | Set-Content $_.FullName
}
```

### Why These Settings Matter

- **Topics**: Help users discover your project through GitHub search
- **Discussions**: Better for Q&A than issues, reduces issue clutter
- **Security Tab**: Critical for responsible disclosure of vulnerabilities
- **Branch Protection**: Prevents accidental direct commits to main
- **Labels**: Helps organize and prioritize issues effectively
- **CI/CD**: Automated quality checks, builds trust with contributors

---

*Last Updated: 2026-02-08*
*Part of Public Release Preparation - Phase 2*
