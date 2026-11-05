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
- **Phase 2:**
  - Generated 364 documents, 154 memory operations and 136 tasks (112 test, 24 dev); each memory kind is half
    documented, half undocumented. Wrote the KEEP / WORSE / NO BETTER bar before any scored run.
  - Files: `eval/tools/build_memory_search.py`, `eval/datasets/memory-search/*`, `eval/MANIFEST.json`, `eval/pass-bars.md`.
  - Verified: the generator's leak checks pass (no undocumented alias or codename in any document).
  - Next: the `memory-search` command and its adapter.
- **Phase 3:**
  - Added the `memory-search` command: a Claude tool loop over the real `McpTools` methods (Off: search_knowledge and get_document; On: adds memory_recall), scoring, the paired report and the KEEP/DROP gate. The model rejects a temperature, so pass-bars.md now says the 3 repetitions carry the variance (amended before any run).
  - Files: `tests/Connapse.Eval/Runs/MemorySearch/*`, `Runs/MemorySearchEval.cs`, `Datasets/MemorySearchAdapter.cs`, `Program.cs`, `Cli/CliArgs.cs`, `Statistics/PairedStats.cs`, the csproj (Anthropic 13.5.0), README, `eval/tools/run_light.sh`.
  - Verified: 34 new unit tests; all 3,167 eval unit tests pass.
  - Next: Release build, format, Codex review, PR.
- **Phase 4:**
  - Release build with CI flags and `dotnet format` were clean. Codex found two gate gaps (a partial run could pass; a failed index was ignored), both fixed in 7f566ac6 with 6 more tests. Opened draft PR #1130 into main.
  - Files: `Runs/MemorySearchEval.cs`, `MemorySearchTests.cs`, README.
  - Verified: 40 memory-search unit tests pass; Release build passes.
  - Next: the PM's go, an API key and the model, then the scored run.
