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
| **Merged get/delete/upload tools** (suite `memory-search-v1`, CON-344; see below) | — | Correct-document score non-inferior within 0.05 against the separate tools | Reported: tool-list tokens before and after, tool calls per task |
| **Existing facts in the save answer** (CON-341; see below) | Every counted claudepm duplicate fixed in one call | Writer agents' right answers within 0.05, wrongly replaced facts at most +1 | Reported: saves that carry a list |

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

## One tool each for getting, deleting and uploading (CON-344)

Written on 2026-11-12, before either arm ran. CON-344 merges `get_document`/`get_documents`,
`delete_file`/`bulk_delete` and `upload_file`/`bulk_upload` into one tool each that takes one item or a list. The
merge passes only if agents with the merged tools get no fewer answers right. `memory-search-v1` is the only agent
eval that uses these tools, and it uses only the getting pair; deleting and uploading are held to identical
results by the cross-surface and unit tests, not by an agent eval.

- **Arms.** Both run the `memory-search` command with the same agent, model, system prompt, questions and
  repetitions; only the tools differ, and their descriptions are the MCP server's own.
  - **Baseline** runs on the unchanged code (`main` at 24e6a7b7) with `--tools search_knowledge,get_document,get_documents`.
    *Corrected after the run, 2026-11-12:* `memory-search` at 24e6a7b7 has no `--tools`, so the baseline was built
    from a0acaea8, which is 24e6a7b7 plus the harness-only commit that adds `--tools` and `memory-search-compare`.
    That commit changes nothing under `src/`, so the MCP tools the agent saw are `main`'s. The build was
    `dotnet build -c Release tests/Connapse.Eval -o <baseline-bin>` and the run was
    `Connapse.Eval memory-search --config hybrid --split test --workers 1 --repetitions 3 --model claude-sonnet-5-5
    --effort medium --tools search_knowledge,get_document,get_documents --out baseline.json`.
  - **Merged** runs on the CON-344 branch with `--tools search_knowledge,get_document`, where `get_document` is the
    merged tool.
- **Fixed as in CON-244 above:** `claude-sonnet-5-5` at effort `medium`, 3 repetitions, 12 tool calls per run, Hybrid
  search with 10 results, the `hybrid` config, the 112 `test` tasks, 1 worker, retries and the score per run. A run
  that fails, runs out of tool calls or never answers scores 0.
- **Rule:** `memory-search-compare <baseline.runs.jsonl> <merged.runs.jsonl>` pairs each task's mean score.
  **PASS** when the lower end of the 90% BCa interval of (merged − baseline) over all 112 tasks is above −0.05
  (non-inferiority at margin 0.05, a one-sided test at α = 0.05). Anything else is **FAIL**, and a failing merge
  is redesigned, not shipped. The CON-244 Off runs put the noise at about ±0.014 on this interval, so a true tie
  passes with room to spare.
- **Reported, not gated:** the M and H sets, answer accuracy, tool calls per task, how often each arm used the list
  form, outcomes, and tokens.

## Existing facts in the save answer (CON-341)

Written on 2026-11-12, before any run and before the change was built. A save that lands beside current facts with
the same subject and attribute still succeeds, and its answer lists them (id, value, dates). The change passes only
if each proof below passes.

**Replay of ClaudePM's own memory.** A read-only copy of the `claudepm` space (870 fact versions, exported for
CON-340) is replayed through the real memory service and Postgres store in a throwaway, capped container. A fact
is *current at a save* when it was recorded before the save, not yet superseded, its `validFrom` is empty or not
after the save, and its `validTo` is empty or after the save, which is what the service calls current.

- **Counted set**, fixed now: the saves of a new fact, under any attribute but `note`, `lesson` and `finding`, made
  while another fact with the same subject and attribute (ignoring case) was current. In the copy that is one save,
  `01a1bc93-b998-705b-b340-30000eb0559f` (`CON-291` / `status`), beside `01a1bc77-f7a0-7731-aa12-dc3d9e5d3b44`,
  which was never corrected. Four `worker` saves (`local_9671ef63`, `local_fa0dbb91`, `local_0f351564`,
  `local_09310255`) look like duplicates but landed beside facts whose `validTo` had already passed.
- **R1.** Every counted save's answer lists the fact it landed beside, with that fact's id, value and dates.
- **R2.** Every counted save is fixed with one call that uses only what the answer gave: `memory_correct` on the
  listed id, kind `wrong`, the listed value, and `validTo` set to when the new fact took effect. Afterwards the
  subject and attribute have exactly one current fact, the new one, and the old value is still the answer as of
  just before the save. Today's answer names no other fact, so the same fix needs a lookup first: two calls.
- **R3.** Each of the four `worker` saves answers with no list.
- **Reported, not gated:** how many of the copy's 830 saves would carry a list, by attribute.

**Writer agents on the memory question set** (`memory-questions`, held-out projects ASH, HAL, KST, MRW, QFT).

- **Arms.** Both run Claude Sonnet subagents with `eval/tools/memory-questions/memory_writer.md`, reading the
  sessions in order with `next_session.py` and writing with `memory_op.py` into a scratch folder. **Today**: a
  `remember` answers as it always has. **Listed**: a `remember` that lands beside current facts with the same subject
  and attribute (ignoring case) also lists them (`memory_op.py --list-existing`), and the prompt's `remember` bullet
  gains the sentence the MCP description gains: "If the answer lists facts already current with the same subject and
  attribute, and you meant to replace one, correct it instead of leaving both."
- **Repetitions:** 2 per project and arm, 20 runs, at most 2 at a time. A run that breaks the no-look-ahead rule
  (`check_memory_ops.py`) is discarded and rerun once.
- **Right answer**, per held-out `current` and `as_of` question (79): the facts true on the asked date (for
  `current`, the last session's date) that name any value the asked setting ever had, read with `MemoryEval.Named`,
  together name exactly the expected value. A stale fact left current beside the right one makes the answer wrong.
- **Wrongly replaced fact:** a `correct` whose target names a value of setting S, and whose new value does not name
  S's value on the date the correction takes effect (its `validFrom`, or its session's date).
- **Q1.** Listed's right-answer rate is at least Today's minus 0.05 (means over the two repetitions).
- **Q2.** Listed's wrongly replaced facts, summed over projects and averaged over repetitions, are at most Today's
  plus 1.
- **Reported, not gated:** per project, how many saves got a list, how many of those were followed by a `correct`
  of a listed fact, and duplicates left current at the end.

## Baselines

Recorded on 2026-10-24 with the `hybrid` config (nomic-embed-text through Ollama, reranker off) from the commit
that adds this file. See `eval/baselines/2026-10-24.md`.
