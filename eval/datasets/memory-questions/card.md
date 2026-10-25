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
  - **Evidence:** a question is kept only if some note holds its answer, at or before the date it asks about, and those notes are listed as its `evidence`. 3 questions were dropped because a writer reworded a value (`Cache: none` for `no cache`).
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
- **Plain-notes baseline** (`baseline.py`, `baseline.json`). All 246 notes are ranked by BM25 against the question text, the way a folder of notes and a keyword search work: nothing knows which note is newer or which fact replaced which.
  - **hit@k:** an evidence note is in the top k.
  - **answer:** the value read from the first-ranked note that names any value of the asked fact, taking the last value it names (in "from A to B", B). For a "when" question it is that note's date, and for a "ticket" question the first ticket id in it.
  - **Held-out results:**

    | Kind | Questions | hit@1 | hit@5 | answer |
    |---|---|---|---|---|
    | all | 287 | 0.244 | 0.679 | 0.439 |
    | current | 40 | 0.050 | 0.225 | 0.325 |
    | as_of | 39 | 0.077 | 0.333 | 0.436 |
    | changed | 10 | 0.200 | 0.200 | — |
    | ticket | 63 | 0.413 | 0.825 | 0.476 |
    | ticket_effect | 72 | 0.167 | 0.944 | — |
    | when | 63 | 0.397 | 0.809 | 0.476 |
    | LongMemEval knowledge-update | 33 | 0.970 | 1.000 | — |
    | LongMemEval temporal-reasoning | 81 | 0.802 | 0.938 | — |

  - **Current and as_of are where plain notes fail:** the note that ranks highest for "Halcyon's database" is usually an older one naming an older value. A plain-notes reader gets 1 in 3 current values right.
  - **LongMemEval measures something else:** its answer sessions are easy for BM25 to find. What the benchmark tests is reading them, which needs a model to answer and is not scored here.
- **Reproduce:**

  ```
  python -I eval/tools/memory-questions/make_histories.py eval/datasets/memory-questions
  # notes: one writer per project, prompted as in the PR, fetching sessions with next_session.py <CODE> <index>
  python -I eval/tools/memory-questions/build_questions.py eval/datasets/memory-questions
  python -I eval/tools/memory-questions/select_longmemeval.py <longmemeval_oracle.json> eval/datasets/memory-questions/longmemeval.json
  python -I eval/tools/memory-questions/baseline.py eval/datasets/memory-questions <longmemeval_s_cleaned.json> eval/datasets/memory-questions/baseline.json
  ```

- **Not in `MANIFEST.json` yet:** the harness has no memory adapter, so the memory PR (CON-39) adds the adapter and the manifest entry.
- **Known limitations:**
  - **Notes paraphrase little.** The writers mostly restate each session's log, so a real agent's notes would be messier and omit more.
  - **Only one note per session,** and no edits to earlier notes. A memory that rewrites old notes is outside what these notes show.
  - **Evidence is an exact-string match.** A note that states a value in other words doesn't count as evidence, and its question is dropped instead.
  - **"changed" questions have no plain-notes answer score.** They are scored only by whether the notes for each change are retrieved.
