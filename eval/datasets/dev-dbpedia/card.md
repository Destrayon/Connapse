# dev-dbpedia

- **What it tests:** entity retrieval: find DBpedia entity abstracts that answer a keyword or natural-language entity query.
- **Source:** https://huggingface.co/datasets/mteb/dbpedia (revision 7d8780d…), BEIR's full dataset; queries come from the BEIR dev split. Built locally by `eval/tools/build_dev_suite.py` (seed 552): 67 queries sampled from 67 eligible (NanoBEIR test queries removed by ID and by text), and a corpus pooled from the full 4,635,922 documents: every judged document plus each query's top 50 by BM25 and top 50 by nomic-ai/nomic-embed-text-v1.5.
- **Upstream licence:** CC BY-SA 3.0, per the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663). The builder downloads the source at build time; nothing is redistributed.
- **Size:** 9825 documents, 67 queries, 5673 judgments (4268 at grade 0, 1024 at grade 1, 381 at grade 2; 84.7 per query).
- **Labels:** the upstream BEIR dev judgments, grades kept as published.
- **Split rule:** dev data, in the `dev` suite only and never in `v1`. The harness scores it like a test set so reports and `compare` work; tune against it, never against `v1`.
- **Known limitations:** 100 queries → the minimum detectable nDCG@10 difference is still sizeable (see each report's MDE). The pool is built with BM25 and one dense model, so systems that retrieve differently from both meet fewer distractors than they would in the full corpus.
