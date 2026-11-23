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

## Verdict

**Don't switch.** EmbeddingGemma 2 (EG2) scores 0.678 hybrid nDCG@10 against nomic-embed-text's 0.682 on the 13
scored datasets. It is significantly worse on two of them (touche2020 −0.142 and emanual −0.111, Holm-corrected) and
significantly better on none, so bars 1 and 2 both fail, and its peak memory misses bar 5. It is not worth offering as
an option either: its best gain (scifact +0.080) does not survive correction, and it loses most on `emanual`, the only
dataset here that looks like a product manual, which is the kind of content Connapse users upload.

## Results

Runs: nomic on 2026-11-22 from 11:12 to 17:44, EG2 from 18:26 to 23:11. The EG2 run was stopped twice when the machine
ran low on memory and resumed from the harness's per-dataset checkpoints, so no dataset was scored twice. Same commit
(`6152484c`), machine and limits. The fresh nomic mean over the 13 scored datasets is 0.682 against 0.680 in the
2026-10-24 baseline, inside the 0.02 sanity margin. `nanobeir-scidocs` failed ingestion for both models (344 of 2,210
documents, the same failure as in October), so it is unscored, as the method said it would be.

**Hybrid nDCG@10** (the product default). The difference is EG2 minus nomic with its 95% interval; *p* is
Holm-corrected across all datasets and metrics.

| Dataset | nomic | EG2 | Difference | Holm *p* |
|---|---|---|---|---|
| arguana | 0.543 | 0.610 | +0.067 [+0.012, +0.127] | 1.00 |
| climatefever | 0.356 | 0.340 | −0.017 [−0.075, +0.030] | 1.00 |
| dbpedia | 0.688 | 0.677 | −0.011 [−0.054, +0.028] | 1.00 |
| fever | 0.931 | 0.910 | −0.021 [−0.058, +0.007] | 1.00 |
| fiqa | 0.535 | 0.493 | −0.042 [−0.109, +0.020] | 1.00 |
| hotpotqa | 0.894 | 0.892 | −0.002 [−0.030, +0.021] | 1.00 |
| msmarco | 0.606 | 0.640 | +0.034 [−0.005, +0.082] | 1.00 |
| nfcorpus | 0.333 | 0.378 | +0.046 [+0.015, +0.122] | 1.00 |
| nq | 0.692 | 0.751 | +0.059 [+0.003, +0.123] | 1.00 |
| quora | 0.958 | 0.968 | +0.010 [−0.024, +0.047] | 1.00 |
| scifact | 0.789 | 0.869 | +0.080 [+0.034, +0.149] | 0.27 |
| touche2020 | 0.670 | 0.528 | **−0.142** [−0.181, −0.102] | **0.018** |
| emanual | 0.872 | 0.761 | **−0.111** [−0.170, −0.067] | **0.018** |
| **Portfolio** | **0.682** | **0.678** | −0.004 | |

On touche2020, EG2 also loses significantly on Recall@5, Recall@10 and P@5. Its gains are on short factual or
scientific questions (scifact, nq, nfcorpus, arguana). Its losses are on long argumentative passages (touche2020) and
technical manual text (emanual).

**Semantic nDCG@10** (vector search only, not gated). The semantic run was not resumed after the second memory stop,
so it covers only the first 8 datasets (arguana to nfcorpus). Over those 8, EG2 is −0.002 on the mean with no
significant difference after correction. Its largest moves are hotpotqa +0.075 (Holm *p* 0.056) and fiqa −0.097.

**Costs against the bar.**

| Bar | nomic | EG2 | Holds? |
|---|---|---|---|
| 3. Summed index time, 14 datasets | 23,409 s | 23,070 s (0.99×) | Yes, but see below |
| 4. Median of per-dataset `hybrid` p95 latency | 181 ms | 136 ms | Yes, but see below |
| 5. Peak Ollama memory in the run | 1.40 GiB | 1.86 GiB (median 1.39 GiB) | **No** |
| 6. `chunk_vectors` total size | 309 MB, 66,985 vectors | 332 MB, 71,248 vectors (1.07×) | Yes |

Index time and latency from the harness are confounded by other work running on the same PC during both runs: the
same EG2 arguana ingest took 1,983 s on a first, aborted try and 285 s on the rerun. They are reported because the
method named them, but the isolated embedding speed in the next section is the number to trust. Both models store 768
float32 values per vector (3,076 bytes), so index size differs only through chunk count. The Semantic chunker places
chunk boundaries using the embedder, and it cut about 6% more chunks with EG2. EG2's size is the sum of the two run
segments either side of the second memory stop, because the eval database is rebuilt on resume.

**Embedding speed and memory in isolation.** These were measured on 2026-11-23, outside the harness. Each model got a
fresh Ollama container capped at 2 CPUs and 4 GB, then embedded the same 500 eval passages one at a time and in batches
of 16, and then 100 queries one at a time. Each model ran on the CPU and then on the GPU, one model at a time.

| | nomic CPU | EG2 CPU | nomic GPU | EG2 GPU |
|---|---|---|---|---|
| Passages/s, batch 1 | 1.3 | 0.6 | 11.8 | 8.8 |
| Passages/s, batch 16 | 1.4 | 0.6 | 25.5 | 14.8 |
| Query latency p50 | 199 ms | 298 ms | 88 ms | 97 ms |
| Query latency p95 | 204 ms | 400 ms | 3,274 ms* | 194 ms |
| Container peak memory | 702 MiB | 892 MiB | 1,260 MiB | 1,361 MiB |
| GPU memory added | — | — | 621 MiB | 792 MiB |
| Model size loaded (`ollama ps`) | 389 MB | 782 MB | 335 MB | 508 MB |

