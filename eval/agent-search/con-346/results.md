# CON-346 results: server-side tool search

Scored on the `test` split on 2026-11-12 and 2026-11-13, against the bars in `eval/pass-bars.md` ("Server-side tool
search"). Every arm ran `claude-sonnet-5-5` at medium effort, 3 repetitions, at most 12 tool calls, 2 workers, through
`claude -p` on the user's Claude subscription (no API key; token counts are the CLI's own). *Full* is every tool at
`/mcp/full`; *compact* is the eight-tool list at `/mcp`. "Tool search" is Claude Code's `ENABLE_TOOL_SEARCH`.

**Verdict: fails bar 3.** With tool search on, the compact list's `memory-search` score is not non-inferior within
0.05. Every other gate measured here passes.

| Suite | Tool search | Full | Compact | Compact − full, 90% BCa | Non-inferior | Median input tokens (full / compact) |
|---|---|---|---|---|---|---|
| tool-routing (60 tasks) | off | 1.000 | 1.000 | 0 [0, 0] | yes | 34,843 / 22,837 (0.655) |
| tool-routing | on | 1.000 | 1.000 | 0 [0, 0] | yes | 14,175 / 19,106 (1.348) |
| memory-search (112 tasks) | off | 0.6905 | 0.7024 | +0.012 [−0.009, +0.036] | yes | 37,620 / 16,450 (0.437) |
| memory-search | on | 0.6994 | 0.6696 | −0.030 [−0.060, −0.015] | **no** | 17,241 / 19,170 (1.112) |

- **Bar 1 (tool tokens):** compact list plus instructions is under 30% of the full list by `o200k_base`
  (`McpToolListTokenTests`). The Anthropic token-counting half was not run: no API key was used, by the user's choice.
- **Bar 2 (input tokens, tool search off):** 0.655, under 0.80. Passes.
- **Bar 3 (right answers):** routing passes in both modes; memory-search passes with tool search off and fails with
  it on.
- **Validity:** every tool-search-on arm called ToolSearch (routing full 1.18 and compact 0.10 calls per run;
  memory-search full 1.01 and compact 1.02), so no arm is invalid. No provider errors; 3 runs ran out of calls.

## The fix made after a test-split run

The first compact routing arm with tool search off scored 0.9611 (−0.039, 90% BCa [−0.106, −0.011]); the agent
answered three code-symbol tasks with `search_knowledge` instead of finding `code_find_symbol`. Commit 791f3c1d then
told the agent to prefer a fitting hidden tool to `search_knowledge`. That fix was written after seeing test-split
failures; the dev split could not check it (dev was already at 1.0 before and after). The routing rows above are the
post-fix runs on the same test split, so they are not a clean held-out result. Pre-fix files are in
`results/pre-fix/`.

## Why memory-search fails with tool search on

The compact list barely changes what the agent does on this suite. In all four arms it answers with the visible
`search_knowledge` (605 to 620 calls per arm) and almost never reaches a hidden tool (compact, tool search on: 8
`find_tools` calls in 336 runs). The difference comes from the correction, alias and location kinds, which move by
similar amounts in the opposite direction with tool search off (correction: full 0.19, compact 0.31). That looks like
run-to-run variation the 3-repetition design can't separate from a 0.05 margin, but the bar counts an interval that
reaches −0.05 as a failure "whether the change is worse or the run is too small to tell", and a failed change is
redesigned and judged on a fresh held-out set, not rerun.

## Files

`results/<suite>-<list>-<search>.json` and `.runs.jsonl` per arm; `results/arms-<suite>-<search>.json` per paired
comparison; `results/dev-tool-routing-compact-off.json` for the dev check; `results/pre-fix/` for the first compact
routing arm.
