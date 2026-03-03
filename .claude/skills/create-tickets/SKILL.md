---
name: create-tickets
description: 'Batch-create GitHub issues from a brainstorming discussion. Decomposes ideas into properly-sized tickets with labels, milestones, and project board placement. Trigger when user says: create tickets, make tickets, turn ideas into issues, create issues from brainstorm, create issues from discussion, batch create issues.'
allowed-tools: Bash, Read, Grep, Glob, AskUserQuestion, TodoWrite, Agent
---

# Create Tickets

Turn a brainstorming conversation into well-structured, properly-sized GitHub issues.

## Overview

After a discussion about features, improvements, or bugs, this skill:
1. Extracts every idea from the conversation
2. Decomposes large features into PR-sized sub-issues
3. Presents a structured plan for user review
4. Batch-creates all approved issues with proper metadata
5. Adds them to the project board

## Step 1: Extract Ideas from Conversation

Read the full conversation history. For each distinct idea, feature, bug, or task mentioned, capture:
- What it is (feature, bug fix, enhancement, refactor, docs, test, infrastructure)
- Why it matters (the user's stated motivation)
- Rough scope (how many files/areas it touches)

Group related ideas together. If the user discussed a large feature with multiple parts, keep those parts linked.

## Step 2: Size and Decompose

Apply these sizing rules strictly:

| Size | Lines Changed | Files | Target |
|------|--------------|-------|--------|
| XS | < 50 | 1-2 | Ideal for config, typos, small fixes |
| S | 50-150 | 2-4 | Target size for most issues |
| M | 150-300 | 3-6 | Acceptable for focused features |
| L/XL | 300+ | 6+ | **MUST be decomposed — never create as single issue** |

For any idea estimated at L or larger, decompose by layer:
1. **Data layer**: models, migrations, interfaces (S)
2. **Service layer**: business logic implementing interfaces (S-M)
3. **Endpoint layer**: API endpoints wired to service (S)
4. **UI layer**: Blazor components consuming endpoints (S-M)
5. **Test layer**: unit + integration tests (S-M)

Not every layer applies to every feature. Only create sub-issues for layers that are relevant.

## Step 3: Structure Each Issue

For each issue, determine all metadata:

### Title
- Concise, actionable, imperative voice
- Good: "Add multi-prefix OR-clause to cloud scope search"
- Bad: "Cloud scope search improvements"

### Labels (always include `needs-triage` plus one type and one or more area labels)

**Type labels** (exactly one):
`type: feature`, `type: bug`, `type: enhancement`, `type: refactor`, `type: docs`, `type: test`, `type: infrastructure`

**Area labels** (one or more):
`area: core`, `area: web-ui`, `area: api`, `area: cli`, `area: mcp`, `area: ingestion`, `area: search`, `area: database`, `area: storage`, `area: identity`, `area: agents`

### Priority
- `P0-Critical`: Blocking users or breaking core functionality
- `P1-High`: Important for next release, significant user impact
- `P2-Medium`: Planned improvement, moderate impact
- `P3-Low`: Nice to have, minor improvement

### Milestone
Assign based on scope and dependencies:
- `v0.3.1`: Polish, tech debt, CI improvements
- `v0.4.0`: Cross-container search, live events, local connector ACLs
- `v0.5.0`: New connectors (GitHub, Notion, Slack) and embedding providers
- No milestone: exploratory or unscheduled work

### Issue Body Format

Use this exact template for every issue:

```markdown
## Description
[1-3 sentences: what this does and why it matters]

## Acceptance Criteria
- [ ] [Specific, testable criterion]
- [ ] [Another criterion]
- [ ] Tests pass (`dotnet test`)

## Implementation Notes
- Key file: `src/Connapse.[Project]/[relevant path]`
- Interface: `[relevant interface if applicable]`
- Pattern: [follow existing pattern in X if applicable]

## Size Estimate
[XS/S/M] — [one-line justification, e.g. "~80 lines across 2 files"]

## Related
[Parent: #N | Part of: #N | Depends on: #N | none]
```

## Step 4: Present for Review

Before creating anything, present the full issue plan in a clear table format:

```
## Proposed Issues

### [Milestone or Group Name]

| # | Title | Type | Area | Size | Priority | Parent |
|---|-------|------|------|------|----------|--------|
| 1 | Title here | feature | storage | S | P2 | — |
| 2 | Title here | feature | storage | S | P2 | #1 |
```

Then show the full body for each issue so the user can review descriptions and acceptance criteria.

Ask the user to confirm, modify, or remove issues before proceeding. Use AskUserQuestion:
- "Create all N issues as shown?"
- Options: "Create all", "Let me make changes first", "Skip for now"

If the user wants changes, apply them and present again.

## Step 5: Create Issues

After user approval, create issues in dependency order (parents before children).

For each issue, run:

```bash
gh issue create \
  --repo Destrayon/Connapse \
  --title "TITLE" \
  --body "$(cat <<'ISSUE_EOF'
BODY_CONTENT
ISSUE_EOF
)" \
  --label "needs-triage" \
  --label "type: feature" \
  --label "area: storage" \
  --milestone "v0.4.0"
```

Important `gh issue create` rules:
- Use HEREDOC for the body to preserve formatting
- Each `--label` flag takes one label (repeat the flag for multiple labels)
- Use `--milestone` only when a milestone was assigned
- Capture the issue URL from the output for project board and sub-issue linking

After creating each issue:

```bash
gh project item-add 3 --owner Destrayon --url ISSUE_URL
```

### Sub-issue Linking

After both parent and child issues exist, link them using the GitHub API.
First get the parent issue's node ID:

```bash
PARENT_NODE_ID=$(gh api graphql -f query='
  query {
    repository(owner: "Destrayon", name: "Connapse") {
      issue(number: PARENT_NUMBER) { id }
    }
  }' -q '.data.repository.issue.id')
```

Then get the child issue's node ID and add it as a sub-issue:

```bash
CHILD_NODE_ID=$(gh api graphql -f query='
  query {
    repository(owner: "Destrayon", name: "Connapse") {
      issue(number: CHILD_NUMBER) { id }
    }
  }' -q '.data.repository.issue.id')

gh api graphql -f query='
  mutation {
    addSubIssue(input: {
      issueId: "'"$PARENT_NODE_ID"'"
      subIssueId: "'"$CHILD_NODE_ID"'"
    }) {
      issue { id }
      subIssue { id }
    }
  }'
```

## Step 6: Summary

After all issues are created, present a summary:

```
## Created Issues

| Issue | Title | Labels | Milestone |
|-------|-------|--------|-----------|
| #18 | Add multi-prefix OR-clause | type: enhancement, area: search | v0.3.1 |
| #19 | ... | ... | ... |

All issues added to project board: https://github.com/users/Destrayon/projects/3
```

## Important Rules

1. **NEVER create issues without user approval** — always present the plan first
2. **NEVER create L/XL issues** — always decompose into S/M sub-issues
3. **Every issue must have acceptance criteria** — vague issues lead to vague PRs
4. **One concern per issue** — don't mix refactoring with features
5. **Use TodoWrite** to track progress through the batch creation process
6. **If `gh` commands fail**, report the error and ask if the user wants to retry or skip
7. **Reference CLAUDE.md** for architecture context when writing implementation notes
