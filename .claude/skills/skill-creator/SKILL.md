---
name: skill-creator
description: 'Create, test, iterate, and package Claude Code skills (SKILL.md-based). Use this skill when the user wants to create a new Claude Code skill from scratch, improve or debug an existing skill, run test cases against skills, benchmark skill quality with evals, optimize a skill description for better auto-triggering, or package skills for distribution. Trigger on phrases like "turn this into a skill", "make a skill for", "create a Claude Code skill", "help me write a custom command", "improve my skill", references to .claude/skills/ or SKILL.md files, or any workflow involving skill authoring, testing, or iteration.'
---

# Claude Code Skill Creator

A skill for creating, testing, and iteratively improving Claude Code skills.

## What are Claude Code Skills?

Claude Code skills are directories containing a `SKILL.md` file and optional supporting resources. They extend Claude Code's capabilities through organized instructions, scripts, and templates. Skills are **auto-invoked** by Claude when relevant (model-invoked) and can also be manually triggered via `/skill-name`.

### Skill locations

- **Personal skills**: `~/.claude/skills/skill-name/SKILL.md` — available across all projects
- **Project skills**: `.claude/skills/skill-name/SKILL.md` — shared via git with the team
- **Plugin skills**: bundled with Claude Code plugins

### SKILL.md format

```markdown
---
name: my-skill-name
description: 'Single-line description of what the skill does and when to use it. Max 1024 chars.'
allowed-tools: Read, Grep, Glob  # Optional: restrict tool access
---

# My Skill Name

Instructions for what Claude should do when this skill is invoked.
```

Key facts:
- `name`: kebab-case, max 64 chars, lowercase letters/numbers/hyphens only. Must match directory name.
- `description`: **Must be a single-line string** — Claude Code's skill indexer does not parse YAML multiline indicators (`>-`, `|`, `|-`) correctly. Quote the value when it contains colons or special YAML chars.
- `allowed-tools`: Optional comma-separated list of tools the skill can use without per-use approval
- Additional frontmatter: `disable-model-invocation: true` (only user can invoke), `user-invocable: false` (only Claude can invoke), `model:` (override model), `agent:` (specify subagent config)
- `$ARGUMENTS` placeholder is replaced with text the user types after the slash command
- Skills use progressive disclosure: only frontmatter loads initially (~100 tokens), full SKILL.md loads on activation
- Keep SKILL.md under 500 lines; split detailed content into separate reference files

### Skill directory structure

```
my-skill/
├── SKILL.md          # Core instructions (required)
├── scripts/          # Executable code for deterministic tasks
├── references/       # Docs loaded into context as needed
├── assets/           # Templates, icons, fonts
└── examples/         # Example inputs/outputs
```

---

## High-Level Workflow

The process of creating a skill goes like this:

1. Decide what you want the skill to do and roughly how it should do it
2. Write a draft of the skill
3. Create a few test prompts and run claude-with-access-to-the-skill on them
4. Help the user evaluate the results both qualitatively and quantitatively
   - While the runs happen in the background, draft some quantitative evals if there aren't any
   - Use `eval-viewer/generate_review.py` to show the user the results
   - Let them also look at the quantitative metrics
5. Rewrite the skill based on feedback from the user's evaluation
6. Repeat until you're satisfied
7. Expand the test set and try again at larger scale

Your job when using this skill is to figure out where the user is in this process and help them progress. Maybe they want a skill from scratch. Maybe they already have a draft. Maybe they want to improve an existing one. Jump in at the right point.

Be flexible — if the user says "I don't need to run a bunch of evaluations, just vibe with me", do that instead.

After the skill is done, you can also run the description optimizer to improve triggering accuracy.

---

## Communicating with the User

Pay attention to context cues to understand how to phrase your communication. There's a wide range of technical familiarity:

- "evaluation" and "benchmark" are borderline but OK
- for "JSON" and "assertion" you want to see cues from the user that they know what those are before using them without explaining

It's OK to briefly explain terms if you're in doubt.

---

## Creating a Skill

### Capture Intent

Start by understanding the user's intent. The current conversation might already contain a workflow the user wants to capture (e.g., they say "turn this into a skill"). If so, extract answers from conversation history first — the tools used, the sequence of steps, corrections made, input/output formats observed.

