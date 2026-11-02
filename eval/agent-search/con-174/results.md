# CON-174 result: a folder summary shown up front does not help (no)

**Decision:** because of this result, the user decided to remove folder summaries from the product; the removal is its own ticket and PR.

The agent called `container_describe` first on 48 of 48 questions and read the summary, and it got fewer hard
questions right. The verdict, under the bar in [pass-bar.md](pass-bar.md) written before any run, is **no**.

| Setup | Hard right (of 36) | Control right (of 12) | Cost per question | Tool calls | Turns | search_knowledge calls | Described first |
|---|---|---|---|---|---|---|---|
| baseline (CON-146 "both") | 26 (72%) | 12 | $0.1953 | 6.6 | 7.6 | 156 | 0/48 |
| folder (baseline + summary up front) | 18 (50%) | 11 | $0.2006 | 7.7 | 8.7 | 171 | 48/48 |
| CON-160 baseline, for reference | 22 | 11 | $0.1602 | | | | |

## Against the bar

1. More hard questions right: −8 (−22 points), where the bar was at least +4. **Fail.**
2. Cost: +2.7% per question, where the bar was at most +20%. Pass.
3. Nothing already right is dropped: of the 30 questions CON-146 got right, the folder setup dropped qst_0034,
   qst_0268 and qst_0282 (each was wrong in at least 2 of 3 attempts). qst_0240 was wrong once and right on both
   reruns, so it doesn't count as dropped. **Fail.**

On the hard questions the two setups disagreed 8 times, and the folder setup lost all 8 (exact McNemar test,
p = 0.008). The describe-first annotation doesn't apply, because 48/48 is above the 43 the bar required.

## Why it hurt

This is read from the transcripts, with no further API spend, so it is a reading rather than a measured cause. On
the 8 lost hard questions (qst_0218, 0229, 0240, 0256, 0267, 0268, 0278, 0282), the agent with the summary searched
with similar queries but settled sooner on a close match. Four answers open with "the best match is…" or "I couldn't
find a record that…", then describe a neighbouring incident or leave out the one detail the judge needed. On
qst_0256 the baseline kept searching for 32 calls and found the right incident; the folder setup stopped after 7
with a different one. The summary describes the corpus as many similar operational records, which may make "close
enough" look like the expected answer.

## Setup

- Corpus erb-50k, where each record is its own folder, so the folder is the container `eval-erb-50k`. The 48
  questions are CON-146's (36 hard, 12 control), and the judge is CON-142's with CON-146's one-word change.
- The summary was built offline (`container_summary.py`) with the product's per-document and rollup prompts, from 20
  records sampled across the container with the gold records left out. A sample was needed because the product's
  summary job only reads the first 10,000 documents, a bug the PM has ticketed separately.
- `describe_proxy.py` inserts the summary into `container_describe`'s output, and instructions
  `prompts/describe-v1.md` tell the agent to call it first. That prompt got 10 of 10 in the quick test
  ([instructions.md](instructions.md)).

## Spend

Full run $22.77 (both setups, judging, 6 tiebreak reruns), quick test $1.10, summary $0.03: about $23.90 against the
$30 cap. The first run stopped at 27 questions because the budget guard projected past the original $25. Two
reasons: the baseline cost 30% more per question than in CON-160 on the same questions, and the guard had been
weighting CON-160's cost too lightly (fixed in 73f84a58). The user raised the cap to $30 and the run resumed.

## Limits

- One summary text and one instruction prompt were tested. A summary that only routes ("this container holds X;
  search for Y") without describing the records might not have the same effect, but nothing here suggests it would
  add correct answers.
- Each question ran once per setup, apart from the tiebreak reruns. Even so, losing all 8 disagreements is unlikely
  to be chance.

## Per question

Y = right; columns are baseline / folder / CON-146 files_first.

| Question | Kind | Baseline | Folder | CON-146 |
|---|---|---|---|---|
| qst_0296 | hard | - | - | - |
| qst_0248 | hard | Y | Y | Y |
| qst_0235 | hard | - | - | Y |
| qst_0240 | hard | Y | - | Y |
| qst_0251 | hard | Y | Y | Y |
| qst_0270 | hard | Y | Y | Y |
| qst_0273 | hard | Y | Y | Y |
| qst_0214 | hard | Y | Y | Y |
| qst_0254 | hard | - | - | - |
| qst_0239 | hard | - | - | - |
| qst_0267 | hard | Y | - | - |
| qst_0256 | hard | Y | - | - |
| qst_0191 | hard | Y | Y | Y |
| qst_0278 | hard | Y | - | - |
| qst_0186 | hard | - | - | - |
| qst_0204 | hard | - | - | - |
| qst_0243 | hard | - | - | - |
| qst_0179 | hard | Y | Y | - |
| qst_0193 | hard | Y | Y | Y |
| qst_0207 | hard | Y | Y | - |
| qst_0178 | hard | Y | Y | Y |
| qst_0245 | hard | Y | Y | Y |
| qst_0275 | hard | - | - | - |
| qst_0203 | hard | - | - | - |
| qst_0282 | hard | Y | - | Y |
| qst_0297 | hard | Y | Y | - |
| qst_0213 | hard | Y | Y | Y |
| qst_0221 | hard | - | - | - |
| qst_0196 | hard | Y | Y | Y |
| qst_0218 | hard | Y | - | - |
| qst_0229 | hard | Y | - | - |
| qst_0255 | hard | Y | Y | Y |
| qst_0299 | hard | Y | Y | Y |
| qst_0244 | hard | Y | Y | Y |
| qst_0268 | hard | Y | - | Y |
| qst_0289 | hard | Y | Y | Y |
| control (11 questions) | control | Y | Y | Y |
| qst_0034 | control | Y | - | Y |

The 11 control questions all three setups got right: qst_0024, 0046, 0078, 0091, 0042, 0083, 0001, 0067, 0081,
0055, 0108.
