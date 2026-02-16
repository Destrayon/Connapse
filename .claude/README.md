# .claude/ Directory

This directory contains development documentation and configuration for the Connapse project. It was created during development using [Claude Code](https://www.anthropic.com/claude/code), an AI-powered development assistant.

## What's Here

### `/state/` - Architecture Documentation
**Public and beneficial** - helps contributors understand the project:

- **[decisions.md](state/decisions.md)** - Architectural decisions with context and rationale
- **[conventions.md](state/conventions.md)** - Code patterns and style choices
- **[progress.md](state/progress.md)** - Feature development status and session history
- **[issues.md](state/issues.md)** - Known bugs, tech debt, and workarounds
- **[api-surface.md](state/api-surface.md)** - Public interfaces and breaking change log

### `/commands/`
Project-specific command documentation:
- **[init.md](commands/init.md)** - Project initialization guide

### Configuration Files
- **`settings.json`** - Project-wide Claude Code permissions (tracked in git)
- **`settings.local.json`** - User-specific permissions (gitignored, not tracked)

## Why This Exists

Modern development increasingly involves AI assistance. We've chosen to keep the architecture documentation **public and transparent** because:

1. **Better Documentation**: Decisions are recorded with full context and rationale
2. **Contributor Onboarding**: New contributors can understand *why* things were built this way
3. **Transparency**: Shows the thought process behind technical choices
4. **Living Document**: Updated as the project evolves

## AI-Assisted Development

This project was built with assistance from Claude (Anthropic), using:
- Claude Code CLI for codebase-aware assistance
- Systematic architectural decision tracking
- Test-driven development with AI-generated tests
- Documentation-first approach

**Result**: 171 passing tests, 0 errors, comprehensive documentation, clean architecture.

## For Contributors

If you're contributing to this project:
- Read [decisions.md](state/decisions.md) before major architectural changes
- Follow patterns in [conventions.md](state/conventions.md)
- Check [issues.md](state/issues.md) for known limitations
- Update [progress.md](state/progress.md) after completing features

## For Other Projects

Feel free to adopt this documentation structure! The `/state/` directory pattern provides:
- Searchable decision history
- Pattern documentation
- Issue tracking outside of GitHub Issues (for internal use)
- Progress tracking across sessions

---

**Note**: This directory does NOT contain any secrets, credentials, or sensitive information. All sensitive data is properly excluded via `.gitignore`.
