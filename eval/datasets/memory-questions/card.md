# memory-questions

- **What it tests:** whether an agent memory recalls project facts that change over time:
  - the value now
  - the value on a past date
  - what changed in a window
  - which ticket made a change, and what a ticket changed
  - when a change happened

  It is the proof set for agent memory (CON-39), which has to beat plain notes, above all on facts that change.
- **Histories** (`make_histories.py`, seed 38): ten fictional projects, 2026-01-05 to 2026-06-26.
  - **Facts:** each project has eight: database, deploy region, tech lead, CI system, cache, public API rate limit, alert channel and release.
  - **Changes:** 141 in all, each on a known date and through a ticket. 30 are announced by a plan days before they happen, about a quarter go back to an earlier value, and 80 unrelated events fill the gaps.
  - **Sessions:** the events of one day make one work session, 246 in all.
  - **Answer key:** `timeline.json`, every value with the date and ticket it took effect. It was committed before any note existed (first commit of the PR).
  - **Fictional names only:** every project, person and channel is made up.
- **Notes** (`notes/<CODE>/<session>.md`, collected in `notes.jsonl`): 246 agent-style memory notes, one per session, with a median of 20 words.
  - **Writers:** ten subagents (Claude Sonnet) of the session that built the set, one per project. No paid API was used.
  - **No look-ahead:** each writer fetched its sessions one at a time through `next_session.py`, wrote a note before asking for the next, and could re-read only its own earlier notes.
  - **Checked:** `build_questions.py` fails if a note names a value or ticket before its history introduced it, or names a value its project never had. Both checks pass.
- **Questions** (`questions.jsonl`, 552): current 79, as_of 78, changed 20, ticket 117, ticket_effect 141, when 117.
  - **Keys:** every answer comes from the timeline.
  - **Evidence:** a question is kept only if some note holds its answer, at or before the date it asks about, and those notes are listed as its `evidence`. For a ticket or a date, only notes dated on or after the change count, so a plan note naming the ticket and the value before the change doesn't. In a changed question, each change lists its own evidence. 3 questions were dropped because a writer reworded a value (`Cache: none` for `no cache`).
  - **Repeated values:** a value a fact took twice isn't asked about by value, because it would have two answers.
  - **Questions about values:**

    | Kind | Example |
    |---|---|
    | current | "What is Halcyon's database now?" |
    | as_of | "What was Emberline's alert channel on 2026-04-05?" |
    | changed | "What changed in Marrowgate's setup between 2026-04-08 and 2026-05-03?" (some windows hold no change) |

  - **Questions about tickets and dates:**

    | Kind | Example |
    |---|---|
    | ticket | "Which ticket changed Quillfeather's database to PostgreSQL 15?" |
    | ticket_effect | "What did HAL-17 change in Halcyon?" |
    | when | "When did Ashgrove's CI system change to Buildkite?" |

- **Split, fixed before any run:** by project, so no project's notes serve both halves. The five projects that sort first by SHA-256(`38:CODE`) are held out: ASH, HAL, KST, MRW and QFT, 287 questions. The other five, 265 questions, are for tuning.
- **Public check** (`longmemeval.json`): LongMemEval-cleaned (Wu et al., 2024; MIT) at commit `98d7416c`. Its files are pinned by SHA-256, and nothing from them is committed here.
  - **Questions used:** the 78 knowledge-update and 133 temporal-reasoning questions, the two kinds closest to this set's own.
  - **Split:** each question's half is set by SHA-256(`38:question_id`): 97 tuning, 114 held-out.
  - **Files:** `longmemeval_oracle.json` lists the questions, and `longmemeval_s_cleaned.json` (277 MB) adds each question's haystack of about 50 sessions.
- **Plain-notes baseline** (`baseline.py`, `baseline.json`). The question's project's notes are ranked by BM25 against the question text, the way a project's notes folder and a keyword search work: nothing knows which note is newer or which fact replaced which.
  - **hit@k:** an evidence note is in the top k. For a changed question, every change in the window needs one of its own notes in the top k. A window with no changes isn't scored.
  - **answer:** the value read from the first-ranked note that names any value of the asked fact, taking the last value it names (in "from A to B", B). For a "when" question it is that note's date, and for a "ticket" question the first ticket id in it.
  - **Held-out results:**

    | Kind | Questions | hit@1 | hit@5 | answer |
    |---|---|---|---|---|
    | all | 287 | 0.589 | 0.955 | 0.722 |
    | current | 40 | 0.400 | 1.000 | 0.425 |
    | as_of | 39 | 0.462 | 0.949 | 0.564 |
    | changed (every change found) | 10 | 0.000 | 0.000 | — |
    | ticket | 63 | 0.857 | 1.000 | 0.857 |
    | ticket_effect | 72 | 0.375 | 0.986 | — |
    | when | 63 | 0.857 | 1.000 | 0.873 |
    | LongMemEval knowledge-update | 33 | 0.970 | 1.000 | — |
    | LongMemEval temporal-reasoning | 81 | 0.802 | 0.938 | — |

  - **Facts that change are where plain notes fail.** A ticket or a value names its own note, so search finds it (0.86–0.87). But the note that ranks highest for "Halcyon's database" is often an older one naming an older value, so plain notes get 0.425 of current values and 0.564 of past values right. No window of changes is fully covered.
  - **LongMemEval measures something else:** its answer sessions are easy for BM25 to find. What the benchmark tests is reading them, which needs a model to answer and is not scored here.
