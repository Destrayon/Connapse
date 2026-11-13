# CON-342: why the Today arm got answers wrong

The Today arm's right-answer rates were 90.1% in round 1 and 87.5% in round 2. Every one of its wrong answers had the right date. The agent then added the date of the next change, and the scoring rule counts any second date as wrong. No miss came from recall, from a misread fact or from an ambiguous question.

This uses only the existing `run1/today.runs.jsonl` and `run2/today.runs.jsonl`, checked against `eval/datasets/memory-questions/timeline.json` and `memory-ops/`. No new runs were made.

## Causes

The Today arm had 68 wrong runs out of 608: 30 in round 1 and 38 in round 2. All of them were on `when` questions, spread over 24 of the 63 `when` tasks. The `current`, `as_of` and `changed` questions scored 100% in both rounds.

| Cause | Wrong runs | Example |
|---|---|---|
| Right date given first, then the next change's date (strict scoring) | 65 | ASH-039, "When did Ashgrove's alert channel change to #ash-alerts?", expected 2026-04-11. The answer was "2026-04-11 (ASH-24). It changed back to #ash-pager on 2026-06-11 (ASH-31)." |
| Right date given first, then the next two changes' dates (strict scoring) | 3 | MRW-048, expected 2026-01-26. The answer was "2026-01-26 (changed from GitLab CI to Jenkins; it later moved back to GitLab CI on 2026-04-25, then CircleCI on 2026-05-23)". |
| Recall did not return the fact | 0 | — |
| Agent misread a fact or picked the wrong change | 0 | — |
| Ambiguous question | 0 | — |
| Memory held the wrong date or no value | 0 | — |

How each run was checked:

- In all 68 runs, the expected date appears in the answer, and it is the first date given.
- Every extra date is a later change of the same setting, taken from the timeline.
- Every run asked recall for the setting's history (`includeHistory`), so the agent had the full list of changes in front of it.
- The writer's saved dates match the timeline for every asked change.

Recall output text is not logged. That it returned the fact is inferred from these points: the agent gave the exact expected date, and the extra dates it gave are real later changes.

## What recall could fix

Recall could fix none of the 68 misses. The agent already had the right fact every time.

Two fixes would each bring the Today arm to 100% on these runs. They are not exclusive:

1. **Tell the agent to give one date.** The system prompt in `tests/Connapse.Eval/Runs/MemoryRecallEval.cs` says "a date as YYYY-MM-DD" but never says only one. Agents read "When did X change to Y?" as an invitation to add how long Y lasted. A sentence like "For a question asking when something changed, give only that date" closes this.
2. **Score the first date.** The rule in `MemoryEval` counts any answer with a second ISO date as wrong. Scoring only the first date would accept all 68 answers. A real user would also count them as right.

Fixing the prompt is the more promising choice. It matches what a product prompt should ask for, and it keeps the scoring rule strict about agents that hedge between two candidate dates. The scoring-rule change would also hide that kind of hedge.

This also bears on the CON-342 verdict. The round-2 right-answer loss for Newest came from the same two-date answers, more often. Until the prompt asks for one date, the R2 bar on `when` questions measures how often an agent volunteers extra context, not whether recall worked. Run-to-run swings like Today's 76.2% to 69.8% on `when` questions are mostly this same habit varying between runs.