1. What should this skill enable Claude to do?
2. When should this skill trigger? (what user phrases/contexts)
3. What's the expected output format?
4. Should we set up test cases? Skills with objectively verifiable outputs benefit from test cases. Skills with subjective outputs (writing style, art) often don't need them.

### Interview and Research

Proactively ask about edge cases, input/output formats, example files, success criteria, and dependencies. Wait to write test prompts until this is ironed out.

### Write the SKILL.md

Based on the interview, fill in:

- **name**: Skill identifier (kebab-case, max 64 chars)
- **description**: When to trigger, what it does. This is the primary triggering mechanism. **Must be a single-line string.** Include both what the skill does AND specific contexts for when to use it. Since Claude tends to "undertrigger" skills, make descriptions a little "pushy" — enumerate key phrases and contexts.
- **allowed-tools**: Optional tool restrictions
- **The body**: Clear, step-by-step instructions

### Skill Writing Guide

#### Writing Patterns

Prefer the imperative form. Explain **why** things are important — today's LLMs respond better to understanding reasoning than to rigid MUSTs.

**Defining output formats:**
```markdown
## Report structure
ALWAYS use this exact template:
# [Title]
## Executive summary
## Key findings
## Recommendations
```

**Examples pattern:**
```markdown
## Commit message format
**Example 1:**
Input: Added user authentication with JWT tokens
Output: feat(auth): implement JWT-based authentication
```

#### Progressive Disclosure

Skills use a three-level loading system:
1. **Metadata** (name + description) — Always in context (~100 tokens)
2. **SKILL.md body** — Loaded when skill triggers (<500 lines ideal)
3. **Bundled resources** — Loaded as needed (scripts execute without loading)

Keep SKILL.md under 500 lines. For large reference files (>300 lines), include a TOC and clear pointers about when to read them.

**Domain organization:**
```
cloud-deploy/
├── SKILL.md (workflow + selection logic)
└── references/
    ├── aws.md
    ├── gcp.md
    └── azure.md
```

#### Principle of Lack of Surprise

Skills must not contain malware, exploit code, or anything that could compromise security.

### Test Cases

After drafting the skill, come up with 2-3 realistic test prompts. Share with the user for confirmation. Save to `evals/evals.json`:

```json
{
  "skill_name": "example-skill",
  "evals": [
    {
      "id": 1,
      "prompt": "User's task prompt",
      "expected_output": "Description of expected result",
      "files": []
    }
  ]
}
```

See `references/schemas.md` for the full schema.

---

## Running and Evaluating Test Cases

This section is one continuous sequence.

Put results in `<skill-name>-workspace/` as a sibling to the skill directory. Organize by iteration (`iteration-1/`, `iteration-2/`, etc.), with each test case getting its own directory.

### Step 1: Spawn all runs (with-skill AND baseline) in the same turn

For each test case, spawn two subagents at once — one with the skill, one without.

**With-skill run:**
```
Execute this task:
- Skill path: <path-to-skill>
- Task: <eval prompt>
- Save outputs to: <workspace>/iteration-<N>/eval-<ID>/with_skill/outputs/
```

**Baseline run:**
- **New skill**: no skill at all; save to `without_skill/outputs/`
- **Improving**: the old version (snapshot it first); save to `old_skill/outputs/`

### Step 2: While runs are in progress, draft assertions

Draft quantitative assertions and explain them to the user. Good assertions are objectively verifiable with descriptive names.

### Step 3: As runs complete, capture timing data

Save `total_tokens` and `duration_ms` to `timing.json` immediately when each subagent completes.

### Step 4: Grade, aggregate, and launch the viewer

1. **Grade each run** — use `agents/grader.md`. Save to `grading.json` with fields `text`, `passed`, `evidence`.
2. **Aggregate** — `python -m scripts.aggregate_benchmark <workspace>/iteration-N --skill-name <n>`
3. **Launch viewer** — `python eval-viewer/generate_review.py --workspace <workspace>/iteration-N --skill-name <name> --output /tmp/skill_review.html && open /tmp/skill_review.html`

GENERATE THE EVAL VIEWER **BEFORE** evaluating outputs yourself. Get them in front of the human ASAP.

### Step 5: Read the feedback

Read `feedback.json`. Empty feedback = the user thought it was fine. Focus improvements on specific complaints.

---

## Improving the Skill

