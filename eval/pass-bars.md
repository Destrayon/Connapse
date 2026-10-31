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

## Baselines

Recorded on 2026-10-24 with the `hybrid` config (nomic-embed-text through Ollama, reranker off) from the commit
that adds this file. See `eval/baselines/2026-10-24.md`.
