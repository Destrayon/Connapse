# Memory search saying when a fact isn't stored: verdict (CON-337)

**Verdict: fail.** Gates 1, 2, 3 and 6 pass; gates 4 and 5 fail. The "Partial match" note fixes absent questions completely (1.000, against ripgrep's 0.896), but it fires on almost every search. That makes agents refuse questions whose answer is stored under different words, and it raises cost on every kind of question. The pass bar is in `eval/pass-bars.md` ("Saying when a fact isn't stored (CON-337)") and was committed before the run.

## Runs

- **When and how:** all runs were made on 2026-11-11 with claude-sonnet-5-5 at medium effort, 3 repetitions, 2 workers, and containers capped at 2 CPUs and 4 GB.
- **Arms:**
  - The change ran every task of `memory-agent` (test split), `-sealed-1`, `-sealed-2` and `-sealed-3`.
  - Ripgrep ran the absent tasks of the same sets in the same window.
- **The sealed-3 rerun:** sealed-3's first change run lost 79 of 236 runs because the agent could not start (Windows exit 0xC0000142 while the PC was short on resources), so it was thrown away. Sealed-3 was rerun alone, both arms, at 23:06–23:27, and every final run completed without an error.
- **Baseline:** CON-322's recorded final runs (`change5`).
- **Summaries:** the per-run summaries are in `eval/baselines/memory-agent-*-con337-{change,ripgrep}-2026-11-11.json`.

## Gates

| # | Gate | Result | Numbers |
|---|------|--------|---------|
| 1 | Absent, pooled: at least re-run ripgrep and at least 0.906 | **Pass** | 1.000 vs ripgrep 0.896 (96 runs each) |
| 2 | Absent, sealed-3: at least re-run ripgrep | **Pass** | 1.000 vs 0.833 |
| 3 | Absent, pooled: calls and cost at most ripgrep's | **Pass** | 1.32 vs 2.04 calls; $0.0083 vs $0.0101 |
| 4 | Every other kind vs CON-322: score ≥ −0.03, calls ≤ +0.1, cost ≤ +10% | **Fail** | partial score 0.913 vs 1.000 (−0.087); cost: partial +16.7%, reworded +10.4%, duplicate +10.2% |
| 5 | Paired by task: 90% BCa lower end > −0.03; find cost ≤ +5% | **Fail** | find −0.0148, CI [−0.0346, −0.0012]; save −0.0069, CI [−0.0278, 0.0000]; find cost +10.7% |
| 6 | Memory integration tests pass | **Pass** | 124 of 124 |

The pass bar's "302 other find tasks" counted the 32 absent tasks by mistake. The other find tasks number 270, and gate 5 was computed on those 270.

Every other kind compared with CON-322 (score, calls, cost per run):

| Kind | CON-322 | Change |
|------|---------|--------|
| exact | 1.000, 1.00, $0.0051 | 1.000, 1.00, $0.0056 (+9.9%) |
| reworded | 0.953, 1.06, $0.0068 | 0.942, 1.06, $0.0075 |
| indirect | 0.937, 1.31, $0.0084 | 0.957, 1.28, $0.0091 |
| partial | 1.000, 1.12, $0.0071 | 0.913, 1.21, $0.0082 |
| new | 1.000, 2.06, $0.0116 | 1.000, 2.03, $0.0122 |
| update | 1.000, 2.00, $0.0119 | 1.000, 2.00, $0.0122 |
| duplicate | 0.986, 0.97, $0.0063 | 0.958, 1.00, $0.0069 |

## Why it failed

- **The note fires almost every time.** It showed up in:

  | Kind | Runs with the note |
  |------|--------------------|
  | reworded | 357 of 363 |
  | exact | 44 of 102 |
  | partial | 138 of 138 |
  | absent | 96 of 96 |

  Agents pad their searches with synonyms, for example `build image base image pipelines docker img` or `cold-room watcher readings database db`. No single fact holds every one of those words, so "no single fact holds every word" is true even when the answer is shown.
- **On partial questions it changes the answer.** A partial question names its subject in other words: "cold-room watcher" means Gannet, the freezer monitor. The agent found the right fact and then said memory doesn't hold it, and cited nothing:
  - sealed-1 `find-p12`: all 3 runs.
  - sealed-2 `find-p08`: 2 runs.
  - sealed-2 `find-p09`: 2 runs.
- **Cost rises on every kind.** The note's text adds cache writes (exact questions: 632 against 503 tokens per run). Answers that hedge are longer (reworded questions: 346 against 308 output tokens). Calls didn't change.

## What a next attempt would need

Whether a fact is missing has to be judged by the words that name the subject or the thing asked for, not by every word in a padded search. One example is to fire only when the rarest search word (by IDF, the weight that ranking already uses for rare words) has no holder among the shown facts. Any new rule must be judged on question sets it was not tuned on. All four sets here have now been seen twice.
