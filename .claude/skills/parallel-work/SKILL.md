---
name: parallel-work
description: 'Run multiple Claude Code agents on separate GitHub issues simultaneously, each in its own worktree. Picks non-conflicting tasks, spawns autonomous agents that implement, commit, and open PRs. Trigger when user says: parallel work, work on multiple tasks, run agents in parallel, multi-agent, spawn agents, parallel tasks, work on N things at once.'
---

# Parallel Work

Run N autonomous agents in parallel, each working on a separate GitHub issue in its own isolated worktree. Each agent creates a branch, implements the task, commits, and opens a PR.

**Default**: 3 agents. Override with argument: `/parallel-work 2` or `/parallel-work 5`.

Parse the agent count from `$ARGUMENTS` if provided (extract the number). Default to 3 if not specified or not a number between 1-5.

## Step 0: Pre-Flight Check

Before anything else, ensure a clean starting point:

1. **Check current branch**: If not on `main`, warn the user. Worktrees branch from HEAD — spawning from a feature branch means agents won't have the latest merged code.
2. **Check for uncommitted changes**: If the working tree is dirty, offer to stash (`git stash -u -m "pre-parallel-work"`) before switching branches. Always restore the stash after switching.
3. **Switch to main and pull**: `git checkout main && git pull` to ensure agents start from the latest code.
4. **Restore stash if needed**: `git stash pop` to bring back any uncommitted work.

Only proceed to Step 1 after confirming the user is on an up-to-date `main` with their changes preserved.

## Step 1: Gather Context

Run all of these in parallel:

### Open issues
```bash
gh issue list --repo Destrayon/Connapse --state open --json number,title,labels,milestone,assignees,createdAt,updatedAt --limit 100
```

### Recent merged PRs (for momentum scoring)
```bash
gh pr list --repo Destrayon/Connapse --state merged --json number,title,mergedAt,labels --limit 5
```

### Current git state
```bash
git status --short
git branch --show-current
```

### Project board state (filter to Todo only)
```bash
gh project item-list 3 --owner Destrayon --format json
```
Filter the results to items with `status == "Todo"` only. Exclude items with status "Done" or "In Progress" — those are already being worked on or completed.

### Architecture context
Read `.claude/state/decisions.md` and `.claude/state/issues.md` for blockers or dependencies.

## Step 2: Score and Select

Score each open issue using these criteria (same as `/next-task`):

### Priority Weight
| Priority | Score |
|----------|-------|
| P0-Critical | 100 |
| P1-High | 70 |
| P2-Medium | 40 |
| P3-Low | 10 |
| No priority | 20 |

### Milestone Urgency
| Milestone | Score |
|-----------|-------|
| Current (nearest due date or lowest version) | +30 |
| Next | +15 |
| Future | +5 |
| No milestone | +0 |

### Dependency Readiness
| Status | Score |
|--------|-------|
| Unblocks other issues | +25 |
| No dependencies, ready to start | +15 |
| Dependencies met (referenced issues closed) | +10 |
| Has open dependencies | -30 |
| Labeled `blocked` | -50 |

### Momentum Bonus
| Condition | Score |
|-----------|-------|
| Same area label as a recently merged PR | +10 |
| Builds directly on a recently merged PR | +15 |

### Size Preference
| Size | Score |
|------|-------|
| XS | +15 |
| S | +10 |
| M | +5 |
| L (should be decomposed) | -10 |
| No size | +0 |

### Tiebreaking

When issues have equal scores, break ties in this order:
1. **Type priority**: bug > enhancement > refactor > test > docs > infrastructure > feature (bugs are more impactful to fix)
2. **Issue number**: lower number first (older issues should be addressed sooner)

### Conflict Detection

After scoring and tiebreaking, select the top N tasks that do NOT share `area:` labels. The goal is to avoid merge conflicts by ensuring each agent works on a different part of the codebase.

Algorithm:
1. Sort all eligible issues by total score (descending), then by tiebreaker rules
2. Initialize an empty `selected` list and a `taken_areas` set
3. Walk through sorted issues:
   - Extract this issue's `area:` labels
   - If ANY area overlaps with `taken_areas`, skip this issue
   - Otherwise, add to `selected` and add its areas to `taken_areas`
   - Issues with NO `area:` label never conflict (they can always be selected)
4. Stop when `selected` has N items or no more issues remain

If fewer than N non-conflicting tasks exist, tell the user and proceed with however many were found. If the ONLY way to get N tasks is to allow area overlap, present the conflict and ask the user whether to proceed.

## Step 3: Present Selection

Show the selected tasks and ask for confirmation:

```
## Parallel Work Plan

I've selected {N} non-conflicting tasks to work on simultaneously:

### Agent 1: [Issue Title] (#number)
**Score**: [total] | **Priority**: [P-level] | **Milestone**: [version] | **Size**: [size]
**Area**: [area labels] | **Branch**: [branch name]
**Summary**: [1 sentence from issue body]

### Agent 2: [Issue Title] (#number)
...

### Agent 3: [Issue Title] (#number)
...

**Conflict check**: No overlapping area labels — these tasks touch different parts of the codebase.

Ready to spawn {N} agents? Each will create a branch, implement the task, and open a PR.
```

