# CON-245 — Container memory saves only what is new: results

Bars, splits and the tuning rule were set in `eval/pass-bars.md` (CON-245 section) before any run.

## Sorting (offline, no model calls)

`dotnet run --project tests/Connapse.Eval -- memory-novelty --config hybrid` indexes both CON-244 corpora (late
documents left out) with nomic-embed-text and scores each labelled note against its container's passages. Raw output:
`memory-novelty.json` (the tuned rule and gate) and `memory-novelty.notes.jsonl` (every note, its split, its scores and
whether the rule held it).

Notes: 134 planted ops (94 restating, 40 new) and 228 learned session-1 notes, all restating. One of the 229 learned
notes the bars counted had a blank field and was not scored. Dev 140, test 222.

Best cutoff per candidate score on dev (each keeps 8 of 8 dev new facts):

| Score | Cutoff | Dev restating held |
|---|---|---|
| Best single passage, word share | 1.0 | 18.2% |
| Top three passages together, word share | 0.861 | 34.8% |
| Value words only, best passage | none keeps 90% of new facts | 0% |
| Semantic cosine of the top passage | 0.8893 | 40.2% |

The rule chosen on dev is semantic cosine ≥ 0.8893. On the test split, scored once:

| | Restating held | New facts kept |
|---|---|---|
| Bar | ≥ 80% | ≥ 90% |
| All test notes | **32.1%** (61 of 190) | **100%** (32 of 32) |
| Planted ops | 3.0% (2 of 67) | 100% (32 of 32) |
| Learned notes | 48.0% (59 of 123) | — |

**Verdict: the sorting bar is missed.** New facts are never held, but only a third of restating notes are.

Why: the new facts in this set are relations whose every word is already in the documents. "message queue codename
Osprey" is new because no document links the message queue to Osprey, but Osprey's ops notes exist, so every passage
score finds Osprey; "revision QN is in force" is new because no document says which revision is in force, while the
revision QN document itself is indexed. Value-only overlap is 1.0 for every one of the 40 new facts. A restating planted
op such as "Finance password rotation interval 120 days" has full word overlap but cosine 0.83, below the cutoff that
keeps every dev new fact (the highest dev new fact scored 0.889). Telling these apart needs reading the relation, which
the decision rules out (Connapse runs no model of its own for this).

The cutoff was not moved after the test score.

## Agent arms (not run)

The planted and realistic agent arms were not rerun with the check on. Each run drives Claude agents, and the usage
meter had reached the 42% stop line set for this ticket on 2026-11-05. The bars stay as pre-registered: realistic arm
with memory on no worse than off (off ≈ 0.70), and planted arm gain at least +0.3. Given the sorting result, the expected
effect is small: the check holds a third of restating saves and no new facts, so it cannot lower the planted gain
through lost facts, and it removes only part of the restating clutter the realistic arm suffered from.