### How to Think About Improvements

1. **Generalize from the feedback.** Don't overfit to specific examples. Try different metaphors or patterns.
2. **Keep the prompt lean.** Remove things that aren't pulling their weight. Read transcripts, not just final outputs.
3. **Explain the why.** LLMs are smart. Explain reasoning rather than relying on rigid ALWAYS/NEVER rules.
4. **Look for repeated work.** If all test cases independently write similar helpers, bundle that script in `scripts/`.

### The Iteration Loop

1. Apply improvements
2. Rerun all test cases into `iteration-<N+1>/`, including baselines
3. Launch the reviewer with `--previous-workspace`
4. Wait for user review
5. Read feedback, improve again, repeat

Keep going until the user is happy, feedback is all empty, or you're not making meaningful progress.

---

## Advanced: Blind Comparison

For rigorous comparison, read `agents/comparator.md` and `agents/analyzer.md`. This is optional and most users won't need it.

---

## Description Optimization

The `description` field determines whether Claude auto-invokes a skill. After creating or improving a skill, offer to optimize it.

### Step 1: Generate 20 trigger eval queries

Mix of should-trigger and should-not-trigger. Save as JSON:
```json
[
  {"query": "realistic user prompt", "should_trigger": true},
  {"query": "near-miss prompt", "should_trigger": false}
]
```

Queries must be realistic — concrete, specific, with detail. Focus on edge cases, not clear-cut cases.

### Step 2: Review with user

Present via the HTML template from `assets/eval_review.html`.

### Step 3: Run the optimization loop

```bash
python -m scripts.run_loop \
  --eval-set <path-to-trigger-eval.json> \
  --skill-path <path-to-skill> \
  --model <model-id-powering-this-session> \
  --max-iterations 5 \
  --verbose
```

This splits 60% train / 40% test, evaluates 3 times per query, iterates with extended thinking.

### How skill triggering works

Skills appear in Claude Code's `available_skills` list (in the Skill tool definition). Claude decides whether to invoke based on description. Claude only loads skills for tasks it can't easily handle on its own — simple queries may not trigger. Eval queries should be substantive.

### Step 4: Apply the result

Update SKILL.md frontmatter with `best_description`. Show before/after and scores.

**IMPORTANT**: The description MUST remain a single-line string. Do not use YAML multiline indicators.

---

## Claude Code-Specific Notes

### Testing with `claude -p`

Description optimization uses `claude -p` (headless mode). Key flags:
- `--output-format stream-json`: streaming JSON for skill trigger detection
- `--verbose`: extra detail
- `--include-partial-messages`: early trigger detection from stream events
- `--model <model>`: specify model

The scripts create temporary skills in `.claude/skills/`, run `claude -p`, and check whether Claude invoked the `Skill` tool targeting the skill.

### Subagent Support

Claude Code has full subagent support via the Task tool. The full parallel testing workflow works — spawn with-skill and baseline runs in parallel, grade in parallel, blind comparison works.

### Skill vs Command

Claude Code merged commands into skills. `.claude/commands/review.md` and `.claude/skills/review/SKILL.md` both create `/review`. Skills are preferred — they support directories, frontmatter for invocation control, and auto-invocation.

### Skills across surfaces

Skills work across Claude Code (terminal, VS Code, JetBrains, web), Claude.ai, and the Agent SDK. The same SKILL.md format is portable across all surfaces.

---

## Package and Present

Package the skill for distribution:

```bash
python -m scripts.package_skill <path/to/skill-folder>
```

This creates a `.skill` file (zip format) that can be distributed and installed. Users can install via Claude.ai Settings > Features or by extracting into their skills directory.

---

## Reference Files

- `agents/grader.md` — Evaluate assertions against outputs
- `agents/comparator.md` — Blind A/B comparison
- `agents/analyzer.md` — Analyze why one version beat another
- `references/schemas.md` — JSON structures for evals.json, grading.json, benchmark.json, etc.

---

## Core Loop Summary

1. Figure out what the skill is about
2. Draft or edit the skill
3. Run claude-with-access-to-the-skill on test prompts
4. Evaluate outputs with the user:
   - Create benchmark.json and run `eval-viewer/generate_review.py`
   - Run quantitative evals
5. Repeat until satisfied
6. Optimize the description for triggering
7. Package the final skill and present it
