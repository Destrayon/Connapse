# enterprise-10k: link search off vs on (CON-151)

Checks that `enterprise-10k` (`erb-10k`, `eval/datasets/erb-10k/card.md`) gives the same no-harm verdict for link
search as the full `enterprise-v1` run in `2026-10-28-links.md`. Same bar: identical rankings, p95 at most +10%.
EnterpriseRAG-Bench carries no connector metadata, so both runs write 0 links and can only show no harm.

**Same verdict: passes on both.** 470 test queries each; run 2026-10-30 at `32b6763` (plus the docs in this PR),
under `eval/tools/run_light.sh`'s limits: every container at 2 CPUs and 4 GB, Ollama on the GPU with one embedding
request in flight, 2 ingestion workers, `--index-cache off`.

| | erb-10k off | erb-10k on | erb-50k off | erb-50k on |
|---|---|---|---|---|
| Documents | 10,000 | 10,000 | 50,000 | 50,000 |
| nDCG@10 | 0.667 | 0.667 | 0.620 | 0.620 |
| MRR@10 | 0.660 | 0.660 | 0.619 | 0.619 |
| Recall@10 | 0.783 | 0.783 | 0.733 | 0.733 |
| p50 ms | 704 | 684 | 1,392 | 1,383 |
| p95 ms | 1,027 | 1,030 (+3, +0.3%) | 1,721 | 1,725 (+4, +0.2%) |
| Errors | 0 | 0 | 0 | 0 |
| Rankings | | identical (4,700 lines) | | identical (4,700 lines) |

- **Rankings.** On both sets the off and on TREC run files match line for line on query, document and rank.
- **Scores.** erb-10k scores higher because it has 40,000 fewer distractors. Compare a change only against a baseline
  on the same set.
- **Latency.** The two sets ran on different days with different memory limits, so their latencies don't compare;
  only off vs on within a set does.
- **Time.** erb-10k took 1 h 55 min of wall time: 1 h 44 min to index, then about 5 min to search under each
  config. erb-50k took about 15.5 h. On 2 CPUs without the GPU, Ollama embedded about 4 documents a minute, which
  would put erb-10k near 2 days, so routine runs need `EVAL_GPU=1`.
