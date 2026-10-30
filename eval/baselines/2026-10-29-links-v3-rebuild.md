# links-v3 rebuilt from its snapshot, 2026-10-29 (CON-149)

Can links-v3 (`links-flink-atlassian`) be rebuilt from its saved snapshot and reproduce the scores CON-107 recorded
(run `20261028-062310-4446ef4-links-v3`, [2026-10-28-links.md](2026-10-28-links.md) on CON-107's branch)? The
snapshot and the `--snapshot` build are CON-132's (#1017); this run checks them on a fresh cache and re-scores.

**Code.** `epic/con-e3-links-and-memory` at `a83b1ae`. Since CON-107's `4446ef4` only `build_link_suite.py`, the
dataset card, the records pin and one unrelated integration test changed, so search is the same code.

**Runs.** 2026-10-29 on the same PC and setup as CON-107: nomic-embed-text through Ollama on the GPU (one request in
flight), reranker off, containers at 2 CPUs (Ollama 4 GB, Postgres and MinIO 2 GB), 2 ingestion workers,
below-normal priority. Two full runs, each indexing from scratch: `20261029-235611-a83b1ae-links-v3` and
`20261030-000436-a83b1ae-links-v3`. Each took under 8 minutes.

**Pass bar, set before measuring.** The rebuild matches all three pins with an empty cache and no `--repin`. Every
line CON-107 printed for both configs matches at its printed precision: nDCG@10, MRR@10 and judged@10 for the
dataset, and nDCG@10, Recall@10 and n for each `hops:` and `kind:` group.

## Results

**The rebuild passes.** From `E:\tmp\eval-snapshots\links-flink-atlassian\records.jsonl.xz` into an empty cache,
the build read 3,423 issues and 815 pages and wrote 4,238 records and 2,923 links. `records.jsonl` (`164bb398…`),
`links.jsonl` (`06f0927d…`) and `questions.jsonl` (`28696883…`) all match `eval/MANIFEST.json`.

**Links off passes.** All 17 lines of `hybrid` match the recorded ones exactly, in both runs.

**Links on fails the exact bar.** The scores move by up to 0.003 between runs on identical data and code:

| `hybrid-links` (160 test queries) | nDCG@10 | MRR@10 | Recall@10, hops:1 |
|---|---|---|---|
| CON-107, recorded | 0.323 | 0.257 | 0.547 |
| Run 1 | 0.326 | 0.260 | 0.549 |
| Run 2 | 0.323 | 0.256 | 0.547 |

Run 1 differs from the recorded scores on 8 of 17 lines and run 2 on 3, the largest single group being
`kind:jira-page` (0.676 recorded, 0.704 in run 1, 0.676 in run 2). Between the two runs, 31 of 307 queries rank
their top 10 differently.

The spread is about a thirtieth of the gain link search brings over links off (+0.103 nDCG@10), so CON-107's
conclusions hold.

## Why links on varies

Link search keeps at most 20 linked documents per query (`MaxReachedDocuments`) and, among equal link scores,
keeps them in document-id order (`src/Connapse.Search/Links/LinkSearch.cs:98`). Document ids are new random GUIDs
each time the set is indexed, and the neighbours of one start document all get the same link score, so which tied
documents make the cut changes from run to run. A document that misses it loses its link boost.

`jira-siblings-0024` shows it: page 125308817 scores 0.861 in run 1 and 0.761 in run 2, a difference of exactly
the link term, so it was among the 20 in one run and not in the other. None of the 31 changed queries is only
tied documents trading places; in each a document gains or loses its link score.

The fix is to break ties by a key that survives re-indexing, such as the document's path. That is a search change,
so it is CON-152 and not part of this ticket. Until then, a links-on rerun can't be expected to match to the third
decimal.

With links off, one query (`jira-page-0048`) swapped documents at ranks 8 to 10 between the runs. No score changed.
