# EmbeddingGemma 2 against nomic-embed-text (CON-361)

## Method and bar

Written on 2026-11-22, before any run. Nothing below this section changes it.

**Models.**

- **Baseline:** `nomic-embed-text` through Ollama, the app's default (`src/Connapse.Web/appsettings.json`), 768
  dimensions, prompts `search_query: ` / `search_document: `, lowercased input.
- **Candidate:** EmbeddingGemma 2, Ollama tag `embeddinggemma-2:270m-bf16-text` (see the amendment below). This is the text-only load (130M
  backbone and 140M embedder), the same text weights as the full 740M model without its vision and audio encoders.
  It runs in bf16, since the model card warns float16 returns NaN or degraded vectors. 768 dimensions, prompts
  `task: search result | query: ` / `title: none | text: ` (its `config_sentence_transformers.json`).
- **Prompt handling:** Connapse's prompt registry (`EmbeddingPrompts`) knows `embeddinggemma` but not
  `embeddinggemma-2`. To give the candidate its published prompts without changing product code, the eval copies the
  model in Ollama under an `embeddinggemma:` name, which the registry resolves to EmbeddingGemma's prompts.
  Those are the same strings EmbeddingGemma 2 publishes.
- *Amended 2026-11-22, before any scored run:* Ollama's `-bf16` tags need Apple's MLX runtime and do not run on this
  Windows/CUDA machine. The candidate is instead Unsloth's BF16 GGUF of the text model
  (`hf.co/unsloth/embeddinggemma-2-GGUF`, file `embeddinggemma-2-BF16.gguf`, 558 MB), loaded without its `mmproj`
  file (the image and audio encoders) as `embeddinggemma:eg2-text-bf16`. A check before the runs
  (`bench/refcheck.json` in the results) compared it with Google's float32 `google/embeddinggemma-2` checkpoint
  through sentence-transformers 6.3: cosine at least 0.99999 on 20 queries and 40 passages, the same top passage for
  every query, and the full 744M-parameter load gives the same text vectors as the 271M text-only load (cosine
  1.0000). So this is EmbeddingGemma 2's text path at the released size, not a cut-down model.

**Eval set.** Suite `v1` minus `cqadupstack-programmers` and `ragbench-techqa`, which took about 9 hours each to index
on 2026-10-24. That leaves the 13 NanoBEIR datasets and `ragbench-emanual`: 14 datasets, all public. `nanobeir-scidocs`
was unscored in the baseline; it is scored here only if both runs ingest it under the harness's 1% failure limit.
Test split.

**Configs.** `hybrid` (the app's defaults, the product decision) and `semantic` (vector search only, to show the
embedder's effect without BM25). Both share one index per model. Chunking is the default Semantic chunker, which
places boundaries with the embedder, so each model gets its own chunks.

**Limits.** `eval/tools/run_light.sh` with `EVAL_GPU=1`: Ollama, Postgres and MinIO capped at 2 CPUs, Ollama at
4 GB, one embedding request in flight, 2 ingestion workers, below-normal priority, one model at a time, nothing left
running. GPU use is sampled every 10 s and must stay under 50%; a run that goes over is stopped and rerun on CPU.

**Measures.**

| Measure | How |
|---|---|
| Search quality | nDCG@10 per dataset (also MRR@10, Recall@10); portfolio = mean over scored datasets. Paired per query, Holm-corrected permutation test from `compare`. |
| Index time | The harness's `indexed in` line per dataset, summed. |
| Search latency | The report's p95 per dataset. |
| Embedding speed | Outside the harness: documents per second at batch 1 and 16, and single-query latency p50/p95, on 2 CPUs and on the GPU, same 500 passages and 100 queries for both models. |
| Memory | Peak Ollama container memory (`docker stats`) during the run and the benchmark; GPU memory from `nvidia-smi`. |
| Index size | Row count and `pg_total_relation_size` of `chunk_vectors` and its vector index, sampled from the eval Postgres before it is torn down. |

**Baseline numbers.** The comparison is against a fresh nomic run on the same commit, machine and limits. The
2026-10-24 baseline (`eval/baselines/2026-10-24.md`) is a sanity check: on these 14 datasets its hybrid nDCG@10 is
0.549 arguana, 0.350 climatefever, 0.685 dbpedia, 0.938 fever, 0.523 fiqa, 0.895 hotpotqa, 0.607 msmarco, 0.331
nfcorpus, 0.691 nq, 0.958 quora, 0.789 scifact, 0.656 touche2020 and 0.862 emanual, a mean of 0.680 over those 13.
A fresh nomic mean more than 0.02 away from that is investigated before the candidate's results are read.

**The bar for switching the default.** EmbeddingGemma 2 becomes the recommended default only if every line holds:

1. **Better:** `hybrid` portfolio nDCG@10 at least +0.010 over the fresh nomic run, and at least one dataset with a
   significant gain.
2. **No regression:** no dataset with a significant `hybrid` nDCG@10 drop.
3. **Index time:** summed index time at most 1.5× nomic's.
4. **Search latency:** median over datasets of the `hybrid` p95 at most nomic's +50 ms.
5. **Memory:** peak Ollama memory at most 1.5 GB in the eval run.
6. **Index size:** `chunk_vectors` total size at most 1.10× nomic's.

If 1 and 2 hold but a cost line fails, the verdict is "offer it as an option, keep nomic as the default". If 1 or 2
fails, the verdict is "don't switch". `semantic` results are reported, not gated.
