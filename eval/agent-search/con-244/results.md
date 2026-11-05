# CON-244 results: does container memory make agent search better?

**Verdict: KEEP.** This is the pre-registered run measured against the bar in `eval/pass-bars.md`, and it conforms to
that bar.

## The run

- **Data:** the 112 `test` tasks, 3 repetitions per arm, 672 agent runs in total.
- **Agent:** `claude -p` with claude-sonnet-5-5 at effort medium, on the Claude subscription. No API key or cloud-provider setting was passed.
- **Search:** the hybrid config, under `eval/tools/run_light.sh` with 1 worker. Search was locked at its default 10
  results in Hybrid mode: the agent was not offered `mode` or `topK`. The memory-off agent still sent `mode` in 10
  searches, because the tool description mentions modes. Those values were ignored, and the trace records what the
  agent sent.
- **When:** 2026-11-05, 05:06–06:47 UTC.
- **Exit code:** 0, a conforming run that passed the gate.
- **Usage:** the subscription's seven-day window went from 34% to 36%. At API list price the run would have cost $17.55.
- **Files:**
  - `memory-search-test.json` is the report.
  - `memory-search-test.runs.jsonl` holds every run with its tool calls, answer and citations.

An earlier run on 2026-11-05 didn't lock search: the agent could change mode and result count. It reached the same
verdict, with a gain of +0.396 on set M. The Codex adversarial review found the gap. This run replaces that one.

## Main metric: did the agent cite the current document and no superseded one?

| Set | Tasks | Memory off | Memory on | Difference | Interval | p | MDE |
|---|---|---|---|---|---|---|---|
| M: memory should help | 64 | 0.479 | 0.911 | **+0.432** | 95% [0.313, 0.542] | 0.0002 | 0.165 |
| H: memory must not hurt | 48 | 1.000 | 1.000 | 0.000 | 90% [0, 0] | 1 | 0 |
| All tasks | 112 | 0.702 | 0.949 | +0.247 | 95% [0.176, 0.327] | 0.0002 | 0.110 |

Both KEEP conditions hold:
- On M, the gain is at least 0.15, its 95% interval lies above 0, and p < 0.05.
- On H, the lower end of the 90% interval is above −0.10.

MDE (minimum detectable effect) is the smallest gain this run could reliably detect.

## Where the gain comes from

- **Undocumented facts** (memory knows something no document says): 0.09 without memory, 0.86 with it.
- **Documented facts:** 0.86 without memory, 0.96 with it.

Score by kind of task, without memory and then with it:

| Kind | Memory off | Memory on |
|---|---|---|
| alias | 0.50 | 1.00 |
| location | 0.63 | 1.00 |
| current | 0.50 | 0.94 |
| correction | 0.29 | 0.71 |
| control | 1.00 | 1.00 |
| stale | 1.00 | 1.00 |

## Searches and other measures (reported, not gated)

| Measure | Difference (on − off) | 95% interval |
|---|---|---|
| Search calls per run | −0.62 (1.91 → 1.29) | [−0.97, −0.36] |
| Tool calls per run, counting `memory_recall` | +0.55 | [0.18, 0.84] |
| Stale citations | −0.12 | [−0.19, −0.07] |
| Answer accuracy | +0.05 | [0.01, 0.10] |
| Run recall of gold documents | +0.06 | [0.02, 0.11] |
| First-query MRR@10 | −0.002 | [−0.009, 0.001] |

The first search ranks gold documents the same in both arms (first-query MRR@10). The gain comes from what the agent searches for after it recalls memory. Without memory, the agent searched about 4 times per alias or location task. With memory, it searched under 2 times.

Outcomes: one run without memory ended with no answer. Every other run in both arms answered.

## Examples

**Memory made results better** in 3 alias tasks: test-alias-04, -06 and -07.
- Without memory, the agent could not map a team nickname such as "Bluebird" or "Northstar" to its department. It gave up, or listed every department's figure.
- With memory, it recalled the nickname note and cited the right policy.
- In these runs it said that the team charter doesn't confirm the nickname. That caution is correct, since the alias is undocumented by design.

**Memory made results worse:** in no task did memory score lower than search alone.

**Where memory still scored 0:** 17 of the 336 memory runs, across 8 tasks (14 runs on correction tasks, 3 on current tasks).
- 14 of those runs cited more than one document. As in the earlier run, the agent named the current value and cited both the
  current document and the superseded one it replaced, to explain what had changed.
- The pre-registered rule scores any stale citation as 0, so those runs count as misses. That bias works against the memory arm, so it doesn't weaken the KEEP.

## Caveats

- **The dataset is synthetic.** Memory notes were written with the documents they cite. Real agent memory is noisier, and could hold wrong notes.
- **One way memory reaches the agent:** the agent calls `memory_recall` itself. Search never reads memory, so this run does not measure memory wired into search.
