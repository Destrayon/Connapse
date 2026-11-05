# Pass bars

What each graph-related change must show before it merges (epic CON-E3). The bars were written on 2026-10-24,
before any of these features or their results existed. A change merges only when it passes every gate in its row.

## Rules for every row

- **Held-out only.** A bar is judged on the `test` split, scored once per design. Tuning uses the `dev` split.
- **Significance.** A gain counts only when the harness's Holm-corrected paired permutation test gives p < 0.05.
  When the report's minimum detectable effect is larger than the bar, the result is "not shown", which fails.
- **Same baseline.** The comparison is against the `hybrid` config (the app's defaults) built from the commit the
  change branches from, on the same machine and embedding model.
- **No regression.** `v1`, `enterprise-v1` and `pdfqa-v1` (and `links-v1` for rows that are not about links) show
  no dataset with a significant drop in nDCG@10 (pdfqa-v1: MRR@10, its passage metric).
- **No leaks.** The permission-leak suite (CON-31) passes with zero failures. A leak fails the change whatever
  its quality numbers are.
- **Cost.** Index time is the harness's `indexed in` log line; latency is the report's p95 per dataset. AI tokens
  are counted at the providers; zero means no LLM call at index or query time.
- **A change that fails is dropped or redesigned, never merged.** A redesign is judged on a fresh held-out set
  (a new snapshot or a new seed), because the old one has been seen. A bar is never changed after a result
  exists; a new bar is committed first and needs a fresh held-out set.

## Bars

| Change | Better | No regression | Cost and speed |
|---|---|---|---|
| **Engine link layer, off** | Every suite's ranked results identical to the baseline | — | Index time and p95 within run-to-run noise |
| **Engine link layer, on** (no source writes links) | Identical rankings on sources without links | All suites | Index +10% at most, p95 +10% at most, 0 AI tokens |
| **Linked sources** (GitHub issues/PRs, Jira, Confluence links) | `links-v1` test MRR@10 at least +0.05 over the baseline; Recall@10 at least +0.10 on the list kinds (`gh-subissues`, `gh-siblings`, `gh-epic-delivery`, `jira-duplicate`, `jira-subtasks`, `jira-siblings`); no `hops:` or `kind:` group with a significant drop | All suites, plus ordinary GitHub and Jira questions in `enterprise-v1` (`source:` breakdown) | Sync/index +10% at most, p95 +150 ms at most, 0 AI tokens |
| **Code retrieval** (suite `code-v1`, to be built; answer keys from a compiler-grade indexer) | Definition questions Hit@1 ≥ 0.95; caller/usage Recall@10 at least +0.15 over a grep baseline; parser call edges at high confidence ≥ 0.90 precision against the compiler's; "how does X work" MRR@10 at least +0.05 over the baseline | All suites | Under 1 s per changed file, a full Connapse checkout under 2 min, symbol lookups p95 under 300 ms, 0 AI tokens |
| **Agent memory** (suite `memory-v1`, to be built; answer keys from a scripted fact timeline) | Current-value, as-of-date and what-changed questions at least +0.10 Recall@5 over the same notes stored as plain documents | All suites; ordinary recall questions no significant drop against plain documents | A write lands in under 200 ms, recall p95 +150 ms at most, 0 AI tokens on the server |
| **AI link extraction** (opt-in) | `links-v1` test MRR@10 at least +0.05 over links-only | All suites | Reported: tokens per passage, index time; no default-on without a new bar |
| **Container memory for search** (suite `memory-search-v1`, CON-244; see below) | Correct-document score on the memory tasks at least +0.15 over the same agent without memory | Control and stale tasks non-inferior within 0.10 | Reported: search calls, tool calls and tokens per task |

## Container memory for search (CON-244)

Written on 2026-11-04, before the `memory-search` command or any scored run existed. The result decides whether
container-level memory stays in the product: **keep** it only if it clearly beats plain search; **drop** it if it is
no better or worse.

- **Arms.** Both run the same agent, model, system prompt, questions and repetitions. **Off** has
  `search_knowledge` and `get_document`, as an agent searches today. **On** adds only `memory_recall`. Nothing tells
  the agent to use memory. The tool descriptions are the MCP server's own.
- **Fixed before the run:**
  - **Model:** `claude-sonnet-5-5` at temperature 0. If ClaudePM's go names another model, the change is committed
    here before the run.
  - **Repetitions:** 3 per task and arm, averaged per task before any test.
  - **Limits:** at most 12 tool calls per run. `search_knowledge` returns its default 10 results in Hybrid mode.
  - **Split:** the 112 `test` tasks, scored once. The `dev` tasks are for building and checking the command only.
  - **Exclusions:** none. A provider error is retried up to 3 times; a run that still fails, runs out of tool calls
    or never answers scores 0, and the count of such runs is reported per arm.
- **Score per run:** 1 when the documents the agent cites include a gold document and no labelled stale document,
  else 0. A task's score is the mean over its repetitions.
- **Sets:** **M** is the 64 current, alias, correction and location tasks. **H** is the 48 control and stale tasks.
- **Verdicts** (difference = On − Off, paired by task):
  - **KEEP** only when both hold:
    1. on M the mean difference is at least +0.15, its 95% BCa interval lies above 0, and the permutation p < 0.05;
    2. on H the lower end of the 90% BCa interval is above −0.10.

    Both must pass, so each is tested at α = 0.05 with no Holm split (an intersection-union test).
  - **WORSE** when the 95% interval of the difference over all 112 tasks lies below 0, or when H's mean
    difference is below 0 and the lower end of its 90% interval is at or below −0.10.
  - **NO BETTER** for every other result.
  - **WORSE and NO BETTER both mean DROP.**
- **Minimum detectable effect.** The report gives the minimum detectable effect for M and H. If it is above 0.15 on
  M and KEEP fails, the report says the run could not tell a tie from a 0.15 gain; the verdict is still DROP.
- **Reported, not gated:** each kind separately, documented and undocumented halves, answer-value accuracy, stale
  citations, run recall of gold documents, first-query MRR@10, search and tool calls with their intervals, tokens,
  and a few answers memory changed for better and for worse.

## Baselines

Recorded on 2026-10-24 with the `hybrid` config (nomic-embed-text through Ollama, reranker off) from the commit
that adds this file. See `eval/baselines/2026-10-24.md`.
