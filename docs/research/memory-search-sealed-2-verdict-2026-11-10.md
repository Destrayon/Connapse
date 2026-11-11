# CON-322 on the sealed-2 set: verdict

**Date:** 2026-11-10
**Result:** fails the replacement bar in `eval/pass-bars.md` ("Finding and saving memories", approved 2026-11-10).
The change beats today's memory tools on accuracy and cost, but it does not beat or tie grep and today's tools on
tool calls and cost for every task kind, and its pooled savings in calls are not significant.

Run: `memory-agent --dataset memory-agent-sealed-2`, `claude-sonnet-5-5`, effort medium, 3 repetitions, 12-call limit,
test split scored once. All three runs are conforming. Results: `eval/baselines/memory-agent-sealed-2-{change,memory,grep}-2026-11-10.json`.
Paired tests use `PairedStats.Compare` (sign-flip permutation p, 90% BCa interval), paired by task.

## Per kind (score, mean tool calls, mean list-price cost per run)

| Kind | Tasks | Change | Today | Grep |
|---|---|---|---|---|
| exact | 8 | 1.000, 1.00, $0.0051 | 0.958, 1.00, $0.0087 | 1.000, 1.00, $0.0051 |
| reworded | 30 | 0.933, 1.19, $0.0069 | 0.878, 1.10, $0.0114 | 0.944, 1.09, $0.0063 |
| indirect | 18 | 1.000, 1.54, $0.0100 | 0.759, 1.98, $0.0229 | 0.907, 1.96, $0.0110 |
| partial | 12 | 1.000, 1.56, $0.0092 | 0.917, 2.19, $0.0149 | 0.944, 1.86, $0.0102 |
| absent | 8 | 1.000, 2.12, $0.0104 | 1.000, 1.96, $0.0182 | 1.000, 2.12, $0.0110 |
| new | 10 | 1.000, 2.50, $0.0138 | 1.000, 2.17, $0.0218 | 1.000, 2.07, $0.0125 |
| update | 8 | 1.000, 2.00, $0.0118 | 1.000, 2.00, $0.0178 | 1.000, 2.00, $0.0138 |
| duplicate | 6 | 1.000, 1.00, $0.0070 | 1.000, 1.00, $0.0135 | 1.000, 1.00, $0.0071 |

## Gates

| Gate | Result | |
|---|---|---|
| Score within 0.05 of each comparator, every kind | pass | worst: reworded −0.011 vs grep |
| Calls at most +0.1, every kind | **fail** | new +0.33 vs today and +0.43 vs grep; absent +0.17 vs today; reworded +0.100 vs grep (at the limit) |
| Cost at most +5%, every kind | **fail** | new +10.2% and reworded +8.5% vs grep; 34–57% cheaper than today on every kind |
| Fewer find calls than today, p < 0.05 | **fail** | 1.41 vs 1.56, p = 0.075 |
| Fewer find calls than grep, p < 0.05 | **fail** | 1.41 vs 1.52, p = 0.14 |
| Lower find cost than today, p < 0.05 | pass | $0.0082 vs $0.0151, p = 0.0002 |
| Lower find cost than grep, p < 0.05 | **fail** | $0.0082 vs $0.0084, p = 0.43 |
| Hard: first recall shows gold ≥ 0.90 and +0.10 vs today | pass | 0.933 vs 0.750, p = 0.0002 |
| Hard: recall calls fall by ≥ 0.3 | **fail** | 1.36 vs 1.58, −0.22 (p = 0.036) |
| Retrieval arm hard hit@10 ≥ 0.80 | **fail** | 0.767 (today 0.500) |
| No harm vs today (90% BCa lower end > −0.05) | pass | hard +0.117 [0.061, 0.194]; exact, absent, save all ≥ 0 |
| `memory_recall` p95 < 300 ms; leak suite | pass | 49 ms; leak, load and speed tests 10/10 |

The hard-task first-recall paired test uses per-task values recomputed from the run logs by matching the gold fact's
value in the first search's output. That reproduces the eval's per-kind figure exactly for the change and slightly
undercounts today (0.70 vs the eval's 0.75), so the gate is judged on the eval's own means.

## Where the bar is missed

- **Save "new" tasks** take one more call than before: agents check memory, save, then often recall again to confirm.
- **Absent tasks** take a second search more often; the empty-search hint names nearby subjects, which agents follow up.
- **Reworded tasks against grep:** grep is already cheap on these, and the change's first answer is longer (a subject's
  other facts come with each hit), so cost per run is slightly higher.

Per the bar's rules, these are not tuned against sealed-2. A redesign would be judged on a fresh sealed set.
