# memory-learned

- **What it tests:** whether memory that an agent writes for itself while answering questions in a container makes
  later, related questions in that container more reliable (CON-244, realistic arm). Nothing is written into memory
  up front, and every fact a task needs is in some document.
- **Built by** `eval/tools/build_memory_learned.py` (seed 244), which reuses the names, topics and wording of
  `build_memory_search.py`. Everything is made up, and the output is a pure function of the seed.
- **Corpus** (`corpus.jsonl`, 387 documents): department policies, policy revisions, a policy register and a team
  charter per department, system overviews and codenamed runbooks. **Late documents** (`late.jsonl`, 14) are uploaded
  between the two sessions; each replaces an older policy and says so.
- **Tasks** (`tasks.jsonl`): every task has `family` and `session` (1 or 2). A family's session-1 question and its
  session-2 question are different but need the same thing to be worked out first, except in stale families, which
  ask the same question twice so the late document changes the answer.

  | Kind | What session 1 has to work out | Session 2 asks | Test families | Dev families |
  |---|---|---|---|---|
  | current | which of three two-topic revisions the department's policy register puts in force | the other topic of that policy | 16 | 3 |
  | alias | which department a team nickname means (only its charter says) | another policy of the same team | 16 | 3 |
  | correction | that a renamed standard, worded differently, replaced the policy the question names | the standard's other topic | 16 | 3 |
  | location | the codename a system's runbooks use (only its overview says) | a different procedure on the same system | 16 | 3 |
  | stale | a policy value, which a late document then replaces | the same question; the old policy is labelled stale | 12 | 2 |
  | control | (no session-1 question) | a policy nothing in session 1 touched | 28 | 4 |

- **Checks in the generator:** a nickname appears only in its charter; a codename only in its overview and runbooks;
  a system's name only in its overview; a standard never uses the old wording its question uses; every gold document
  holds the answer; no session-1 task cites a late document.
- **Sets the pass bar uses** (`eval/pass-bars.md`, "Learned container memory"), on session-2 test tasks only:
  **M** is current, alias, correction and location (64); **H** is control and stale (40).
- **Run order:** split, then session, then a seeded shuffle, so session 1 mixes kinds as real questions would.
- **Known limitations:** documents are short and templated; session 1 is told to save what it learns, which a real
  deployment would do through its own instructions; one corpus holds both splits.
- **Reproduce:** `python -I eval/tools/build_memory_learned.py eval/datasets/memory-learned`
