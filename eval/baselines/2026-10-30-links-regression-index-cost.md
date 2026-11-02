# CON-150: link search on v1 and pdfqa-v1, and link-suite index cost

The two rows of the links gate (`eval/pass-bars.md`) that CON-107 left unmeasured.

- **Tip:** `epic/con-e3-links-and-memory` at `46d0353` (run folders carry `476a7ea`, this branch's empty commit on top).
- **Branch point:** `f400887`, where the epic leaves `main`.
- **Machine:** one desktop (32 logical CPUs) for every run. Containers capped at 2 CPUs (Postgres and MinIO 2 GB,
  Ollama 4 GB, CPU only, one request in flight), 2 ingestion workers, below-normal priority, one run at a time.

## A. No regression: link search off (`hybrid`) vs on (`hybrid-links`)

One index per dataset, searched under both configs (`run --suite <s> --config hybrid,hybrid-links`). The TREC run
files are byte-identical for every dataset measured, so every metric is identical and no significance test is needed.

| Suite | Datasets measured | Rankings | Headline | p95 off → on |
|---|---|---|---|---|
| pdfqa-v1 | 1 of 1 | identical | MRR@10 0.506 both, nDCG@10 0.537 both | 145 → 59 ms |
| v1 | 16 of 16 | identical | equal on all 16 | at most +0.4% (table below) |

Latency is lower with links on for almost every dataset, but that reflects run order, not link search: `hybrid`
always searches first and pays the cold start. What it does show is that link search adds no measurable time, so
the "p95 +10% at most" bar passes. The one rise, cqadupstack-programmers 280 → 281 ms, is +0.4%.

**Verdict: pass.** Link search changes no ranking on any of the 17 datasets.

v1 p95 in ms, per query, test split (`Scoring.Percentile`):

| Dataset | off | on |
|---|---|---|
| nanobeir-arguana | 855 | 198 |
| nanobeir-climatefever | 141 | 77 |
| nanobeir-dbpedia | 95 | 48 |
| nanobeir-fever | 95 | 52 |
| nanobeir-fiqa | 105 | 50 |
| nanobeir-hotpotqa | 127 | 41 |
| nanobeir-msmarco | 92 | 43 |
| nanobeir-nfcorpus | 70 | 28 |
| nanobeir-nq | 86 | 33 |
| nanobeir-quora | 105 | 36 |
| nanobeir-scidocs | 122 | 36 |
| nanobeir-scifact | 241* | 59* |
| nanobeir-touche2020 | 75 | 59 |
| cqadupstack-programmers | 280 | 281 |
| ragbench-techqa | 263 | 257 |
| ragbench-emanual | 30 | 22 |

Claude Code stopped the first v1 run partway through, while it was searching, because the PC ran low on memory
(other workloads were also running). The first 11 datasets above had finished under both configs.

The rest were rerun one dataset at a time on 2026-10-31:

- **nanobeir-scifact:** done. Rankings identical off and on; nDCG@10 0.789 and MRR@10 0.758 under both; indexed in
  1330 s. *Its run folders were lost when the old worktree was removed, so its p95 comes from the search service's
  own "Search completed in" log lines (50 queries per config), not from `Scoring.Percentile`.
- **The last four** ran later on 2026-10-31 under the same caps, except that Ollama had the GPU (still one request
  in flight). The first touche2020 attempt, CPU only, was stopped 3 h 10 min in at 3,000 of 5,745 documents;
  touche2020 was rerun from scratch, reusing the embeddings that attempt had cached.

| Dataset | Test queries | nDCG@10 off / on | MRR@10 off / on | Indexed in |
|---|---|---|---|---|
| nanobeir-touche2020 | 49 | 0.656 / 0.656 | 0.827 / 0.827 | 378 s |
| cqadupstack-programmers | 876 | 0.395 / 0.395 | 0.394 / 0.394 | 3525 s |
| ragbench-techqa | 157 | 0.624 / 0.624 | 0.619 / 0.619 | 1731 s |
| ragbench-emanual | 66 | 0.864 / 0.864 | 0.866 / 0.866 | 41 s |

## B. Index cost: branch point vs tip

`run --suite <s> --config hybrid --index-cache off`, three times at each commit, alternating (branch point, then
tip, repeated three times). Both commits share one embedding disk cache, warmed by an uncounted tip run, so neither
pays for embeddings. The measured time is parsing, chunking, database writes, vector index builds and, at the tip,
link writes. Times are the harness's `indexed in` lines (1 s resolution).

| Dataset | Branch point runs (s) | Median | Tip runs (s) | Median | Change |
|---|---|---|---|---|---|
| links-connapse-github (links-v1) | 20, 20, 20 | 20 | 20, 21, 21 | 21 | +5% (1 s) |
| links-flink-jira (links-v1) | 176, 153, 144 | 153 | 150, 152, 151 | 151 | −1% |
| links-connapse-github-2 (links-v2) | 29, 31, 30 | 30 | 31, 30, 30 | 30 | 0% |
| links-flink-atlassian (links-v3) | 172, 162, 159 | 162 | 169, 158, 166 | 166 | +2.5% |

All four are within the +10% bar, and each change is smaller than the spread between repeats at the same commit.

### What the branch point needed

The branch point's harness knows only links-v1. To index links-v2 and links-v3 there:

- the tip's `eval/MANIFEST.json` replaced the branch point's copy;
- the tip's `eval/datasets/links-connapse-github-2/` and `eval/datasets/links-flink-atlassian/` were copied in;
- `2026-10-30-con150-branch-point.patch` gave the eval adapter the tip's Confluence rendering without link metadata
  (applies to `f400887`), so the branch point indexes the same text and writes no links.

The branch-point tree was a `git archive` export, built in Release.

## Run folders

At the tip, under `eval/runs/` (not committed):

- pdfqa-v1: `20261030-043338-476a7ea-pdfqa-v1-connapse-hybrid` and `-hybrid-links`.
- v1 (partial): `20261030-171943-476a7ea-v1-connapse-hybrid` and `20261030-171944-476a7ea-v1-connapse-hybrid-links`.
- v1 scifact rerun: `20261031-065159-52adc5b-v1-connapse-hybrid` and `20261031-065200-52adc5b-v1-connapse-hybrid-links`
  (deleted with the old worktree; the scores above are from the run log).
- v1, last four, at `d7f9071` (the epic tip merged into this branch): `20261031-190253` (touche2020), `190937`
  (cqadupstack-programmers), `201127` (ragbench-techqa) and `204229` (ragbench-emanual), each
  `-d7f9071-v1-connapse-hybrid` and `-hybrid-links`.
- Part B tip repeats: `20261030-1638*`, `1654*` and `1711*` for links-v1, links-v2 and links-v3. The warm-up runs are
  `20261030-093347`, `125341` and `130444`.
