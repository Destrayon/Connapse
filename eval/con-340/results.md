# CON-340 result: check-by dates on a copy of the claudepm space

**Verdict: FAIL.** The bar in `eval/pass-bars.md` ("Check-by dates on a copy of the ClaudePM memory space") was
committed in c177890f before the export. Bars 2 and 3 pass, but bar 1 fails: none of the 40 stale facts left the
current list.

## Run

- Export: one paged, read-only GET of the live `claudepm` space (`status=all`), taken 2026-11-13 04:55Z. It holds 870
  versions, 252 of them current; 67 of the current facts are in ClaudePM's decisions list. The live space was not written.
- Replay: all 252 current facts were saved again through the real `MemoryService` and `PostgresMemoryStore`, oldest
  first, with the clock at each fact's recorded time. The copy's space had the bar's defaults (`waiting_on` 7 days,
  `status` 14 days). Recall then ran at the export time.
- Stale facts: a current fact in the decisions list whose subject or value names a CON ticket that the PM's status
  list (2026-11-12) marks as merged or dropped.

| Bar | Result | Detail |
|---|---|---|
| 1. No stale fact in the current list | FAIL | 40 before, 40 after. The 2 CON-307 and 2 CON-335 facts stayed in the current list |
| 2. Every current fact in exactly one list | PASS | 67 current + 0 check these = 67 of 67 |
| 3. Recall drops nothing | PASS | 252 of 252 current facts recalled |

Reported, not gated: no still-true fact went into "check these", because no fact was past its date.

## Why it failed

The space is young, and its facts go stale fast. Its oldest fact was 4.5 days old at export. The 40 stale facts were
saved between 0.8 and 4.5 days before it, and half of them were under 1.5 days old. A ticket merged a day after its
`waiting_on` or `status` fact was saved. None of them reached a 7- or 14-day date.

Diagnostic only, not a result: shorter periods on `waiting_on` and `status` still miss most of them. With a 1-day
period, 11 of 40 move, along with 2 still-true facts; with 2 to 4 days, 8 of 40 move, along with 1.

Only 16 of the 40 are `waiting_on` or `status`. The other 24 are under attributes such as `decision`, `scope` and
`container design`, which have no default. Some of those are still true (a design decision that names a merged
ticket), so "names a closed ticket" overcounts.

## What it means

A date measures a fact's age, not whether the world changed. On this space, the stale facts went stale because a
ticket closed, usually within a day or two. A check-by date can't see that. Catching them needs an event (the ticket
merged) to flag or correct the fact, which is ClaudePM's side (PM-70) or a link from the fact to its ticket.
Following the pass-bar rules, the change is not merged as is. A redesign needs a new bar and a fresh copy.

Harness (outside the repo): `E:\tmp\con-340\replay` (C# replay), `evaluate.py` (scoring), `report.md` (per-fact list).
