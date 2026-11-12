# Facts a save might repeat or contradict — verdict (CON-332, 2026-11-12)

**Passes the bar committed in `eval/pass-bars.md` before the first run.** A save now shows up to three current
facts it may repeat or contradict, using one full-text search seeded by the words Postgres already stemmed for the
new fact. It makes no model call and uses no embeddings, and nothing is ever changed because of a match.

## Labelled cases (`MemorySaveMatchCases`, 44 cases over 19 background facts)

| Family | Shown as expected | Bar |
|---|---|---|
| Same subject and attribute | 8/8, each first | all, first |
| Attribute reworded | 10/10 | pooled ≥ 95% |
| Subject spelled or extended differently | 6/7 | pooled ≥ 95% |
| Pooled near-match families | 24/25 (96%) | ≥ 95% |
| Unrelated (must show nothing) | 12/13 (92%) | ≥ 90% |
| Subject named in other words (reported only) | 2/5 | not gated |

No save showed more than three facts, and no seeded fact was superseded.

The miss and the false alarm:

- **"Orrin" saving "chief executive" beside "Orrin Systems"/"CEO".** The subjects are close, but "CEO" and
  "chief executive" share no letters, so the attribute test fails. Only meaning-based matching would catch it.
- **"launch date" beside "freeze date" for Halcyon.** These are the same subject, and the attributes share the word
  "date", so the check shows the freeze date. This is a false alarm the agent can dismiss; the note tells it to keep
  both facts when they differ.

Synonym subjects: "SSO gateway" and "billing service" were found, because another fact's value describes Wicket and
Halcyon in those words. "cold-room watcher", "batch runner" and "the firm" were missed: nothing stored uses those
words. These need embeddings (CON-322 stage 2).

## Speed (`MemorySaveMatchGateTests`, Category=Gate)

The space held 5,000 facts and the run made 200 saves, half repeating a held key and half new. Timings come from a
local Testcontainers Postgres capped at 2 CPUs and 4 GB.

| | p95 | median | bar (p95) |
|---|---|---|---|
| Check alone | 9.8 ms | 8.6 ms | ≤ 50 ms |
| Whole save, check included | 12.8 ms | 11.3 ms | ≤ 150 ms |

All 100 repeating saves showed their earlier fact, and none of the 100 new subjects showed anything. A check that
runs past 500 ms, or throws, shows nothing while the save still succeeds (`MemoryServiceMatchTests`).
