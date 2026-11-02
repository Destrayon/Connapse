# CON-160 result: summaries do not help — **no**

Same agent as CON-146's `files_first` (Sonnet 5.5 via `claude -p`, 25-turn cap), same 36 hard + 12 control
questions over erb-50k, same judge. This time MinIO file storage was loaded, so `get_document` works.

- **baseline**: today's tools.
- **summaries**: today's tools, plus a `Document summary:` line on every `search_knowledge` hit (Haiku, Connapse's
  PerDoc prompt) and `container_describe` allowed (container summary from the product's own rollup job).

| Setup | Hard (36) | Control (12) | Cost per question | Tool calls | Turns | `get_document` calls | `container_describe` used |
|---|---|---|---|---|---|---|---|
| baseline | 22 (61%) | 11 | $0.1602 | 5.6 | 6.6 | 39 | 0/48 |
| summaries | 22 (61%) | 11 | $0.2594 | 6.9 | 7.7 | 33 | 0/48 |
| CON-146 `files_first` (empty storage) | 19 | 12 | $0.2040 | | | | |

## Against the pass bar ([pass-bar.md](pass-bar.md))

1. **At least 4 more hard questions right**: +0 (22 vs 22). FAIL.
2. **At most 20% more cost**: +61.9% per question. FAIL.
3. **No drop on questions answered right today**: 28 were right in baseline; qst_0235 and qst_0244 dropped and stayed
   wrong on both tiebreak reruns. FAIL. (qst_0214 and control qst_0083 also flipped but were right on both reruns.)

Hard discordant pairs: summaries won 5 (qst_0256, 0203, 0297, 0229, 0289), lost 5 (qst_0296, 0235, 0214, 0243, 0244);
exact McNemar p = 1.000. The summaries move answers around without adding any.

## What else the run showed

- **The agent never asked for the container summary.** 0 of 48 questions called `container_describe`, so that half of
  the feature was not tested by use. The container summary was also written only after summaries questions 1–7 had run,
  which changes nothing given no question called it.
- **Summaries cost more mostly through extra searching** (168 vs 138 `search_knowledge` calls), not fewer documents read.
- **Loading storage alone moved the baseline from 19 to 22 hard right.** CON-146's numbers came from an empty bucket.
- **Producing summaries for the whole corpus would cost about $43** with Haiku (1,193 written here at $0.0009 each).

## Setup findings worth keeping

- The app reads files from `Knowledge:Storage:MinIO:BucketName`, which appsettings sets to `connapse-files`; the
  snapshot's bucket is `aikp-files`, so set `Knowledge__Storage__MinIO__BucketName=aikp-files` (README step 1).
- The container rollup reads `SummarySettings` from the `settings` table, not env vars; it skips with
  `summaries_disabled` until a `Summary` row with `{"enabled":true}` exists.
- .NET's HttpClient sends the Ollama request body chunked, so the Haiku shim has to parse chunked bodies.

## Spend

About **$25.97** of the $32 cap: $25.29 in the four run directories (baseline, summaries, two tiebreak reruns, judge
and Haiku included), $0.011 for the rollup through the shim, and $0.67 for a discarded first baseline question that ran
while storage reads were broken. The stack (containers, web app, shim) was torn down afterwards.

Raw judged runs are in [results/](results/); `results/summary.txt` has per-question correctness.
