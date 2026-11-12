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
| **Finding tables by description** (CON-E6 ticket 13, suite `table-search-v1`; see below) | Hybrid puts every gold table in the first 10 for at least 75% of the 885 test questions, at least 5 points above keyword-only ranking, exact McNemar p < 0.01 | `table_list` unchanged at 50 visible tables or fewer; hidden columns never change the ranking | 0 LLM tokens, one query embedding per search, ranking p95 under 50 ms over 876 tables |
| **Nested files as tables** (CON-335, the `tables-v1` nested set; see below) | At least 85% of the 60 held-out nested questions right, every family at least 75% | tables-v1's 180 held-out questions with the tables setup: at least 176 right (the baseline is 179) | 0 server AI tokens; reported: tool calls and agent cost per question |
| **Memory on the table layer** (CON-E6 ticket 12; see below) | — | `memory-v1` Recall@5 no Holm-significant drop; memory permission and time tests pass unchanged with the restricted login | Memory p95 within the agent-memory row |

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

### Bar for nested files

Written on 2026-11-12 for CON-335, before the nested set's questions or any run existed. The nested set is
`eval/datasets/tables/nested` (four files: nested JSON, JSON Lines, XML and YAML, built by
`eval/tools/tables/build_nested.py`), uploaded as a user's files would be into one container. It is asked with the
tables setup: the four table tools, the same base prompt, model, turn limit and grader as the main bar, one attempt
per question. Its 72 questions are split before any run into 12 `tune` questions, used for dry runs, and 60 `test`
questions, 10 in each of six families:

- **lookup:** a value inside a nested object (a dotted column);
- **filter_count:** counting parent rows by a nested field;
- **child_aggregate:** a count or sum over a child table, filtered through its parent;
- **grandchild:** a question that joins two levels of child tables back to the top;
- **scalar_list:** a list of plain values, such as tags, loaded as a one-column child table;
- **json:** a value inside data that can't be flattened (arrays of arrays), stored as a jsonb column.

CON-335 ships only if, scored once on the 60 `test` questions:

1. **Accuracy:** at least **85%** (51 of 60) right.
2. **Every family:** at least **75%** right in each family (8 of 10).
3. **No drop on flat tables:** the tables setup on tables-v1's 180 held-out questions answers at least **176** right,
   against 179 on this branch's base. Three questions allow for run-to-run noise in one-attempt agent runs.
4. **Cost:** 0 server AI tokens; tool calls and agent cost per question are reported, not gated.

A failed bar follows the main bar's rule: a change made after seeing `test` results is judged on a fresh nested set
built with a new seed, never by changing this bar.

### Bar for GraphQL and the flexibility set

Written on 2026-11-12 for CON-336, before any GraphQL run and before any run on the flexibility set. Three arms answer
the same questions with the same agent (`claude-sonnet-5-5` at medium effort), prompt, 25-call limit and
`TableAnswerGrader`; only the tools differ:

- **S (structured):** `table_list`, `table_describe`, `table_read`, `table_aggregate`, `search_knowledge`, `get_document`;
- **Q (SQL):** `table_sql`, with its listing fixed to name every readable table, plus `table_list`, `table_describe`,
  `search_knowledge`, `get_document`;
- **G (GraphQL):** `table_graphql` plus `table_list`, `table_describe`, `search_knowledge`, `get_document`.

Each arm gets **one attempt per question**, interleaved by question, with no reruns. Two sets are scored:

- **tables-v1:** the 180 held-out `test` questions CON-328 used, 12 of them asked as a restricted user.
- **flex:** the 70 `test` questions of `eval/datasets/tables/flex` (built by `eval/tools/tables/build_flex.py`), 10
  in each of seven families: nested_json, mixed_kind, column_math, median, rank_in_group, monthly_total and
  three_table_join. Its 14 `tune` questions are for dry runs only.

**GraphQL** (`table_graphql`) ships beside the structured tools only if all of these hold:

1. **Leaks:** zero leaked runs on every arm, and `TableGraphQLTests` pass with zero failures.
2. **No loss on tables-v1:** G answers at least S's count minus **5** of the 180 (about 3 points, room for
   one-attempt noise), and all 12 restricted-user questions.
3. **Flexibility:** on flex, G answers at least **7 more** of the 70 than S does, with an exact McNemar p < 0.05.
4. **Cost:** on tables-v1, G's average agent cost per question is at most **1.2×** S's.

