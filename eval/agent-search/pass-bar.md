# CON-142 pass bar

Written and committed on 2026-10-28, before any scored run. The run may not change it.

## What runs
- 48 questions in `questions.json`: 24 about code and 24 about docs; within each, 12 name an exact identifier, message or value ("exact") and 12 describe behaviour in plain words ("concept").
- Three setups, same agent and base prompt, differing only in tools and tool guidance (`prompts/`):
  - **rag**: Connapse `search_knowledge` (default hybrid search, no reranker) and `get_document`.
  - **files**: `grep_files`, `find_files`, `read_file` over the original files (ripgrep).
  - **both**: all five, with instructions on when to use each.
- Agent: Claude Sonnet 5.5 (`claude-sonnet-5-5`) through Claude Code 2.1.295 `claude -p`, built-in tools off, at most 25 turns. One attempt per question per setup, one question at a time.
- Grading: a separate Sonnet 5.5 call sees the question, reference answer, required facts and the candidate answer, but not the setup. Correct means every required fact is present and nothing contradicts the reference.
- Cost: `total_cost_usd` that Claude Code reports per question (agent only, not the judge).

## The bar
"Best single setup" is whichever of rag and files answers more questions correctly (on a tie, the cheaper one).

**Both** passes only if all three hold:
1. **Accuracy:** it answers at least 10 percentage points more of the 48 questions correctly than the best single setup, which means at least 5 more questions.
2. **Cost:** its average cost per question is at most 20% above the best single setup's.
3. **RAG on concept questions:** it calls `search_knowledge` on more than half of the 24 concept questions (13 or more).

## What each outcome means
- All three pass: **build it** — give agents file search next to RAG.
- Bar 1 passes but bar 2 or 3 fails: **change it** — the combination helps, but the tools or instructions need work before building.
- Bar 1 fails: **drop it** as a combined feature. If one single setup beats the other by 10 points or more, that is reported as its own finding.

## Code question (secondary, not part of the pass bar)
Whether raw code should stay searchable next to the GraphRAG graph (CON-28) is read from the 24 code questions: if files or both answers at least 10 points more of them correctly than rag, keeping raw code files searchable is recommended.

## Known limits, stated before the run
- One attempt per question: a 5-question gap on 48 is near the noise floor for a single run. The results report the per-question table and an exact McNemar test between both and the best single setup as context; the bar above is what decides.
- The question writers had read the files, so exact questions use the files' own words, which favours exact search on those questions by design.
- Connapse's MCP server instructions (which say to prefer `search_knowledge`) reach the agent in the rag and both setups, as they would in production.
