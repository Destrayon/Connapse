# dev-nfcorpus

- **What it tests:** medical retrieval: find PubMed articles relevant to a lay nutrition question.
- **Source:** https://huggingface.co/datasets/mteb/nfcorpus (revision 52ac3f1…), BEIR's full dataset; queries come from the BEIR dev split. Built locally by `eval/tools/build_dev_suite.py` (seed 552): 100 queries sampled from 322 eligible (NanoBEIR test queries removed by ID and by text), and a corpus pooled from the full 3,633 documents: every judged document plus each query's top 50 by BM25 and top 50 by nomic-ai/nomic-embed-text-v1.5.
- **Upstream licence:** the original site (https://www.cl.uni-heidelberg.de/statnlpgroup/nfcorpus/) says it is free to use for academic purposes and that other uses of the NutritionFacts.org data need the author's permission; the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663) lists it among the datasets with no reported licence. The builder downloads the source at build time; nothing is redistributed.
- **Size:** 3262 documents, 100 queries, 4044 judgments (3871 at grade 1, 173 at grade 2; 40.4 per query).
- **Labels:** the upstream BEIR dev judgments, grades kept as published.
- **Split rule:** dev data, in the `dev` suite only and never in `v1`. The harness scores it like a test set so reports and `compare` work; tune against it, never against `v1`.
- **Known limitations:** 100 queries → the minimum detectable nDCG@10 difference is still sizeable (see each report's MDE). The pool is built with BM25 and one dense model, so systems that retrieve differently from both meet fewer distractors than they would in the full corpus.
