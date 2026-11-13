# tool-routing

- **What it tests:** whether an agent still reaches the one Connapse tool a task needs when the server lists 8 tools
  and hides the rest behind `find_tools` and the call tools (CON-346). The same tasks run against the full list
  (`/mcp/full`) and the compact list (`/mcp`), each with Claude Code's own tool search off and on.
- **Built by** `eval/tools/build_tool_routing.py`, hand-written and deterministic. Every policy, project, person and
  line of code is made up.
- **Fixtures** (`fixtures.json`): 8 markdown documents for the documents container, a small Python billing package
  and a C# shop service for the code container, and dated facts for a team memory space. The command indexes and
  seeds them once per run in a throwaway Connapse.
- **Tasks** (`tasks.jsonl`): each names a gold tool, a prompt that never names a tool, setup calls that create what
  the task acts on, and the argument values a correct call holds. `{u}` is replaced per run with a fresh id, so runs
  never share the files, containers or facts they change.

  | Group | Gold tools | Test | Dev |
  |---|---|---|---|
  | document | container create/delete/stats, upload, bulk upload, append, delete, bulk delete | 20 | 4 |
  | code | code_find_symbol, code_references, code_impact, code_repo_map (5 each) | 20 | 4 |
  | memory | memory spaces, space create, remember, recall, timeline, correct, forget | 20 | 4 |

- **Success:** the gold tool ran within the 12-call cap, directly or through a call tool, without an error, and with
  every expected argument value (case ignored). The rules are in `eval/pass-bars.md`, "Server-side tool search".
- **Known limitations:**
  - The core tools (`container_list`, `search_knowledge`, `list_files`, `get_document`) are listed in both arms, so
    no task has them as its gold tool; they would not separate the arms.
  - `connapse_api_*` and `approval_status` have no tasks: an admin JWT is never held for approval, and the admin API
    is a fallback, not a routing target.
  - Prompts are one sentence each and plainly worded; real requests are vaguer.
- **Reproduce:** `python -I eval/tools/build_tool_routing.py eval/datasets/tool-routing`
