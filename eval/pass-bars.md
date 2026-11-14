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
| **Learned container memory** (suite `memory-learned-v1`, CON-244; see below) | Same rule, session-2 memory tasks, memory written by session-1 agents | Same rule, session-2 control and stale tasks | Reported: session-1 scores and facts written |
| **Tables container** (suite `tables-v1`, CON-E6; see below) | On the 180 held-out questions: +25 points over rag, exact McNemar p < 0.01, at least 70% correct, no family lower than rag; "both" no worse than rag on the 30 mixed questions | All suites; the leak suite and the table leak tests | 0 server AI tokens, `table_aggregate` p95 under 500 ms on 100k rows, agent cost at most 1.5x rag's |
| **Passage links to tables** (CON-E6 ticket 15, the `tables-v1` linked set; see below) | 80% of the 20 table questions follow a link; both-linked at least as many of 30 right as both-unlinked and 4 more of the 20 | 10 prose questions: at most 2 make a table call, none fewer right; the hidden-table leak test | Agent cost at most 1.2x both-unlinked's |
| **Memory on the table layer** (CON-E6 ticket 12; see below) | — | `memory-v1` Recall@5 no Holm-significant drop; memory permission and time tests pass unchanged with the restricted login | Memory p95 within the agent-memory row |
| **Meaningful search scores** (CON-278; see below) | Unrelated questions no longer score near 1; scores track relevance better; a no-match signal that works | Rankings identical to the baseline on `v1` | Search p95 +10% at most; a search after the embedding model's idle unload measured and fixed or explained |
| **Finding and saving memories** (suite `memory-agent-v1`, CON-313; see below) | Reworded, indirect and partial-word find tasks at least 0.85 and at least +0.15 over today's memory search | Exact, absent and save tasks non-inferior within 0.10; save tasks at least 0.90; the leak suite | Recall p95 under 300 ms; tool calls per task at most 1 more than today's |
| **Server-side tool search** (compact tool list, CON-346; see below) | Compact `tools/list` plus server instructions at most 30% of the full list's tokens; median input tokens per routing task at most 80% of the full list's, tool search off | `memory-search` test score and routing task success non-inferior within 0.05, in both tool-search modes; the 4.1 integration tests | Reported: `find_tools` calls, argument errors, tool calls and wall time per task |

## Meaningful search scores (CON-278)

Written on 2026-11-08, before the `scores` command or any measurement existed. Today hybrid search scales each
side of the fusion to 0–1 within each query, so the top hit always scores about 1 however unrelated it is.

- **Measured with** the `scores` command on suite `v1`, `hybrid` config, test queries. Each dataset's own test
  queries are **in-domain**. Each dataset is also searched with the test queries of another dataset in the suite
  (the next one in suite order, wrapping around); those are **foreign** queries. A hit is relevant when its document
  has a grade above 0. Rules are tuned on the `dev` suite only and judged once on `v1`.
