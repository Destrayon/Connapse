# CON-341: existing facts in the save answer — results

All five bars set in `eval/pass-bars.md` ("Existing facts in the save answer") before any run pass.

| Bar | Result |
|-----|--------|
| R1: the counted save lists the fact it landed beside | Pass |
| R2: one call fixes it | Pass |
| R3: the `worker` saves list nothing | Pass |
| Q1: Listed's right-answer rate ≥ Today's − 0.05 | Pass (1.00 vs 1.00) |
| Q2: Listed's wrongly replaced facts ≤ Today's + 1 | Pass (0 vs 0) |

## Replay of the claudepm copy

Sources: the read-only export `claudepm-export.json` (870 fact versions, 830 saves of a new fact), exported for
CON-340. It was replayed through the real `MemoryService` and `PostgresMemoryStore` in a throwaway pgvector container
capped at 2 CPUs and 4 GB. Each save got a fresh space seeded with the same-key facts as they stood just before it.
The live `claudepm` space was never written.

- **R1.** The `CON-291` / `status` save (`01a1bc93…`) lists `01a1bc77-f7a0-7731-aa12-dc3d9e5d3b44` with its value,
  `validFrom` 2026-11-08T18:50Z and an empty `validTo`.
- **R2.** One `memory_correct` call (kind `wrong`, the listed value, `validTo` 2026-11-08T20:30Z, when the new fact
  took effect) leaves exactly one current fact, the new one. As of one second before 20:30Z the old value is still
  the answer. The first read of "now" was taken one minute after the save was recorded. That was before the new fact's
  `validFrom`, so the old value still showed as current. The bar is read once the new fact has taken effect.
- **R3.** All 14 `worker` saves under `local_*` subjects answer with no list, including the four the bar names.
- **Reported:** 200 of the 830 saves would carry a list: 91 `lesson`, 91 `note`, 17 `finding` and 1 `status`. The
  first three are deliberate accumulating attributes, which is why the bar does not count them.

## Writer agents on `memory-questions`

Claude Sonnet subagents with `memory_writer.md`, 2 repetitions × 5 held-out projects × 2 arms = 20 runs, at most 2 at a
time. The ops files are in `ops/<arm>/<rep>/` and the scorer output in `scored.json`
(`eval/tools/memory-questions/score_save_list.py`).

| Arm | Rep | Right (of 79) | Wrongly replaced | Saves that got a list |
|-----|-----|---------------|------------------|-----------------------|
| Today | 1 | 79 | 0 | 0 |
| Today | 2 | 79 | 0 | 0 |
| Listed | 1 | 79 | 0 | 0 |
| Listed | 2 | 79 | 0 | 0 |

No writer in either arm ever saved a fact beside a current one with the same subject and attribute. Every change
went through `correct`, so the list never appeared. This set shows the change does no harm. It cannot show that the
list helps, because the agents never made the mistake it catches. The replay above is the evidence for that.

Run notes:
- Every run passes `check_memory_ops.py`. Several agents said they had run two commands in one step. Three said they
  fetched the next session before recording a session that was only a plan, so nothing was recorded from it early.
  The bar's breach is what `check_memory_ops.py` flags, so these runs count. The first `today` ASH run was set aside
  before that was settled, and its rerun is the one scored.
- The first two runs passed `--ops-dir` with backslashes through Bash, which stripped them. The files landed in
  `E:\tmpcon-341ops…` and were moved into place. The prompts were then switched to forward slashes.
- After the first runs, the launch prompt gained "run one command per tool call, one at a time" for both arms alike.
