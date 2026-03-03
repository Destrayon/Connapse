---
name: next-task
description: 'Recommend the best next task to work on based on open GitHub issues. Analyzes priority, dependencies, milestone urgency, and codebase readiness. Trigger when user asks: what should I work on, what is next, next task, pick a task, what to do next, suggest work, prioritize tasks.'
---

# Next Task

Analyze open GitHub issues and recommend the best task to start working on right now.

## Step 1: Gather Context

Run these in parallel to understand the current state:

### Open issues and board state
```bash
gh issue list --repo Destrayon/Connapse --state open --json number,title,labels,milestone,assignees,createdAt,updatedAt --limit 100
```

### Current branch and working state
```bash
git status --short
git branch --show-current
```

### Recent activity (what was just worked on)
```bash
gh pr list --repo Destrayon/Connapse --state merged --json number,title,mergedAt,labels --limit 5
```

### Check decisions.md and issues.md for blockers or context
Read `.claude/state/decisions.md` and `.claude/state/issues.md` for any known blockers, dependencies, or recent decisions that affect prioritization.

## Step 2: Score and Rank

Evaluate each open issue against these criteria, in order of importance:

### Priority Weight (highest impact)
| Priority | Score |
|----------|-------|
| P0-Critical | 100 |
| P1-High | 70 |
| P2-Medium | 40 |
| P3-Low | 10 |
| No priority set | 20 |

### Milestone Urgency
Issues in the nearest milestone score higher. The reasoning: shipping a complete milestone is more valuable than scattering work across multiple milestones.

| Milestone | Score |
|-----------|-------|
| Current (nearest due date or lowest version) | +30 |
| Next | +15 |
| Future | +5 |
| No milestone | +0 |

### Dependency Readiness
An issue that has no blockers and no unmerged dependencies should score higher than one waiting on other work. Check:
- Does the issue body reference other issues it depends on? Are those closed?
- Is the issue marked `blocked`?
- Would this issue unblock other issues? (check if other issues reference this one)

| Status | Score |
|--------|-------|
| Unblocks other issues | +25 |
| No dependencies, ready to start | +15 |
| Dependencies met (referenced issues are closed) | +10 |
| Has open dependencies | -30 |
| Labeled `blocked` | -50 |

### Momentum Bonus
If recent merged PRs touched the same area, there's context advantage — the codebase patterns are fresh.

| Condition | Score |
|-----------|-------|
| Same area label as a recently merged PR | +10 |
| Builds directly on a recently merged PR | +15 |

### Size Preference
Smaller issues are easier to start and finish in a single session. Bias toward completing something rather than starting something large.

| Size | Score |
|------|-------|
| XS | +15 |
| S | +10 |
| M | +5 |
| L (should be decomposed) | -10 |
| No size | +0 |

## Step 3: Present Recommendation

Present the top 3 ranked issues in this format:

```
## Recommended Next Task

### #1: [Issue Title] (#number)
**Score**: [total] | **Priority**: [P-level] | **Milestone**: [version] | **Size**: [size]
**Why this one**: [1-2 sentences explaining why this is the best pick right now —
reference momentum, unblocking, milestone progress, etc.]
**Quick start**: [Which files to look at first, what interface to implement, etc.]

---

### #2: [Issue Title] (#number)
...

### #3: [Issue Title] (#number)
...
```

After presenting, ask the user which one they'd like to start — or if they want to see more options.

## Step 4: Set Up for Work

Once the user picks a task:

1. Create the branch: `git checkout -b <type>/<issue-number>-<short-description>`
2. Move the issue to "In Progress" on the project board:
   ```bash
   # Get the project item ID for this issue
   ITEM_ID=$(gh project item-list 3 --owner Destrayon --format json | jq -r '.items[] | select(.content.number == <ISSUE_NUMBER> and .content.repository == "Destrayon/Connapse") | .id')
   # Move to In Progress
   gh project item-edit --project-id PVT_kwHOAldLE84BQszG --id "$ITEM_ID" --field-id PVTSSF_lAHOAldLE84BQszGzg-vn-U --single-select-option-id 47fc9ee4
   ```
3. Read the issue details: `gh issue view <number>`
4. Read the relevant source files mentioned in the issue's implementation notes
5. Summarize what needs to be done and confirm the approach before writing code

## Edge Cases

- **No open issues**: Tell the user the board is clear and suggest running `/create-tickets` if they have ideas to discuss.
- **All issues are blocked**: Flag this explicitly and suggest investigating what's blocking progress.
- **Working tree is dirty**: Warn before creating a new branch — suggest committing or stashing first.
- **Already on a feature branch**: Note that there may be in-progress work. Ask if they want to continue that or switch to something new.
