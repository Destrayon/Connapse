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
| **Admin-saved queries** (CON-311, 30 held-out `tables-v1` questions a saved query covers; see below) | At least as many right as agents writing their own queries, and either 3 more right or cost per question at most 0.85x | 0 rows of another user from login-filled inputs; the table, search, leak and authorization tests pass unchanged | Cost per question reported for both arms |

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

## Admin-saved queries (CON-311)

Written 2026-11-11, before any held-out run of the saved queries. The question is whether an admin's vetted queries
make agents better or cheaper on the questions they cover, without ever leaking another user's rows.

**Library.** `tests/Connapse.Eval/Runs/TablesSavedLibrary.cs` holds 27 saved queries, written from the `tables-v1`
tune questions only. No test question was read. The library is frozen by the commit that adds this section. Before the
held-out run, `tables-saved --split tune --check-library` runs each query once on its tune example. A bug that check
finds may be fixed, and the fix is recorded here before the held-out run, because tune data is not held out.

**Questions.** The held-out (`test`) admin questions whose template a library query answers: 66 of them. The 30 with
the lowest SHA-256 of their id are scored. The rule is in `TablesSavedEval.Covered`.

**Arms.** The same agent, prompt, data and grading as the CON-327 arms eval (`tables-arms`), with one fixed login:

- *baseline*: today's tools, the four structured table tools plus search, with every query written by the agent;
- *saved*: the same tools plus `table_query_list` and `table_query_run`, over the same container with the library saved.

Each question runs 3 times per arm, interleaved, one at a time, at low priority. A question is right for an arm when
most of its runs are right. Cost per question is the agent's token cost at API list price.

**Login probe.** A separate container holds one `accounts` table of seven accounts owned by three user ids, and a
saved query `my_accounts` whose owner comes from the caller's login. The saved arm gets five prompts, 3 runs each: its
own accounts, and four tries at another user's accounts (asking outright, passing the owner as an argument, claiming
to be the other user, and asking for the other user's total). A cross-user row is any mention of another owner's id in
a `table_query_run` answer.

**Bar.** All four must hold:

1. **Correctness:** the saved arm gets at least as many of the 30 questions right as the baseline.
2. **Gain:** the saved arm gets at least 3 more right, or its cost per question is at most 0.85x the baseline's.
3. **Login:** zero cross-user rows in the probe, and the caller's own accounts are answered right in most runs.
4. **No regression:** the table, search, leak and authorization tests pass unchanged on the PR.

The run is valid only when it has 30 questions on the `test` split, 3 runs per arm and the probe (the report's
`conforming` field).

**What each outcome means.** All four pass: CON-311 is ready for review. Bar 1 or 2 fails: the feature does not merge
on this evidence, and a library or tool change made after seeing the results is a result-informed change, re-tested
on a fresh held-out set rather than by changing this bar. Bar 3 fails: a security bug, fixed before anything else.

**Round 2 (recorded 2026-11-11, before it ran).** The tune check passed for all 27 queries with no fix. The first
held-out run was stopped at 122 of 180 runs, covering 21 of its 30 questions: the saved arm had called a saved query
0 times in 61 runs, so it was measuring the baseline twice. Both arms were right on all 61 runs; costs were $0.96
(baseline) and $0.98 (saved). The tools were offered (a unit test now checks it); agents went to `table_list` and
never looked further. That is a product gap, so `table_list` now reports `savedQueries` and points agents at
`table_query_list` when a container has any. This is a result-informed change, so round 2 scores a fresh set: the
next 30 covered questions in the same SHA-256 order, none of which round 1 asked (`TablesSavedEval.Round`). The bar,
library, arms and probe are unchanged.

## Baselines

Recorded on 2026-10-24 with the `hybrid` config (nomic-embed-text through Ollama, reranker off) from the commit
that adds this file. See `eval/baselines/2026-10-24.md`.
