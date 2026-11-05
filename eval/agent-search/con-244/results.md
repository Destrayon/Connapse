# CON-244 results: does container memory make agent search better?

Two arms, each a pre-registered run measured against its own bar in `eval/pass-bars.md`, and each conforming to that bar:

- **Planted memory: KEEP.** The memory was written for the test and held facts no document states. This is the arm the sections below describe.
- **Memory the agents wrote themselves (realistic arm): WORSE, so DROP.** Agents in session 1 searched the documents and saved what they learned. Fresh agents in session 2 did worse with that memory than without it. See [the realistic arm](#realistic-arm-memory-the-agents-wrote-themselves) at the end.

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
| Saw a gold document in any tool output | +0.06 | [0.02, 0.11] |
| First-query MRR@10 | −0.002 | [−0.009, 0.001] |

"Saw a gold document" counts every tool output, `memory_recall` included, so a memory that cites a gold document
counts as seeing it. It is not run recall over search results alone, which this run's traces cannot recompute.

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

## Realistic arm: memory the agents wrote themselves

This arm tests the use the review asked about: an agent searches the documents, saves memories about what it read, and a
later agent answering a related question uses those memories.

**Verdict: WORSE, so DROP.** Memory lowered the score on set M, the tasks where memory should help, and the 95% interval lies entirely below 0.

### The run

- **Data:** `eval/datasets/memory-learned`, test split. Each task family has a session-1 question and a related session-2 question on the same documents.
  - Session 1 has 76 questions. Session 2 has 104, which is 64 in set M and 40 in set H, the tasks where memory must not hurt.
  - Session 2 also covers documents uploaded after session 1, so some of what session 1 saved is out of date.
- **Each repetition:**
  1. Index a fresh container and create a new user.
  2. Session-1 agents answer their questions with `memory_remember` and `memory_correct` available, and are told to save what they learn.
  3. Upload the late documents.
  4. Session-2 agents answer with memory on and with memory off. The session-2 prompt doesn't mention memory, the same as in the planted arm.
- **Size:** 3 repetitions, so 228 session-1 runs and 624 session-2 runs. Only session 2 is scored.
- **Setup:** the same agent, search lock, metric and thresholds as the planted arm.
- **When:** 2026-11-05, 07:43–10:28 UTC.
- **Exit code:** 3, a conforming run that failed the gate.
- **Usage:** the subscription's seven-day window went from 37% to 39%. At API list price the run would have cost $26.36.
- **Files:** `memory-learned-test.json` is the report, and `memory-learned-test.runs.jsonl` holds every run, including session 1.

Every session-1 run called a memory write tool at least once, and every call cited source documents. This run's
traces don't record whether each call was saved. Session-1 agents scored 0.706 on their own questions. That is a
citation score, so it doesn't say how many of the saved memories were wrong.

### Main metric

| Set | Tasks | Memory off | Memory on | Difference | Interval | p | MDE |
|---|---|---|---|---|---|---|---|
| M: memory should help | 64 | 0.708 | 0.599 | **−0.109** | 95% [−0.203, −0.047] | 0.009 | 0.110 |
| H: memory must not hurt | 40 | 0.950 | 0.933 | −0.017 | 90% [−0.042, 0] | 0.49 | 0.033 |
| All tasks | 104 | 0.801 | 0.728 | −0.074 | 95% [−0.135, −0.032] | 0.003 | 0.070 |

H passes its no-harm bound, but M is significantly worse, so the verdict is WORSE.

| Kind | Memory off | Memory on |
|---|---|---|
| alias | 1.00 | 1.00 |
| location | 1.00 | 1.00 |
| current | 0.27 | 0.25 |
| correction | 0.56 | 0.15 |
| control | 1.00 | 1.00 |
| stale | 0.83 | 0.78 |

### Why memory made things worse

Almost all of the loss is on correction tasks, where a renamed standard replaced a 2023 policy.
- **What session 1 saved:** a fact that gave the new value and cited both the new standard and the old policy, to record what had changed.
- **What session 2 did:** the agent with memory recalled that fact and cited its sources. 41 of the 48 correction runs with memory cited the replaced policy, against 21 of 48 without memory.
- **How it scored:** the answers were usually right, but the pre-registered rule scores any stale citation as 0.

In test-correction-07, for example, both arms answered "$242,500" in all 3 repetitions. Only the arm with memory cited the old policy.

Stale citations rose by 0.074 per task (95% [0.035, 0.135]). Answer accuracy didn't change significantly (−0.016, 95% [−0.042, 0]).

On current tasks, the right answer is the revision the policy register names, which is not always the newest. Memory
helped only when the session-1 agent had found the register. In test-current-07 and -16, memory turned a wrong
"latest revision" answer into the right one. Session-1 agents usually saved the newest revision instead, though, and
session 2 then repeated that mistake.

Alias and location tasks scored 1.00 in both arms. Unlike the planted arm, every fact here is in the documents, and
search alone found it.

### What this means

On this dataset, memory that agents wrote while searching did not make later searches more reliable. The agents saved
what they concluded, mistakes and supporting citations included, and the next agent trusted it. Search alone already
found every documented fact. The planted arm's gain came from facts that no document states.

This dataset is synthetic, and the realistic arm tests only one way of saving: the agent decides what to remember, using
the current tools. A memory that saved only the cited current document, or that search checked against the documents,
might behave differently. This run doesn't measure either.
