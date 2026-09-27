# dev-quora

- **What it tests:** duplicate-question retrieval: find Quora questions that ask the same thing as the query.
- **Source:** https://huggingface.co/datasets/mteb/quora (revision 7f060c1…), BEIR's full dataset; queries come from the BEIR dev split. Built locally by `eval/tools/build_dev_suite.py` (seed 552): 100 queries sampled from 5000 eligible (NanoBEIR test queries removed by ID and by text), and a corpus pooled from the full 522,931 documents: every judged document plus each query's top 50 by BM25 and top 50 by nomic-ai/nomic-embed-text-v1.5.
- **Upstream licence:** no licence stated. the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663) lists Quora among the datasets with no reported licence; Quora's original release (https://quoradata.quora.com/First-Quora-Dataset-Release-Question-Pairs) makes the data subject to Quora's Terms of Service, which allow non-commercial use. The builder downloads the source at build time; nothing is redistributed.
- **Size:** 8290 documents, 100 queries, 140 judgments (140 at grade 1; 1.4 per query).
- **Labels:** the upstream BEIR dev judgments, grades kept as published.
- **Split rule:** dev data, in the `dev` suite only and never in `v1`. The harness scores it like a test set so reports and `compare` work; tune against it, never against `v1`.
- **Known limitations:** 100 queries → the minimum detectable nDCG@10 difference is still sizeable (see each report's MDE). The pool is built with BM25 and one dense model, so systems that retrieve differently from both meet fewer distractors than they would in the full corpus.
