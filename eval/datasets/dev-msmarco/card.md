# dev-msmarco

- **What it tests:** web passage retrieval: find a passage that answers a real Bing search query.
- **Source:** https://huggingface.co/datasets/mteb/msmarco (revision 49b09e5…), BEIR's full dataset; queries come from the BEIR dev split. Built locally by `eval/tools/build_dev_suite.py` (seed 552): 100 queries sampled from 6930 eligible (NanoBEIR test queries removed by ID and by text), and a corpus pooled from the full 8,841,823 documents: every judged document plus each query's top 50 by BM25 and top 50 by nomic-ai/nomic-embed-text-v1.5.
- **Upstream licence:** MS MARCO's terms allow non-commercial research use only (BEIR describes it as "MIT License for non-commercial research purposes", the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663); terms at https://microsoft.github.io/msmarco/). The harness only downloads the files at run time and never redistributes them. The builder downloads the source at build time; nothing is redistributed.
- **Size:** 8615 documents, 100 queries, 115 judgments (115 at grade 1; 1.1 per query).
- **Labels:** the upstream BEIR dev judgments, grades kept as published.
- **Split rule:** dev data, in the `dev` suite only and never in `v1`. The harness scores it like a test set so reports and `compare` work; tune against it, never against `v1`.
- **Known limitations:** 100 queries → the minimum detectable nDCG@10 difference is still sizeable (see each report's MDE). The pool is built with BM25 and one dense model, so systems that retrieve differently from both meet fewer distractors than they would in the full corpus.