**SQL** (`table_sql`) is recommended to stay, still off by default, only if on flex it answers at least 7 more of the
70 than the better of S and G, with McNemar p < 0.05 against that arm, and on tables-v1 it scores at least S's count
minus 5 at no more than 1.5× S's cost. Otherwise the decision recommends deleting it. Its listing fix was made after
seeing CON-328's test results, so Q's tables-v1 score is result-informed: if it decides the outcome, it must be
confirmed on a set built with a new seed before anything ships.

Reported for every arm, not gated: per-family accuracy on both sets, tool calls per run (mean, median, p90), wall time,
confident wrong answers, runs with no answer, and API list price. A failed bar follows the main bar's rule: a change
made after seeing `test` results is judged on a fresh set built with a new seed, never by changing this bar.

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

## Finding tables by description (CON-E6 ticket 13)

Written on 2026-11-11, before the ranking code, the `table-search` command or any scored run existed. These bars may
not change once results exist.

- **Test set (`table-search-v1`).** Every table of Spider (Yu et al. 2018, CC BY-SA 4.0): 876 tables from 166
  databases, pinned in `eval/MANIFEST.json` as `spider-tables`. They are loaded as one container's tables with their
  original column headers and no descriptions, as CSV files arrive today. Each table is named the way a spreadsheet
  sheet is, by its database and its table (`concert_singer singer`), because a container holding 166 databases' tables
  would hold them as 166 files. The questions are Spider's 1,034 dev questions. A question's gold tables are the
  tables its gold SQL reads after `FROM` or `JOIN`.
- **Split.** A dev database is `tune` when the first byte of the SHA-256 of its name is divisible by 4, otherwise
  `test`: 4 databases and 149 questions to tune on, 16 databases and 885 questions held out. The test split is scored
  once per design.
- **How it runs.** Each question, as written, is the `search` phrase. The ranking is the one `table_list` runs over a
  container with more than 50 visible tables. Arms:
  - **keyword:** keyword scores over each table's card alone;
  - **hybrid:** the same keyword ranking fused with the cosine similarity of the card's embedding to the question's,
    with the app's default embedding model (nomic-embed-text through Ollama, CPU only).
- **Metric.** A question is found when every one of its gold tables is in the first 10 ranked tables.

### Pass bar

1. **Absolute:** hybrid finds at least **75%** of the 885 test questions. Below that, one question in four still sends the
   agent paging through hundreds of tables, which is the problem this ticket exists to fix.
2. **Embeddings earn their cost:** hybrid finds at least **5 points** more test questions than keyword, with an exact
   McNemar test p < 0.01.
3. **Cost and speed:** 0 LLM tokens. One query embedding per search. A card is embedded once per load or change to
   its visible columns. Ranking 876 tables, query embedding excluded, has p95 under 50 ms.
4. **No regression:** at 50 visible tables or fewer, `table_list` returns what it returned before; its existing tests
   pass unchanged.
5. **No leak:** a hidden column's header never enters a card, so hiding or showing a column changes no caller's
   ranking except through what that caller is shown. Tested directly.

### What each outcome means

- **All pass:** hybrid ships.
- **Bar 2 fails but keyword meets bar 1:** keyword ranking ships alone, with no card embeddings.
- **Bar 1 fails for both arms:** nothing ships. A redesign is judged on a fresh held-out set: 500 questions drawn with
  seed 13 from Spider's train questions over the same 876 tables, unopened until then.
- **Bar 3, 4 or 5 fails:** blocked until fixed.

### Result (2026-11-11, scored once)

**Hybrid ships.** On the 885 test questions, 876 tables, 0 load failures and 0 unresolved gold tables:

| | Found in top 10 |
|---|---|
| keyword | 82.7% |
| hybrid | 89.4% |

1. Passed: 89.4% ≥ 75%.
2. Passed: +6.7 points; 60 questions only hybrid found, 1 only keyword found, exact McNemar p = 5.4e-17.
3. Passed: 0 LLM tokens, one query embedding per search, every card embedded at load (none left at search time),
   ranking p95 5.0 ms (p50 2.0 ms). The harness subtracts a second, cached read of the query vector from each
   timing; adding that read's own p95 (8.4 ms) back still leaves the ranking far under 50 ms.
4. and 5. Passed: the existing `table_list` tests pass unchanged, and `TableListSearchTests` and `TableCardIndexTests`
   cover hidden columns.

Only the `tune` split was used for building: there keyword found 89.3% and hybrid 94.0% of 149 questions. Ranking
first missed bar 3 there (p95 78 ms), because each search sent every card's id and hash to the database twice; it was
redesigned to send only the query vector before the test split was opened.

## Baselines

Recorded on 2026-10-24 with the `hybrid` config (nomic-embed-text through Ollama, reranker off) from the commit
that adds this file. See `eval/baselines/2026-10-24.md`.