- **Baseline:** the same command on the scoring of the commit this change branches from.
- **Bars** (all must pass):
  1. **No near-perfect unrelated hit.** Over foreign queries, the median top-1 score is at most 0.5 and at most 10%
     of them have a top-1 score of 0.8 or more.
  2. **Scores track relevance.** Replaced on 2026-11-09 with the user's approval, after the first `v1` judgement
     failed the original bar (in-domain AUROC at least 0.05 above the baseline's, interval above 0) and before the
     held-out run below existed. Both parts must pass; intervals are 95% bootstrap intervals of the difference
     against the baseline (resampling queries, 1,000 draws, seed 278).
     - **In-domain, no worse.** Pooled AUROC of the score over every top-10 hit of in-domain queries, relevant
       against not relevant: the interval's lower bound is above -0.02.
     - **In-domain and foreign, better.** Pooled AUROC over every top-10 hit of in-domain and foreign queries,
       where a foreign hit is never relevant: the interval's lower bound is above 0.
     Judged on the held-out run: suite `v1`, `--split dev`, datasets `ragbench-techqa` and `ragbench-emanual`
     (RAGBench validation questions, 304 and 132; each dataset's foreign queries are the other's). No dev or tune
     run used these questions, and the `v1` test run that failed the original bar used only test questions; the
     corpus is the same `v1` index. The `v1` test numbers are reported alongside.
  3. **No-match signal.** The search result's no-match flag is set for at least 70% of foreign queries and for at
     most 10% of in-domain queries whose top-1 hit is relevant.
  4. **No regression.** Every in-domain query's top-10 ranking is identical to the baseline's, so nDCG@10 is
     unchanged. `enterprise-v1` and `pdfqa-v1` are covered by the same identity, which a unit test proves for any
     input with the default settings (no reranker, minimum score 0, auto-cut off).
  5. **Speed.** Search p95 over in-domain queries is at most 10% above the baseline's. A search made after the
     embedding model has been idle longer than Ollama's unload time is measured before and after; after the change
     it is at most 1.5 times the warm p95, or the report explains why not.

### Redesign judgement (CON-278)

Written on 2026-11-09, after the first design failed bar 2 on the held-out run above and before the redesign's
code or any fresh-split run existed. The redesign flags a search as having no good match when its top hit's raw
cosine similarity is below a cutoff set per embedding model, and lowers scores only on flagged searches. The bars
above are unchanged; this section names the fresh held-out set and how each model's cutoff is chosen.

- **Fresh split.** The `scores` command's `--split fresh` on suite `v1`, datasets `ragbench-techqa` and
  `ragbench-emanual`, each dataset's foreign queries being the other's. From each dataset's RAGBench train rows,
  drop any whose ID or question text is also a dev or test question, keep one row per ID, order by the lowercase hex
  SHA-256 of `con-278-fresh:` + ID (ordinal), and take the first 300. A hit is relevant when its document is one the
  row's relevant sentence keys point to. No run has used train questions as queries; their documents are already in
  the `v1` index, as they always were.
- **Cutoff rule.** On the spent `--split dev` questions of the same two datasets: the cutoff is the 5th percentile
  of the top hit's raw cosine over in-domain queries whose top hit is relevant, and the floor point is the median
  top raw cosine over foreign queries, each rounded to 0.01. A flagged search's scores are multiplied by 0.79 at the
  cutoff, falling linearly to 0.3 at the floor point, and by 0.3 below it. For `nomic-embed-text` (the default)
  this gives 0.69 and 0.61. A model without a cutoff is never flagged and its scores are unchanged.
- **Models.** `nomic-embed-text` and `qwen3-embedding:0.6b` (1024 dimensions), each with its own index of the two
  datasets and its own baseline from the base commit. Bars 1, 2, 3 and 5 are judged per model on the fresh split;
  bar 4 by the ranking identity on the fresh split and the unit test.
- **Outcome.** The change merges only if `nomic-embed-text` passes every bar. If `qwen3-embedding` fails one, it
  ships without a built-in cutoff (never flagged) and the report says so.
- **Result (2026-11-10, `nomic-embed-text` only).** Bar 1 **failed**: the foreign median top-1 is 0.358 (baseline
  0.863), but 12.3% of foreign queries still score 0.8 or more (limit 10%, baseline 76.5%); 73 of the 74 are
  `ragbench-emanual` questions searched against the `ragbench-techqa` corpus. Bars 2–5 passed: in-domain AUROC
  -0.010 (interval -0.017 to -0.004), pooled with foreign +0.078 (0.067 to 0.090); 83.8% of foreign queries flagged
  and 3.9% of in-domain queries with a relevant top hit; 0 of 600 rankings changed; p95 594 ms against 608. The
  `qwen3-embedding:0.6b` run was stopped for low machine memory, so it ships without a built-in cutoff. The user
  chose to ship with the bar 1 miss stated.

## Container memory for search (CON-244)

**Result:** the planted-memory arm passed, but the realistic arm below came back WORSE, so container memory was
dropped and CON-255 removed it. The rules here stay as the record. The `memory-search` and `memory-learned` commands
now run only the Off arm, and the Off scores in `eval/agent-search/con-244/results.md` are the ones to match.

Written on 2026-11-04, before the `memory-search` command or any scored run existed. The result decides whether
container-level memory stays in the product: **keep** it only if it clearly beats plain search; **drop** it if it is
no better or worse.

- **Arms.** Both run the same agent, model, system prompt, questions and repetitions. **Off** has
  `search_knowledge` and `get_document`, as an agent searches today. **On** adds only `memory_recall`. Nothing tells
  the agent to use memory. The tool descriptions are the MCP server's own.
- **Fixed before the run:**
  - **Model:** `claude-sonnet-5-5` with adaptive thinking at effort `medium` and no temperature (amended before any
    run: the model rejects a temperature, so runs are not deterministic and the 3 repetitions carry the variance).
    If ClaudePM's go names another model, the change is committed here before the run.
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

### Learned container memory (CON-244, realistic arm)

Written on 2026-11-05, before the `memory-learned` command or any scored run existed. The memory-search run above
seeds memory by hand, so it cannot say whether memory an agent writes for itself helps. This arm can, and **the
keep-or-drop decision for container memory rests on it.**

- **Dataset:** `memory-learned` (suite `memory-learned-v1`): every fact a task needs is in a document.
- **Per repetition, in a fresh container holding the corpus:**
  1. **Session 1.** An agent answers each session-1 test question with `search_knowledge`, `get_document`,
     `memory_recall`, `memory_remember` and `memory_correct`. Its system prompt asks it to save what it learned that
     would help answer related questions later, citing the documents. Nothing else is written into memory.
  2. The late documents are uploaded and indexed.
  3. **Session 2.** A fresh agent answers each session-2 test question twice, with the original memory-search system
     prompt, which never mentions memory: **Off** has `search_knowledge` and `get_document`; **On** adds only
     `memory_recall`, reading what session 1 wrote. Both sessions use the same user.
- **Fixed as above:** model, effort, 3 repetitions, 12 tool calls per run, Hybrid search with 10 results, retries,
  exclusions, the score per run, the paired statistics and the KEEP, WORSE and NO BETTER rules. Only session-2 test
  tasks are scored: **M** is the 64 current, alias, correction and location tasks, **H** the 28 control and 12 stale
  tasks, 104 in all.
- **Reported, not gated:** everything listed above, plus session-1 scores and the facts session 1 wrote (how many,
  how many cite documents).
- **Budget:** one full run (228 session-1 and 624 session-2 runs) must use at most about 5% of the seven-day
  subscription window, on the subscription only.

## Tables container (CON-E6)

Written on 2026-11-08, copied from `docs/plans/con-e6-tables-container.md` before any table code, tool or scored run
existed. The test set is `tables-v1` (`eval/datasets/tables/card.md`). These bars may not change once results exist.

- **How it runs.** Claude Sonnet 5.5 through `claude -p`, the same base prompt for every setup, at most 25 turns,
  one attempt per question. The setups:
  - **rag:** the same CSV files in a documents container, parsed as text by today's TextParser and searched with
    `search_knowledge` and `get_document` on default hybrid search;
  - **tables:** the four table tools over a tables container;
  - **both:** all six tools, on the mixed set;
  - **both-linked** and **both-unlinked:** all six tools on the linked set, with passage links shown and hidden.
- **Grading.** Answers come back as typed JSON and are graded by `TableAnswerGrader` (the DataBench rules): numbers
  within 1% relative, or exactly when the gold answer is an integer; lists as sets; categories and booleans exactly
  after normalizing case and spaces. No judge model.
- **Questions asked as the restricted user** are correct only with the trimmed answer.

### Pass bar for the tables container

The tables container ships only if all of these hold on the 180 held-out `test` questions:

1. **Accuracy:** tables answers at least **25 points** more questions correctly than rag, with an exact McNemar test
   p < 0.01.
2. **Absolute accuracy:** tables answers at least **70%** correctly.
3. **Every family:** no family scores lower with tables than with rag.
4. **The mixed set:** "both" answers no fewer of the 30 mixed questions than rag does.
5. **No harm to text search:** v1, enterprise-v1, pdfqa-v1 and links-v1 show no Holm-significant drop and nDCG@10
   falls by at most 0.01 on each, because table containers are kept out of search.
6. **Leaks:** the CON-31 leak suite and the new table leak tests (CON-E6 ticket 7) pass with zero failures.
7. **Cost and speed:** 0 server AI tokens. `table_aggregate` p95 under 500 ms on a 100k-row table. Average agent
   cost per question at most 1.5x rag's.

### Bar for passage links

CON-E6 ticket 15's links ship only if, on the linked set:

1. **Agents follow the link when it helps.** On the 20 table questions with both-linked, at least **80%** have a
   followed link: a successful `table_read` or `table_aggregate` on a table id that first appeared in that run in a
   `search_knowledge` or `get_document` answer, with no `table_list` call before it. The qualifying tool trace is
   saved with each answer.
2. **It helps.** both-linked answers at least as many of the 30 questions correctly as both-unlinked, and at least
   **4 more** of the 20 table questions.
3. **It doesn't distract.** On the 10 prose questions, both-linked makes a table call in at most 2, and answers no
   fewer correctly than both-unlinked.
4. **No leak.** A planted-leak test where the caller can read the document but the admin hid its table shows no
   reference, no table name and no change in the passage's output, in `search_knowledge`, REST search,
   `get_document` and `table_list`. The same holds after the document is re-ingested, including with its tables
   reordered and after a failed ingest is retried.
5. **Cost.** Average agent cost per question with both-linked is at most 1.2x both-unlinked's.

Thirty questions are too few for a significance test, so these bars are counts. They gate ticket 15 only, not the
tables container. A failed bar follows the same re-test rule as the main bar, with the one sealed reserve linked set
of 30 questions.

### Gate for moving memory

Memory moves onto the table layer (CON-E6 ticket 12) only if:

- memory-v1 shows no Holm-significant drop in Recall@5 against `memory-v1-heldout-2026-10-27`;
- the memory p95 latency stays within the **Agent memory** row above;
- the memory permission and time integration tests pass unchanged with the real restricted login.

This gate is measured in ticket 12's own PR. v0.5.0 needs both the tables pass bar and this gate.

### What each outcome means

- **All pass:** ship in v0.5.0.
- **Any result-informed change.** A change made after seeing held-out results is re-tested on the next sealed
  reserve set, with every bar re-run, and never by changing a bar. This covers a redesign after bar 1 or 2 fails and
  a fix for one family after bar 3 fails. There are two reserve sets, so at most two re-tests. If bar 1 or 2 still
  fails after them, CON-E6 stops at the eval and the build tickets after the proof are dropped.
- **Bar 5 or 6 fails, or the memory gate fails:** that work is blocked until it is fixed. A fix that touches table
  tools also counts as a result-informed change.
- **Sealed sets stay sealed.** The reserve files are committed compressed and pinned by SHA-256 in
  `eval/MANIFEST.json`. Nobody opens, prints or scores them until a re-test is due, and each is used once.

The size of the bar comes from the literature. TAG and TableRAG show gaps of 30–55 points, so a +10 bar would let a
weak tool path pass. A 25-point gain on 180 paired questions is well above the McNemar power floor, which is about
35 pairs for a 30-point gain.

## Finding and saving memories (CON-313)

Written on 2026-11-09, before the `memory-agent` command had scored anything. It judges the change CON-313's plan picks
for memory search (see `docs/research/memory-findability-2026-11-09.md`) by how well a real agent does its memory work,
not by ranking alone.

- **Dataset:** `memory-agent` (suite `memory-agent-v1`), 245 made-up facts about the fictional company Orrin Systems,
  shaped like a project manager's subject, attribute and value facts; 10 of them have an older version in history.
  Built by `eval/tools/build_memory_agent.py`, which checks each task's wording rule. See its `card.md`.
- **Setup.** The facts are saved through the MCP tools into a personal space named `pm-desk`, on a local in-process
  instance with the `keyword` config (memory facts are not embedded today; a change that embeds them runs under the
  config it needs, and the baseline is re-run under that config if it differs).
- **Find arm** (74 test tasks). The agent has `memory_spaces`, `memory_recall` and `memory_timeline`, and is told its
  memory is `pm-desk`. A run scores 1 when it answers and cites the current version of a gold fact and no older
  version of any fact; an absent task scores 1 when it answers and cites no fact. Kinds: exact (10), reworded (31),
  indirect (15), partial (10), absent (8). **Hard** is reworded + indirect + partial, 56 tasks.
- **Save arm** (24 test tasks). Each run gets a fresh user and a freshly seeded space, the read tools plus
  `memory_remember` and `memory_correct`, and is told to keep memory accurate. It is scored on what memory holds
  afterwards: **new** (10) needs one or two new facts holding the expected words and no seeded fact changed;
  **update** (8) needs the target's old value ended or corrected, one or two new versions holding the new value, and
  nothing else changed; **duplicate** (6) needs nothing new saved and nothing changed except, harmlessly, the target
  re-saved with the same value.
- **Retrieval arm** (reported, not gated): `memory_recall` with the question as the query, limit 10. Hit@1, hit@10 and
  MRR@10 per kind, how often an absent question returns anything, and recall latency p50 and p95.
- **Fixed before the run:** model `claude-sonnet-5-5` at effort `medium`, 3 repetitions averaged per task, at most 12
  tool calls, the `test` split scored once, `dev` only for building. A run that errors, runs out of calls or never
  answers scores 0 in the find arm; in the save arm only an error scores 0, since memory's state is what is judged.
- **Verdict** (difference = change − today, paired by task; the paired permutation test and BCa intervals of
  `Statistics/PairedStats.cs`). The change **passes** only when all hold:
  1. Hard find tasks score at least 0.85, at least +0.15 over today, with permutation p < 0.05;
  2. exact, absent and save tasks are each non-inferior: the lower end of the 90% BCa interval of the difference is
     above −0.10;
  3. save tasks score at least 0.90;
  4. `memory_recall` p95 is under 300 ms on the retrieval arm, and mean tool calls per find task rise by at most 1;
  5. the permission-leak suite passes with zero failures.

  Each test is at α = 0.05 with no Holm split, since all must pass. A change that fails is redesigned and judged on a
  fresh held-out set, per the rules above.
- **Reported, not gated:** each kind, run recall of gold facts (`sawGold`), whether the first `memory_recall` showed
  gold, citations of old versions, recall and tool calls, other facts changed and same-chain corrections in the save
  arm, and tokens.
- **Today's result** (2026-11-10, conforming run, `eval/baselines/memory-agent-test-2026-11-10.json`): hard find
  tasks **0.946**, exact 1.00, absent 0.875, save 0.986, `memory_recall` p95 20 ms. On the retrieval arm the question
  as typed puts gold in the top 10 for 0.50 of hard tasks and 0 of indirect and partial ones; the agent closes the gap
  by rewording its query and listing a subject's facts, at 1.46 recall calls per find task (2.04 on indirect). Bar 1
  cannot be met as written, since +0.15 over 0.946 exceeds 1.0. The bar is not changed here; the plan doc proposes
  what to do about it.

### Replacement bar, approved 2026-11-10

The bar above cannot be met, and its test tasks have been seen and shaped the stage-1 design, so it is replaced. The
user approved the replacement on 2026-11-10 and added that the change must beat or tie grep over the same facts and
today's memory tools on every task type: "if accuracy is good enough, we just need to make search easier and more cost
effective, and hopefully make it better than grep or how you use memory currently in every case." The section above
stays as written. See `docs/research/memory-findability-plan-2026-11-10.md`.

- **Sealed set.** A new made-up company, written by the same generator rules, with at least 60 hard find tasks, 8
  exact, 8 absent and 24 save tasks (new, update and duplicate). It is committed before any stage-1 code and scored
  once. Model, effort, 3 repetitions, 12-call limit and scoring are as in the section above.
- **Three arms on the same tasks,** with the same model, prompt shape, call limit and answer tool:
  - **today:** the current memory tools, run on the sealed set;
  - **grep:** the space's live facts rendered one per line (`id | subject | attribute | value | valid from | status`),
    with a `grep` tool (regex, case-insensitive option, at most 50 matching lines) and a `read` tool for a range of
    lines; save tasks also get `memory_remember` and `memory_correct`, and the rendering is rebuilt after each write;
  - **change:** the memory tools with the change.
- **Win or tie on every task type.** For each kind (exact, reworded, indirect, partial, absent, new, update,
  duplicate), against today and against grep, comparing means over the kind's tasks:
  1. score is at most 0.05 below the comparator's;
  2. mean tool calls are at most 0.1 above the comparator's;
  3. mean list-price cost per run is at most 5% above the comparator's.
- **A real win.** Pooled over find tasks, the change needs fewer tool calls and a lower list-price cost per run than
  both today and grep, each by the paired permutation test at p < 0.05.
- **Hard find tasks against today:** the first `memory_recall` shows gold for at least 0.90 of them and at least +0.10
  over today, and recall calls per task fall by at least 0.3, both at p < 0.05. On the retrieval arm, hard hit@10 is at
  least 0.80.
- **No harm against today:** hard, exact, absent and save scores are each non-inferior, with the lower end of the 90%
  BCa interval of the difference above −0.05.
- **Speed and leaks:** `memory_recall` p95 under 300 ms, and the permission-leak suite passes with zero failures.

All must hold. If stage 1 fails, stage 2 (embeddings) is built and judged the same way on a second sealed set. The grep
arm is built and committed with the sealed set, before any stage-1 code.

## Baselines

Recorded on 2026-10-24 with the `hybrid` config (nomic-embed-text through Ollama, reranker off) from the commit
that adds this file. See `eval/baselines/2026-10-24.md`.

## Saving a fact shows the facts it might repeat or contradict (CON-332)

Written on 2026-11-12, before the check had run on anything. The check runs on every `memory_remember` and returns at
most three current facts the new one may repeat or contradict, or nothing.

- **Case set.** `tests/Connapse.Integration.Tests/Memory/MemorySaveMatchCases.cs`, written with this bar and before the
  first run. Each case seeds a space, saves one fact and names the facts it must show. Families: **same** (same subject
  and attribute, in any case or punctuation), **reworded attribute** (same subject, attribute reworded, e.g. "status"
  and "current status" or "state"), **similar subject** (subject spelled or extended differently, e.g. "Halcyon" and
  "Halcyon API"), **unrelated** (must show nothing, including another ticket's status: "CON-291" and "CON-292" are
  different subjects), and **synonym subject** (the subject named in other words, e.g. "cold-room watcher" for
  "Gannet"). The two real cases are in it: two status facts for one ticket, and one thing saved under a slightly
  different attribute.
- **Time.** A space holding 5,000 current facts, 200 saves, Postgres in Testcontainers capped at 2 CPUs and 4 GB.
- **Passes** only when all hold:
  1. every **same** case shows its fact first;
  2. at least 95% of **same**, **reworded attribute** and **similar subject** cases, pooled, show their fact;
  3. at least 90% of **unrelated** cases show nothing;
  4. no save shows more than three facts, and no fact is changed, ended or deleted by a save;
  5. the check adds at most 50 ms at p95 to a save, and a whole save takes at most 150 ms at p95. A check that runs
     past 500 ms is cut off and shows nothing, and the save still succeeds.
- **Reported, not gated:** how many **synonym subject** cases show their fact. Without fact embeddings (CON-322's stage
  2, out of scope here) these are expected to miss; the count measures the gap.
- **Fixed before the run:** the case set above and these thresholds. A change to either after the first run is a
  new bar, written down with the reason.

## Server-side tool search (CON-346)

Written on 2026-11-12, before any code or run, copying section 5 of `docs/research/mcp-tool-scaling-2026-11-12.md`
(CON-343). The compact list shows the four core search tools plus `find_tools`, `call_read`, `call_write` and
`call_destructive`; the full list shows every tool, as today. The compact list becomes the default only if every gate
below passes.

- **Arms.** *Full* is the tool list of the commit this change branches from (`release/0.5.0`), descriptions
  unchanged. *Compact* is the 8-tool list. Each arm runs twice: `ENABLE_TOOL_SEARCH=false` (stands in for Claude
  Desktop, ChatGPT and Gemini CLI, which load every tool) and `ENABLE_TOOL_SEARCH=true` with Claude Code's
  ToolSearch the only built-in tool allowed (stands in for Claude Code, Codex, Cursor and VS Code). A `true` run
  that records no ToolSearch call over its whole task set is invalid and is rerun after fixing the setup, not
  scored.
- **Fixed for every arm:** model `claude-sonnet-5-5` at effort `medium`, 3 repetitions per task, at most 12 tool
  calls, the `test` split scored once, the same commit and machine. A provider error is retried 3 times; a run that
  still fails scores 0.
- **Routing set** (`eval/datasets/tool-routing`, built and frozen before any scored run). 60 held-out tasks, 20
  document, 20 code and 20 memory, each naming the tool that must run (called directly or through a call tool), plus
  a dev split used only to build the harness. A routing task succeeds when the gold tool runs with valid arguments
  within the call cap.
- **Non-inferior within 0.05:** passes only when the lower end of the 90% BCa interval of the paired per-task
  difference (compact − full) is above −0.05. An interval reaching −0.05 or below fails, whether the change is worse
  or the run is too small to tell.

**Passes** only when all hold:
1. **Tool tokens.** The compact `tools/list` plus the server instructions is at most 30% of the full `tools/list`'s
   tokens, by `o200k_base` and by Anthropic's token-counting endpoint. A unit test checks the `o200k_base` half on
   every build.
2. **Input tokens.** With tool search off, the median total input tokens per routing task is at most 80% of the
   full list's.
3. **Right answers.** `memory-search` test score and routing task success are each non-inferior within 0.05, in
   both tool-search modes.
4. **Gate.** The 4.1 integration tests pass: a call tool refuses a tool of another risk, scopes still apply through
   the call tools, destructive calls reach `McpApprovalGate`, and every tool is findable by its own name.

- **Reported, not gated:** `find_tools` calls, argument errors, tool calls and wall time per task.
- **A failure** keeps the full list as the default; the search is redesigned and judged on a fresh routing set. These
  bars are not changed after a result exists.