- **Reproduce:**

  ```
  python -I eval/tools/memory-questions/make_histories.py eval/datasets/memory-questions
  # notes: one writer per project, prompted as in the PR, fetching sessions with next_session.py <CODE> <index>
  python -I eval/tools/memory-questions/build_questions.py eval/datasets/memory-questions
  python -I eval/tools/memory-questions/select_longmemeval.py <longmemeval_oracle.json> eval/datasets/memory-questions/longmemeval.json
  python -I eval/tools/memory-questions/baseline.py eval/datasets/memory-questions <longmemeval_s_cleaned.json> eval/datasets/memory-questions/baseline.json
  ```

- **Agent memory (CON-111):** suite `memory-v1` in `MANIFEST.json`, which pins every file below. It is scored by the harness's `memory` command, not by `run`.
  - **Writers:** ten Claude Sonnet subagents, one per project, given `memory_writer.md`.
    - **Same sessions as the notes:** each writer read its project's sessions in order through `next_session.py`, and recorded each with `memory_op.py` (remember, correct or show) before fetching the next.
    - **Operations:** 221 in all, in `memory-ops/<CODE>.jsonl`.
    - **One rerun:** the first Lanternfish writer read a session before recording the previous one, so its operations were discarded and the project rewritten.
    - **One harmless slip in the rerun:** it sent one fetch in the same step as the previous session's record, which had already been written.
  - **Checked:** `check_memory_ops.py` fails if an operation names a value or ticket that its project's sessions had not shown by then, or if operations come out of session order. It passes for all ten.
  - **How it is run:** one Connapse instance, one container per project.
    - **Notes side:** the project's notes are stored as plain documents and found with hybrid search. This is the baseline the pass bar names.
    - **Memory side:** the writers' operations are replayed through the memory service into the same container. Each fact cites its session's note document.
    - **Questions:** each kind is asked the way an agent would ask it: now, as of the question's date, the question's window, or the whole history for ticket and date questions.
  - **Scoring:** both sides are scored the same way.
    - **Recall@5:** an evidence note is among the top five notes, or among the notes the top five facts cite. For a window, every change needs its own evidence note.
    - **Answer:** read as in `baseline.py`, from the first note or fact that names a value of the asked fact.
    - **Significance:** a paired permutation test with Holm correction.
  - **Held-out result** (`eval/baselines/memory-v1-heldout-2026-10-27.json`, run once under the light limits):

    | Kind | Questions | Notes Recall@5 | Memory Recall@5 | Notes answer | Memory answer |
    |---|---|---|---|---|---|
    | current | 40 | 1.000 | 1.000 | 0.425 | 1.000 |
    | as_of | 39 | 1.000 | 1.000 | 0.641 | 1.000 |
    | changed | 10 | 0.000 | 1.000 | — | — |
    | ticket | 63 | 1.000 | 1.000 | 0.921 | 1.000 |
    | when | 63 | 1.000 | 1.000 | 0.937 | 1.000 |
    | ticket_effect | 72 | 0.764 | 1.000 | — | — |

    - **Bar, changed-fact questions** (current, as_of and changed together): +0.112 Recall@5. The 95% interval is 0.056 to 0.191, Holm p = 0.002, and the minimum detectable effect is 0.094.
    - **Ordinary questions:** +0.086, Holm p < 0.001, so no drop.
    - **Writes:** median 5.7 ms, at most 119 ms. Recall p95 is 6.2 ms.
    - **AI tokens:** memory calls no model. The only model in the run is the notes side's embedder.
  - **Recall@5 hides most of the gain.** Hybrid search puts some note holding the current value in the top five almost every time, often below an older one. The difference is in the answer read from the first result: 0.425 against 1.000 for current values.
  - **Tuning split** (`memory-v1-tuning-2026-10-27.json`): +0.080 Recall@5 on changed-fact questions.
    - **Six-change windows:** two tuning windows have six changes, and no top five can cover six.
    - **Held-out windows:** they have at most five changes each.
    - **Ranking fix:** this split showed that ticket questions were drowned by the project name, which every fact contains. That was fixed before the held-out run.
- **Known limitations:**
  - **Notes paraphrase little.** The writers mostly restate each session's log, so a real agent's notes would be messier and omit more.
  - **Only one note per session,** and no edits to earlier notes. A memory that rewrites old notes is outside what these notes show.
  - **Evidence is an exact-string match.** A note that states a value in other words doesn't count as evidence, and its question is dropped instead.
  - **"changed" questions have no plain-notes answer score.** They are scored only by whether the notes for each change are retrieved.
