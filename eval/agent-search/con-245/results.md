# CON-245 — Container memory saves only what is new: results

Bars, splits and the tuning rule were set in `eval/pass-bars.md` (CON-245 section) before any run. The check holds a
save whose semantic cosine to a container passage is at least 0.78306. Runs used Sonnet at medium effort on the test
split.

| Bar | Required | Measured | Result |
|---|---|---|---|
| Sorting: restating notes dropped | ≥ 80% | 44.4% of the 160 that ran (at most 53.2% of all 190) | Missed |
| Sorting: new facts kept | ≥ 90% | 90.6% (29 of 32) | Met |
| Planted: M gain | ≥ +0.30, 95% interval above 0 | +0.24 (0.13 to 0.36) | Missed |
| Planted: H | 90% low end above −0.10 | 0.00 (0.00 to 0.00) | Met |
| Realistic: M | 95% low end above −0.10 | −0.02 (−0.07 to +0.02) | Met |
| Realistic: H | 90% low end above −0.10 | 0.00 (0.00 to 0.00) | Met |

M is the memory set, the tasks memory can help with. H is the holdout set, which it shouldn't change.

## Sorting after the agent decides

Raw output: `memory-sort-agent.json` and `memory-sort-agent.runs.jsonl`, 222 notes.

Thirty learned restating notes never reached an agent because `claude` failed to start (exit `0xC0000142`). The report's
53.2% counts them as dropped, so 44.4% is the honest figure, and the bar is missed either way. This run's host was also
stopped for low memory, so these failures almost certainly have the cause found below; that wasn't tested on this run.

The check held 143 of the 160 restating notes, and agents saved 72 of those again with a reason, so the loss is in the
second save. By source, 71.6% of planted restating notes were dropped against 43.1% of learned ones (with the 30 counted
as dropped). The typical reason was that a passage never mentions the subject the note links to it.

## Planted arm

Raw output: `memory-search-test.json` and `memory-search-test.runs.jsonl`, 112 tasks, 3 repetitions, compared by task
with CON-244's off runs. No holds and no failed starts.

| | Off | On, CON-244 | On, CON-245 |
|---|---|---|---|
| M score | 0.479 | 0.912 | 0.719 |
| M gain | | +0.43 | +0.24 |
| H score | 1.00 | 1.00 | 1.00 |
| Corrections | 0.292 | 0.708 | 0.271 |
| Current value | 0.500 | 0.938 | 0.646 |
| Aliases | 0.500 | 1.00 | 0.958 |
| Locations | 0.625 | 1.00 | 1.00 |

The check took the planted gain from +0.43 to +0.24. Most of the loss is in corrections, which fell back to the off
score, and the rest is mainly in current-value tasks. By the pre-registered replay rule, a held remember is dropped unless its task labels it undocumented, and a
correct whose target was dropped becomes a new remember checked the same way. Of 164 replayed saves, 100 were held, 11
were retried with a reason, 89 were dropped and 19 corrects were rewritten. Undocumented facts still gain the most,
0.09 to 0.60; documented ones are about level, 0.86 to 0.83.

The check added a mean 12.2 ms per save.

## Realistic arm

Raw output: `memory-learned-test.json` and `memory-learned-test.runs.jsonl`, 104 tasks, 3 repetitions, compared by task
with CON-244's off runs. All 312 session-2 runs and all 228 session-1 runs reached an answer.

| | Off | On, CON-244 | On, CON-245 |
|---|---|---|---|
| M score | 0.703 | 0.609 | 0.682 |
| M difference | | −0.09 | −0.02 |
| H score | 1.00 | 0.99 | 1.00 |
| Corrections | 0.563 | 0.104 | 0.438 |
| Current value | 0.250 | 0.333 | 0.292 |
| Aliases, locations, controls, stale | 1.00 | 0.97 to 1.00 | 1.00 |

The bar here is no harm, and it is met. Memory written by agents still doesn't help: M is slightly below off, and the
report's gate verdict is "no better". The check removed most of CON-244's harm to corrections.

Session-1 agents made 320 saves. The check held 214, agents saved 96 of those again with a reason, and 10 were stored
first try. So 118 held saves, 55% of them, were dropped.

The first attempt is void, so no bar is read from it. A low-memory stop of the session killed the shell whose console
the eval had inherited. From then on every `claude` start failed with `0xC0000142`, even with 40 GB free, and the guard
scored those runs as errors: 159 of 312 session-2 runs. A small test reproduced the failure: a child of a closed console
exits `0xC0000142`, and one with a console of its own starts normally. The earlier guess that low memory caused this
exit code was wrong.

Fixed in the eval: `claude` gets its own console, and 5 failed starts in a row stop the run in place of scoring zeros.
Repetition 1 finished before the failure and is kept from that attempt, on the build without the fix, which changes only
how `claude` starts. Repetitions 2 and 3 ran again from the checkpoint with no holds and no failed starts. Nothing was
tuned between them.

## First tuning, offline, replaced

This section is the first rule, retired when the sorting bar was read as what happens after the agent decides. The
cutoff in use, 0.78306, is the highest that holds at least 90% of dev restating notes.

`dotnet run --project tests/Connapse.Eval -- memory-novelty --config hybrid` indexes both CON-244 corpora (late
documents left out) with nomic-embed-text and scores each labelled note against its container's passages. Raw output:
`memory-novelty.json` and `memory-novelty.notes.jsonl`.

The first rule, semantic cosine ≥ 0.8893, held 32.1% of test restating notes (61 of 190) and kept all 32 new facts.
The new facts in this set are relations whose every word is already in the documents: "message queue codename Osprey"
is new because no document links the message queue to Osprey, though Osprey's own notes exist. Telling these apart needs
reading the relation, which the decision rules out, because Connapse runs no model of its own for this.
