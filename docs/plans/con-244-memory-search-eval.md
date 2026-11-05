# CON-244: does container memory make agent search better?

The eval decides whether container-level memory stays: keep it only if it clearly beats plain search, drop it
if it is the same or worse.

**Finding before any build:** search never reads memory. `Connapse.Search` has no reference to facts, and
`SearchOperations` mentions memory only to refuse searching a memory container. Memory can help search only when
an agent calls `memory_recall` and then searches with what it learned. The eval therefore compares two agents:

- **Off:** `search_knowledge` and `get_document` only, which is how an agent searches today.
- **On:** the same agent and tools, plus `memory_recall`.

The task set also carries a plain query and gold documents for each task, so a server-side memory-aware search
(option B, a separate ticket if wanted) can be scored on the same tasks later.

Research: `docs/research/container-memory-search-eval-2026-11-04.md`.

## Phases

1. **Research and plan.**
   - Goal: name the methods borrowed and fix the design.
   - Done when this file and the research report are committed.
2. **Dataset and pass bar.**
   - Goal: a seeded task set and a keep/drop bar committed before any scored run.
   - Done when the `memory-search` dataset, its manifest entry and the bar in `eval/pass-bars.md` are committed.
3. **Eval command.**
   - Goal: `memory-search` runs both arms with one command and reports paired statistics.
   - Done when the command builds and its unit tests pass.
4. **Checks and PR.**
   - Goal: a Release build with CI's flags, `dotnet format`, the Codex review, a PR into `main`, and the cost
     estimate sent to ClaudePM.
   - Done when the PR is open and ClaudePM has the estimate.
5. **Scored run.**
   - Goal: run the test split after ClaudePM's go and with an API key.
   - Done when the PR reports both scores, the CI, the minimum detectable effect, examples, and a one-line
     keep or drop verdict.

## Log

- **Phase 1:**
  - Wrote the research report and this plan.
  - Files: `docs/research/container-memory-search-eval-2026-11-04.md`, `docs/plans/con-244-memory-search-eval.md`.
  - Verified that search has no path to memory (grep of `Connapse.Search` and `SearchOperations`).
  - Next: the dataset generator.
