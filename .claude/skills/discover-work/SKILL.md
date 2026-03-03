---
name: discover-work
description: 'Deep research across codebase, GitHub issues, discussions, project board, and architecture docs to discover new tasks, gaps, technical debt, and improvement ideas. Trigger when user says: discover work, find tasks, what needs doing, audit codebase, find gaps, technical debt audit, backlog discovery, brainstorm tasks, what is missing, research tasks, code audit.'
---

# Discover Work

Perform thorough research across all project surfaces to discover new tasks, gaps, technical debt, and improvement ideas that haven't been captured yet.

## Overview

This skill systematically investigates six research dimensions, synthesizes findings, and presents a prioritized list of discovered work items. It goes beyond what `/next-task` does (which picks from *existing* issues) — this skill *finds new work* that nobody has filed yet.

## Research Dimensions

Work through each dimension. Use the Agent tool to parallelize independent research. Present findings grouped by dimension, then synthesize into a final prioritized list.

### 1. Codebase Markers

Search the entire `src/` tree for signals of incomplete or problematic code:

```
Patterns to search (case-insensitive):
  TODO, FIXME, HACK, WORKAROUND, TEMPORARY, DEFER, XXX, BUG, PERF,
  NotImplementedException, throw new NotSupportedException,
  "// removed", "// disabled", "// old", "// legacy"
```

For each hit, read surrounding context (5-10 lines) to understand whether it's:
- A genuine gap that needs a ticket
- A known deferral already tracked in `.claude/state/issues.md`
- A false positive (e.g., test code, intentional placeholder)

Skip false positives. Flag genuine gaps.

### 2. GitHub Issues & Discussions

Use `gh` CLI to pull:

```bash
# Open issues with details
gh issue list --state open --limit 100 --json number,title,labels,milestone,body,assignees,createdAt,updatedAt

# Closed issues (look for reopened patterns, incomplete fixes)
gh issue list --state closed --limit 30 --json number,title,labels,closedAt,body

# Discussions
gh api repos/{owner}/{repo}/discussions --jq '.[] | {number, title, category: .category.name, body, comments: .comments, created_at}'

# PRs (open and recently merged — look for follow-up items)
gh pr list --state merged --limit 20 --json number,title,body,mergedAt,labels
gh pr list --state open --limit 10 --json number,title,body,labels
```

Look for:
- Issues that are stale (no updates in 2+ weeks, no assignee, `needs-triage` label)
- Issues missing labels, milestones, or size estimates
- Closed issues whose fixes were partial (check for "deferred", "follow-up", "TODO" in close comments)
- Discussions with unresolved questions or design decisions
- Merged PRs that mention "follow-up" or "deferred" work
- Open PRs that have been sitting without review

### 3. Project Board

Use `gh project` to analyze board health:

```bash
# Get all board items with status
gh project item-list {PROJECT_NUMBER} --owner {OWNER} --format json --limit 100
```

Look for:
- Items stuck in "In Progress" for too long
- Items in "Todo" that have unresolved blockers
- Gaps: issues that exist but aren't on the board
- Orphaned board items (no linked issue)
- Missing columns or workflow gaps

### 4. Architecture & State Files

Read these files and cross-reference against the actual codebase:

- `.claude/state/decisions.md` — Are all decisions implemented? Any that need revisiting?
- `.claude/state/conventions.md` — Are conventions being followed? Any new patterns that should be documented?
- `.claude/state/issues.md` — Are "Open" issues still relevant? Any "Fixed" issues that regressed?
- `docs/v0.3.0-plan.md` — What was planned but not yet built?
- `docs/connectors.md` — Does documentation match implementation?

Look for:
- Decisions that were made but never implemented
- Conventions that code violates
- Plan items that were skipped or deferred
- Documentation that's out of date
- Missing documentation for new features

### 5. Test Coverage Gaps

Analyze test structure against source structure:

```bash
# List all source files
find src/ -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*"

# List all test files
find tests/ -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*"
```

Look for:
- Source files with no corresponding test file
- Features added recently (check git log) that lack tests
- Integration test gaps (e.g., new endpoints without API tests)
- Test files that test obsolete functionality

### 6. Cross-Cutting Concerns

Look for systemic issues that span multiple files:

- **Security**: Endpoints without auth, unsanitized inputs, missing CORS, exposed secrets
- **Error handling**: Swallowed exceptions, missing try-catch in critical paths, inconsistent error responses
- **Performance**: N+1 queries, missing pagination, unbounded collections, missing caching
- **Observability**: Missing logging in critical paths, no structured logging, no metrics
- **UX**: Dead links in UI, missing loading states, error states without user feedback
- **Dependencies**: Outdated NuGet packages, deprecated API usage, security advisories

## Synthesis & Output

After completing all research dimensions, synthesize findings into a single prioritized report.

### Report Structure

Present the report in this exact format:

```markdown
# Work Discovery Report

**Date**: {today}
**Scope**: Full codebase + GitHub + project board

## Executive Summary
{2-3 sentences: how many items found, most critical category, overall health}

## Critical Findings
{Items that should be addressed immediately — security, data loss, broken functionality}

## New Feature Ideas
{Gaps in functionality, user-facing improvements, things competitors have}
Each item: **Title** — Description. Suggested milestone. Estimated size.

## Technical Debt
{Code quality, missing tests, outdated patterns, deferred work}
Each item: **Title** — Description. Impact if ignored. Estimated size.

## Documentation Gaps
{Missing docs, outdated docs, undocumented features}

## Process Improvements
{Board hygiene, label gaps, workflow issues}

## Already Tracked (Validation)
{Items found during research that are already captured in existing issues — confirms coverage}
```

### Prioritization Criteria

Rank items by:
1. **Impact**: How many users/features does this affect?
2. **Risk**: What happens if we ignore it?
3. **Effort**: How much work is it? (prefer quick wins)
4. **Dependencies**: Does this unblock other work?

### De-duplication

Before presenting any finding, check it against:
- Open GitHub issues (by keyword search)
- `.claude/state/issues.md` open items
- Items already on the project board

If already tracked, move it to the "Already Tracked" section with a reference to the existing issue/item.

## After the Report

Ask the user if they'd like to:
1. **Create tickets** — Use `/create-tickets` to batch-create issues from the findings
2. **Deep-dive** — Investigate a specific finding in more detail
3. **Triage** — Walk through findings one by one for keep/skip/modify decisions

## Important Notes

- Use the Agent tool to parallelize independent research dimensions (e.g., codebase markers + GitHub issues + project board can all run in parallel)
- Read the `MEMORY.md` file first to understand project context and avoid re-discovering known items
- Be specific: "The `FooService` is missing error handling on line 42" is better than "Some services need error handling"
- Include file paths and line numbers for all code findings
- Don't suggest work that contradicts decisions in `decisions.md`
- Respect the project's conventions from `conventions.md`
- When checking GitHub, use the repo owner/name from `gh repo view`
