# CON-342 results: newest-first recall

**Verdict: FAIL on both bars, in both rounds.** The bars were committed in `eval/pass-bars.md` before any run and were not changed between rounds.

Setup, the same in both rounds:

- `claude-sonnet-5-5` at medium effort answered the 152 held-out memory questions twice per arm. Every run answered, so no run is excluded.
- The questions ran against the held-out writer-agent operations, replayed into one personal space named `projects`.
- In the Today arm, `order` is hidden from the agents. In the Newest arm, `order` is offered and the system prompt adds one sentence:
  > Recall with `order` "newest" and a small `limit`, and read further only when those facts do not answer the question.
- The subscription was at 93–95% of its seven-day limit throughout.

| Round | Bar | Today | Newest | Newest − Today | Interval | Verdict |
|---|---|---|---|---|---|---|
| 1 | R1: text read per task (characters) | 3,266 | 4,127 | +860.5 | 95% BCa [+495, +1,228]; must sit below 0 | FAIL |
| 1 | R2: right answers | 90.1% | 86.2% | −3.9 pts | 90% BCa [−7.6, −0.7]; low end must be above −5 | FAIL |
| 2 | R1: text read per task (characters) | 3,522 | 3,328 | −193.7 | 95% BCa [−498, +115]; must sit below 0 | FAIL |
| 2 | R2: right answers | 87.5% | 81.6% | −5.9 pts | 90% BCa [−9.5, −3.3]; low end must be above −5 | FAIL |

## Round 1: newest replaced ranking even with a query

In round 1, `order=newest` replaced relevance ranking even when the agent gave a free-text `query`. Such a call returned the ten most recently dated loose matches across every attribute, and the fact asked about was often missing. Of 187 runs that combined `query` and `order=newest` in their first call, 69 needed a second recall. Of 93 runs that used `query` without an order, none did. The extra calls added the text.

## Round 2: a query keeps its ranking

After round 1, recall was changed (commit `4a942494`). With a query, recall keeps relevance ranking and ignores `newest`. Newest order now applies only to a read without text, and the tool description says so.

| Kind | Tasks | Right, Today → Newest | Text read, Today → Newest |
|---|---|---|---|
| current | 40 | 100% → 100% | 2,301 → 2,738 |
| as_of | 39 | 100% → 100% | 1,630 → 1,109 |
| changed | 10 | 100% → 100% | 11,918 → 11,917 |
| when | 63 | 69.8% → 55.6% | 4,136 → 3,714 |

Agents used the option less once it no longer applied to searches:

- 87 of 304 Newest runs passed `order`, against 206 in round 1.
- Every run made exactly one recall call.

**Text read (R1).** Newest read 5.5% less on average, mostly on `as_of` questions (−32%). The 95% interval still includes zero, so the bar is not met.

**Right answers (R2).** The whole loss is on `when` questions, and it does not come from what recall returned:

- Only 9 of the 126 Newest `when` runs passed `order` at all. The other 117 made the same recall a Today agent could make, with the same result.
- The losses are answers that named the date asked for and then the date of the later change, for example "2026-02-05 (…it stayed current until 2026-04-17…)". The scoring rule counts any answer with a second date as wrong.
- Among the 117 runs without `order`, Newest got 53 wrong, all of them answers with two or more dates. On the same tasks and repetitions, Today got 37 wrong.

The likeliest cause is the extra prompt sentence, which tells agents to read further, nudging them to describe what came after. That remains a guess, since nothing here isolates the sentence.

Run-to-run noise is large. With identical code and prompt, Today's `when` score moved from 76.2% in round 1 to 69.8% in round 2.

## Files

- `run1/` and `run2/` each hold the same files for their round:
  - `today.json` and `newest.json`: per-arm reports.
  - `today.runs.jsonl` and `newest.runs.jsonl`: every run, with its calls and answer.
  - `compare.json`: the `memory-recall-compare` output.

Reproduce:

```
dotnet run --project tests/Connapse.Eval -- memory-recall --arm today|newest --repetitions 2 --workers 2 --out <file>
```
