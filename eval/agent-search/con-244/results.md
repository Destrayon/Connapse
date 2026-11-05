# CON-244 results: does container memory make agent search better?

**Verdict: KEEP.** This is the pre-registered run measured against the bar in `eval/pass-bars.md`.

## The run

- **Data:** the 112 `test` tasks, 3 repetitions per arm, 672 agent runs in total.
- **Agent:** `claude -p` with claude-sonnet-5-5 at effort medium, on the Claude subscription. No API key was set.
- **Search:** the hybrid config, under `eval/tools/run_light.sh` with 1 worker.
- **When:** 2026-11-05, 03:04–04:29 UTC.
- **Exit code:** 0, a conforming run that passed the gate.
- **Usage:** the subscription's seven-day window went from 32% to 34%. At API list price the run would have cost $17.37.
- **Files:**
  - `memory-search-test.json` is the report.
  - `memory-search-test.runs.jsonl` holds every run with its tool calls, answer and citations.

## Main metric: did the agent cite the current document and no superseded one?

| Set | Tasks | Memory off | Memory on | Difference | Interval | p | MDE |
|---|---|---|---|---|---|---|---|
| M: memory should help | 64 | 0.490 | 0.885 | **+0.396** | 95% [0.287, 0.510] | 0.0002 | 0.160 |
| H: memory must not hurt | 48 | 1.000 | 1.000 | 0.000 | 90% [0, 0] | 1 | 0 |
| All tasks | 112 | 0.708 | 0.935 | +0.226 | 95% [0.161, 0.307] | 0.0002 | 0.105 |

Both KEEP conditions hold:
- On M, the gain is at least 0.15, its 95% interval lies above 0, and p < 0.05.
- On H, the lower end of the 90% interval is above −0.10.

MDE (minimum detectable effect) is the smallest gain this run could reliably detect.

## Where the gain comes from

- **Undocumented facts** (memory knows something no document says): 0.10 without memory, 0.83 with it.
- **Documented facts:** 0.88 without memory, 0.94 with it.

Score by kind of task, without memory and then with it:

| Kind | Memory off | Memory on |
|---|---|---|
| alias | 0.50 | 1.00 |
| location | 0.65 | 1.00 |
| current | 0.50 | 0.90 |
| correction | 0.31 | 0.65 |
| control | 1.00 | 1.00 |
| stale | 1.00 | 1.00 |

## Searches and other measures (reported, not gated)

| Measure | Difference (on − off) | 95% interval |
|---|---|---|
| Search calls per run | −0.49 (1.80 → 1.31) | [−0.76, −0.29] |
| Tool calls per run, counting `memory_recall` | +0.62 | [0.35, 0.84] |
| Stale citations | −0.10 | [−0.16, −0.06] |
| Answer accuracy | +0.04 | [0.01, 0.10] |
| Run recall of gold documents | +0.04 | [0.01, 0.09] |
| First-query MRR@10 | −0.003 | [−0.022, 0.002] |

The first search ranks gold documents the same in both arms (first-query MRR@10). The gain comes from what the agent searches for after it recalls memory.

Outcomes: one run without memory ended with no answer. Every other run in both arms answered.

## Examples

**Memory made results better** in 3 alias tasks: test-alias-04, -06 and -07.
- Without memory, the agent could not map a team nickname such as "Northstar" or "Voyagers" to its department. It gave up, or listed every department's figure.
- With memory, it recalled the nickname note and cited the right policy.
- In all three, it flagged that the team charter doesn't confirm the nickname. That caution is correct, since the alias is undocumented by design.

**Memory made results worse:** in no task did memory score lower than search alone.

**Where memory still scored 0:** 22 of the 336 memory runs, across 10 correction and current tasks.
- 13 of those runs cited more than one document. In the two examined closely, test-correction-01 and -04, the agent named the current value and the document that replaced the old one. It also cited the superseded document, to explain what had changed.
- The pre-registered rule scores any stale citation as 0, so those runs count as misses. That bias works against the memory arm, so it doesn't weaken the KEEP.
- The other 9 runs cited a single document that was not the current one.

## Caveats

- **The dataset is synthetic.** Memory notes were written with the documents they cite. Real agent memory is noisier, and could hold wrong notes.
- **One way memory reaches the agent:** the agent calls `memory_recall` itself. Search never reads memory, so this run does not measure memory wired into search.
