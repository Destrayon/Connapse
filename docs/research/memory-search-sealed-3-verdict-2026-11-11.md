# CON-322 round 2 on the sealed-3 set: verdict

**Date:** 2026-11-11
**Result:** fails the bar in `eval/pass-bars.md` on two per-kind score gates, both by one task each. The change
(ddec3ae8) beats grep and ripgrep on calls and cost in every kind and pooled (p ≤ 0.009), and on find score, but it
scores below them on duplicate saves (one task) and below ripgrep on absent questions (an answer-citation effect).
Today's arm is void on this set, so the gates against today cannot be judged.

Runs: `memory-agent --dataset memory-agent-sealed-3`, `claude-sonnet-5-5`, effort medium, 3 repetitions, 12-call limit,
2 workers, containers capped at 2 CPUs and 4 GB. Results: `eval/baselines/memory-agent-sealed-3-{change5,today,grep,ripgrep}-2026-11-11.json`.
Ten-repetition runs of exact, reworded and duplicate (diagnostic only) finished for change5 and grep; ripgrep's was
cut off when Claude Code stopped the queue for low memory. Paired tests use `PairedStats.Compare`, paired by task.

## Per kind (score, mean tool calls, mean list-price cost per run)

| Kind | Tasks | Change5 | Grep | Ripgrep |
|---|---|---|---|---|
| exact | 8 | 1.000, 1.00, $0.0062 | 0.917, 1.00, $0.0063 | 0.917, 1.79, $0.0153 |
| reworded | 30 | 0.978, 1.04, $0.0072 | 0.933, 1.18, $0.0083 | 0.889, 1.41, $0.0102 |
| indirect | 18 | 0.944, 1.22, $0.0081 | 0.833, 1.80, $0.0107 | 0.870, 2.07, $0.0276 |
| partial | 12 | 1.000, 1.08, $0.0072 | 1.000, 1.53, $0.0085 | 0.917, 1.81, $0.0151 |
| absent | 8 | **0.792**, 1.79, $0.0095 | 0.792, 2.12, $0.0102 | 0.917, 3.33, $0.0223 |
| new | 10 | 1.000, 2.07, $0.0128 | 1.000, 2.23, $0.0135 | 1.000, 2.33, $0.0140 |
| update | 8 | 1.000, 2.00, $0.0128 | 1.000, 2.00, $0.0149 | 1.000, 2.00, $0.0168 |
| duplicate | 6 | **0.944**, 1.06, $0.0066 | 1.000, 1.00, $0.0075 | 1.000, 1.00, $0.0074 |

Ten repetitions, change5 against grep: exact $0.00423 vs $0.00398 (+6%, noise ±41%), reworded 0.973 vs 0.923 at
$0.00586 vs $0.00559 (+5%, noise ±23%), duplicate 0.867 vs 1.000 at 1.13 vs 1.00 calls.

## Gates

| Gate | Result | |
|---|---|---|
| Score within 0.05, every kind, vs grep and ripgrep | **fail** | duplicate −0.056 vs both; absent −0.125 vs ripgrep |
| Calls at most +0.1, every kind | pass vs ripgrep; duplicate +0.06 vs grep (+0.13 at 10 reps) | |
| Cost at most +5%, every kind | pass | 2–71% cheaper on every kind at 3 reps; 10-rep one-call kinds within noise |
| Fewer find calls than grep / ripgrep | pass | 1.17 vs 1.46 / 1.87, p = 0.0002 both |
| Lower find cost than grep / ripgrep | pass | $0.0076 vs $0.0089 (p = 0.0086) / $0.0169 (p = 0.0004) |
| Hard: first recall shows gold ≥ 0.90 | pass | 0.983 |
| Gates against today | not judged | today's arm is void (below) |
| Retrieval arm hard hit@10 ≥ 0.80 | not run | |
| `memory_recall` speed; leak suite | pass | memory integration tests (leak, load, speed) 121/121 on ddec3ae8 |

## Where the bar is missed

- **Duplicate (save-d03).** The user restates that Tern discharges between 16:00 and 21:00. Both arms see the stored
  "peak window: 16:00-21:00" and "charges and discharges customer batteries around peak prices". The change5 agent
  saved "discharges only between 16:00 and 21:00" as a new fact in 8 of 10 runs; the grep agent called it a match in
  10 of 10. The search showed the same facts, so this is the agent's judgement on a borderline restatement.
- **Absent.** An absent question scores 1 only when the agent answers and cites nothing. All 5 of change5's misses
  (and all 5 of grep's) correctly say memory has no such fact but cite the nearby facts they checked. Memory output
  shows short handles (`f84`) that agents cite readily; grep and ripgrep output shows long ids. Ripgrep's 2 misses
  were a run error and running out of calls. On sealed-1 ripgrep lost 3 absent runs the same way and change5 none.
- **Today's arm is void.** 108 of its 228 find runs ended "Refused" with no tool call and no answer (find score 0.49),
  so its fewer calls are not comparable. It ran from the older harness checkout in `E:\tmp\con-322\base2`.

Per the bar's rules, sealed-3 is now spent and these are not tuned against it.
