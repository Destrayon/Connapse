# dev-scifact

- **What it tests:** scientific claim verification: find abstracts that support or refute a claim.
- **Source:** https://huggingface.co/datasets/mteb/scifact (revision cf10ab6…), BEIR's full dataset; SciFact ships no dev split, so queries come from its train split. Built locally by `eval/tools/build_dev_suite.py` (seed 552): 100 queries sampled from 807 eligible (NanoBEIR test queries removed by ID and by text), and a corpus pooled from the full 5,183 documents: every judged document plus each query's top 50 by BM25 and top 50 by nomic-ai/nomic-embed-text-v1.5.
- **Upstream licence:** CC BY-NC 2.0 (non-commercial), per the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663). The builder downloads the source at build time; nothing is redistributed.
- **Size:** 3704 documents, 100 queries, 117 judgments (117 at grade 1; 1.2 per query).
- **Labels:** the upstream BEIR train judgments, grades kept as published.
- **Split rule:** dev data, in the `dev` suite only and never in `v1`. The harness scores it like a test set so reports and `compare` work; tune against it, never against `v1`.
- **Known limitations:** 100 queries → the minimum detectable nDCG@10 difference is still sizeable (see each report's MDE). The pool is built with BM25 and one dense model, so systems that retrieve differently from both meet fewer distractors than they would in the full corpus.