\* One stalled request: nomic's p50 on the GPU is 88 ms, and other GPU work on the PC was running at the time.

Embedding passages, EG2 is about half as fast as nomic on 2 CPUs and 0.6× as fast on the GPU at batch 16. It also
uses about 30% more GPU memory. Its query latency is close to nomic's on the GPU and about 100 ms slower on the CPU.
The eval's similar total index times hide this, because the harness sends one request at a time and its time goes
mostly to Postgres, chunking and other load on the machine, not to the embedder. On a CPU-only install, where most
self-hosted users start, EG2 would roughly double the embedding part of ingest.

## Moving existing indexes over

Not recommended now. If a later version clears the bar, the move is a full re-embed:

- Vectors from different models are not comparable, so every chunk must be embedded again. The schema can hold both
  (`chunk_vectors.embedding` has no fixed dimension, and each model gets its own partial IVFFlat index keyed by
  `model_id`), but that is not a zero-downtime migration. Search embeds the query only for the configured model, so
  the moment the setting changes, documents not yet re-embedded drop out of semantic results; hybrid search still
  finds them through keyword matching. Reindexing also deletes each document's old chunks when it writes the new ones,
  so switching back to nomic needs another full reindex. A cutover without that gap would need a separately staged
  index and an atomic switch, which Connapse does not have today.
- Documents are re-chunked, not just re-embedded, because the default Semantic chunker uses the embedder to place
  boundaries. Plan for at least the original ingest time per container, and up to twice the embedding time on a
  CPU-only install.
- `EmbeddingPrompts` (`src/Connapse.Core/Models/EmbeddingPrompts.cs`) knows `embeddinggemma` but not
  `embeddinggemma-2`, so the model under its Ollama name would get no prompts and lose quality. It needs an
  `embeddinggemma-2` entry mapped to the EmbeddingGemma prompts (`task: search result | query: ` /
  `title: none | text: `).
- On Windows and Linux the Ollama `-bf16` tags do not run (they need Apple's MLX), so a GGUF build such as Unsloth's
  is needed, and float16 must be avoided (the model card warns it returns NaN or degraded vectors).
- EG2 supports Matryoshka truncation (vectors cut to 512, 256 or 128 values with a small loss), which could shrink
  vector storage up to 6×. That was not measured here.

## Multimodal search

Not with Connapse as it is, and not through Ollama. EG2 is a 270M text backbone plus separate image (170M) and audio
(300M) encoders that map into the same 768-value space, so in principle a text query could find an image or a
recording. In practice:

- Ollama's EG2 tags accept text or text plus image, never audio, and the GGUF builds carry images only through a
  separate `mmproj` file. Audio needs Google's checkpoint through sentence-transformers or transformers.
- Connapse has no path for either. Parsers turn files into text, chunks are text, and the embedding provider interface
  takes strings. Image search would need an image ingestion path (storing the image, a chunk that points to it, a
  provider call that sends pixels) and UI to show image hits.
- Loading the vision encoder adds about 170M parameters, roughly 0.3 GB in bf16, to every embedding process.

Image search is a plausible follow-up if users ask for it, but it is a feature project rather than a model swap, and it
would need its own eval set, since none of the 14 datasets here has images.

## Sources

- Google, EmbeddingGemma 2 model card: https://ai.google.dev/gemma/docs/embeddinggemma/model_card_2. Architecture
  (270M text, 170M vision, 300M audio), Matryoshka sizes, the float16 warning, and benchmark scores: MTEB English v2
  68.46 at 768 dimensions (EmbeddingGemma 1: 69.67), multilingual 61.36 (61.15), code 78.68 (68.76). No BEIR numbers
  are published, which is why this eval was needed.
- Google DeepMind, EmbeddingGemma 2 technical report, arXiv 2610.37364: https://arxiv.org/abs/2610.37364
- Weights and prompts: https://huggingface.co/google/embeddinggemma-2 (`config_sentence_transformers.json`)
- Ollama tags: https://ollama.com/library/embeddinggemma-2/tags
- Unsloth GGUF builds, used here: https://huggingface.co/unsloth/embeddinggemma-2-GGUF
- How other projects use it: Qdrant's Ollama embedding docs list `embeddinggemma-2:270m` at 768 dimensions
  (https://qdrant.tech/documentation/embeddings/ollama/). LumaDock's self-hosted RAG tutorial pairs it with Ollama and
  Qdrant (https://lumadock.com/tutorials/self-hosted-rag-ollama-qdrant). MarkTechPost covered the launch
  (https://www.marktechpost.com/2026/10/06/google-deepmind-releases-embeddinggemma-2-a-740m-open-multimodal-embedding-model-built-on-gemma-4/).
  Google's launch post for the first EmbeddingGemma lists the integrations that family ships with
  (https://developers.googleblog.com/en/introducing-embeddinggemma/).
- Run folders, compare reports and resource samples are kept outside the repo; the numbers above come from each run's
  `report.json` and `compare-vs-*.json`.
