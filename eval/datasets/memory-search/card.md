# memory-search

- **What it tests:** whether a container's memory helps an agent find the right document in that container
  (CON-244). The same agent searches with and without `memory_recall`; everything else is fixed.
- **Built by** `eval/tools/build_memory_search.py` (seed 244). Every department, team, system, person and value is
  made up. The output is a pure function of the seed.
- **Corpus** (`corpus.jsonl`, 364 short documents): policies of 12 departments on 30 topics, team charters, system
  overviews and runbooks. Documents are shuffled, so upload order says nothing about which belong together.
- **Memory** (`memory.jsonl`, 154 operations): what agents stored about these documents earlier, replayed in order
  through the memory service. Each fact cites the document it came from. 24 of them are true but unrelated to any
  task (each team's lead and chat channel).
- **Tasks** (`tasks.jsonl`): each has a question, the gold document(s) to cite, labelled stale documents that must
  not be cited, the answer value, and the memory operations behind it.

  | Kind | What memory knows | Test | Dev |
  |---|---|---|---|
  | current | which of three revisions of a policy is in force | 16 | 4 |
  | alias | the official department behind a team nickname | 16 | 4 |
  | correction | that a policy was replaced by a renamed standard with different wording | 16 | 4 |
  | location | which codenamed runbook holds a system's procedure | 16 | 4 |
  | control | nothing relevant | 40 | 6 |
  | stale | an old value from a policy that a newer document explicitly replaces | 8 | 2 |

- **Documented and undocumented halves.** In half of each memory kind, the documents alone also hold what memory
  knows, so a careful agent can get there by searching more:
  - current: the revisions say which is in force, or nothing says so;
  - alias: the team charter names the nickname, or no document contains it;
  - correction: the new standard says what it replaces, or it does not;
  - location: the system overview names the codename, or no document does.

  The generator fails if an undocumented nickname or codename appears in any document, if a system's name appears
  in its runbooks, or if a corrected document uses the old wording the question uses.
- **Sets the pass bar uses** (`eval/pass-bars.md`, "Container memory for search"):
  - **M**, where memory should help: current, alias, correction and location, 64 test tasks.
  - **H**, where it should not hurt: control and stale, 48 test tasks.
- **Plain query for later.** Each question is also a plain search query, and gold documents are labelled, so a
  memory-aware search on the server can be scored on the same tasks without an agent.
- **Known limitations:**
  - The documents are short and templated, so search is easier than on real policies.
  - The undocumented halves can only be answered by guessing without memory. That is the case memory exists for,
    and results are reported for each half separately.
  - Memory here is clean apart from the stale tasks; real agent-written memory is messier.
- **Reproduce:** `python -I eval/tools/build_memory_search.py eval/datasets/memory-search`