Wait for user confirmation before proceeding. If the user wants to swap any task, re-select.

## Step 4: Spawn Agents

Once confirmed, spawn all N agents in a SINGLE message using the Agent tool. Each agent MUST use `isolation: "worktree"` and `model: "sonnet"`.

For each selected task, spawn an Agent with this prompt template (fill in the placeholders):

```
You are working on GitHub issue #{ISSUE_NUMBER} for the Connapse project.

## Your Task
{ISSUE_TITLE}

## Issue Details
{FULL_ISSUE_BODY from gh issue view}

## Setup Steps

1. Create and switch to branch:
   ```bash
   git checkout -b {BRANCH_TYPE}/{ISSUE_NUMBER}-{SHORT_DESCRIPTION}
   ```

2. Move the issue to "In Progress" on the project board:
   ```bash
   ITEM_ID=$(gh project item-list 3 --owner Destrayon --format json | jq -r '.items[] | select(.content.number == {ISSUE_NUMBER} and .content.repository == "Destrayon/Connapse") | .id')
   gh project item-edit --project-id PVT_kwHOAldLE84BQszG --id "$ITEM_ID" --field-id PVTSSF_lAHOAldLE84BQszGzg-vn-U --single-select-option-id 47fc9ee4
   ```

## Implementation Guidelines

- This is a .NET 9 project using Blazor Server, EF Core, and PostgreSQL
- Follow existing patterns in the codebase — read similar files before writing new code
- Run `dotnet build` after making changes to verify compilation
- Run `dotnet test` if you modified testable logic
- Keep changes focused on THIS issue only — do not fix unrelated things
- PR should be under 300 lines of changes

## Project Structure
```
src/
├── Connapse.Web/          # Blazor WebApp (UI + API endpoints)
├── Connapse.Core/         # Domain models, interfaces, shared logic
├── Connapse.Identity/     # Auth: Identity, PAT, JWT, RBAC
├── Connapse.Ingestion/    # Document parsing, chunking, embedding
├── Connapse.Search/       # Vector search, hybrid search, reranking
├── Connapse.Storage/      # Vector DB, document store, connectors
└── Connapse.CLI/          # Command-line interface
```

## When Done

1. Stage and commit your changes:
   ```bash
   git add [specific files]
   git commit -m "$(cat <<'EOF'
   {COMMIT_TYPE}: {SHORT_DESCRIPTION}

   Closes #{ISSUE_NUMBER}

   Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
   EOF
   )"
   ```

2. Push and create a PR:
   ```bash
   git push -u origin {BRANCH_TYPE}/{ISSUE_NUMBER}-{SHORT_DESCRIPTION}
   gh pr create --title "{PR_TITLE}" --body "$(cat <<'EOF'
   ## Summary
   - {BULLET_POINTS}

   ## Test plan
   - [ ] `dotnet build` passes
   - [ ] `dotnet test` passes
   - [ ] {SPECIFIC_TEST_STEPS}

   Closes #{ISSUE_NUMBER}

   🤖 Generated with [Claude Code](https://claude.com/claude-code)
   EOF
   )"
   ```

3. Report back with the PR URL.
```

IMPORTANT: You MUST read the full issue body with `gh issue view {NUMBER}` BEFORE spawning agents, so you can fill in the prompt template completely. Spawn all agents in a single message for true parallelism.

## Step 5: Report Results

As agents complete, collect their results and present a summary:

```
## Parallel Work Complete

| # | Issue | Branch | PR | Status |
|---|-------|--------|----|--------|
| 1 | #{N} {Title} | `fix/N-desc` | [PR #{X}](url) | Done |
| 2 | #{N} {Title} | `feature/N-desc` | [PR #{X}](url) | Done |
| 3 | #{N} {Title} | `refactor/N-desc` | [PR #{X}](url) | Done |

All PRs are ready for review.
```

If any agent fails, report what went wrong and suggest next steps (e.g., manual intervention, re-running just that task).

## Edge Cases

- **Fewer than N eligible tasks**: Tell the user how many were found and proceed with that number. Suggest `/create-tickets` or `/discover-work` if the board is thin.
- **All tasks share areas**: Present the conflict and ask the user which combination they prefer, accepting the overlap risk.
- **Dirty working tree**: Warn the user and suggest committing or stashing before spawning agents. Worktree isolation means the main tree state shouldn't matter, but uncommitted changes won't be in the worktrees.
- **Already on a feature branch**: Note this — worktrees branch from HEAD, so make sure main is up to date. Suggest `git checkout main && git pull` first.
- **Agent failure**: If an agent errors out, the worktree persists with partial work. Report what happened and suggest manual completion or re-running `/parallel-work 1` for just that task.
- **Rate limits**: 3 parallel Sonnet agents is manageable. If running 4-5, warn about potential rate limit pressure.
- **Worktree cleanup**: After all PRs are merged, remind the user they can clean up leftover worktrees with `rm -rf .claude/worktrees/agent-*`. Worktrees with no changes are auto-cleaned, but ones with commits persist.
