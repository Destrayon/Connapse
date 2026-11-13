# CON-342 results: newest-first recall

**Verdict: FAIL on both bars.** These are the bars committed in `eval/pass-bars.md` before any run. Agents told to recall newest first with a small limit read *more* text, not less. They also got slightly fewer answers right.

Setup:

- The run follows the registered plan.
- `claude-sonnet-5-5` at medium effort answered all 152 held-out memory questions twice in each arm. Every run answered, so no run is excluded.
- The questions ran against the held-out writer-agent operations, replayed into one personal space named `projects`.
- In the Today arm, `order` is hidden from the agents. In the Newest arm, `order` is offered and the system prompt adds one sentence:
  > Recall with `order` "newest" and a small `limit`, and read further only when those facts do not answer the question.
- The subscription was at 93–94% of its seven-day limit throughout.

| Bar | Today | Newest | Newest − Today | Interval | Verdict |
|---|---|---|---|---|---|
| R1: text read per task (characters) | 3,266 | 4,127 | +860.5 | 95% BCa [+495, +1,228]; must sit below 0 | FAIL |
| R2: right answers | 90.1% | 86.2% | −3.9 pts | 90% BCa [−7.6, −0.7]; low end must be above −5 | FAIL |

## By question kind

| Kind | Tasks | Right, Today → Newest | Text read, Today → Newest | Recall calls per run, Today → Newest |
|---|---|---|---|---|
| current | 40 | 100% → 97.5% | 1,902 → 3,724 | 1.00 → 1.35 |
| as_of | 39 | 100% → 100% | 1,731 → 1,488 | 1.01 → 1.00 |
| changed | 10 | 100% → 100% | 11,916 → 11,917 | 1.00 → 1.00 |
| when | 63 | 76.2% → 68.3% | 3,710 → 4,780 | 1.00 → 1.33 |

Agents used the option when offered:

- 206 of 304 Newest runs passed `order`.
- Their limits were mostly 10 (292 calls) and 5 (56 calls).
- Recall calls rose from 305 to 373.
- Tokens at list price rose from $3.38 to $4.00.

## Why it fails

The agents' first call usually combined a free-text `query` with `order=newest`. Newest order replaces relevance ranking. Such a call returns the ten most recently dated facts that loosely match, across every attribute of the project, so the fact asked about is often missing.

- Of 187 runs whose first call combined `query` and `order=newest`, 69 needed a second recall by subject and attribute.
- Of 93 runs that used `query` without an order, none needed a second call.

The extra calls account for the added text on `current` and `when` questions.

The drop in `when` answers has a different cause. With the newest change listed first, agents more often named the later change date as well as the one asked about. The scoring rule counts an answer with any second date as wrong. The Today arm loses its `when` points the same way, only less often.

Newest-first does help where the agent already names the subject and attribute: `as_of` text read fell 14%, with no loss of right answers. It does not help when the order is applied to a ranked text search.

## Files

- `today.json` and `newest.json`: per-arm reports.
- `today.runs.jsonl` and `newest.runs.jsonl`: every run, with its calls and answer.
- `compare.json`: output of `memory-recall-compare`.

Reproduce:

```
dotnet run --project tests/Connapse.Eval -- memory-recall --arm today|newest --repetitions 2 --workers 2 --out <file>
```
