# dev-fiqa

- **What it tests:** financial opinion question answering: find forum answers to investment questions.
- **Source:** https://huggingface.co/datasets/mteb/fiqa (revision 5e59eeb…), BEIR's full dataset; queries come from the BEIR dev split. Built locally by `eval/tools/build_dev_suite.py` (seed 552): 100 queries sampled from 500 eligible (NanoBEIR test queries removed by ID and by text), and a corpus pooled from the full 57,638 documents: every judged document plus each query's top 50 by BM25 and top 50 by nomic-ai/nomic-embed-text-v1.5.
- **Upstream licence:** not stated by the original authors. the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663) lists FiQA-2018 among the datasets with no reported licence (challenge site: https://sites.google.com/view/fiqa/home). Some secondary mirrors label it CC BY-SA 4.0; that label does not come from the authors. The builder downloads the source at build time; nothing is redistributed.
- **Size:** 7364 documents, 100 queries, 226 judgments (226 at grade 1; 2.3 per query).
- **Labels:** the upstream BEIR dev judgments, grades kept as published.
- **Split rule:** dev data, in the `dev` suite only and never in `v1`. The harness scores it like a test set so reports and `compare` work; tune against it, never against `v1`.
- **Known limitations:** 100 queries → the minimum detectable nDCG@10 difference is still sizeable (see each report's MDE). The pool is built with BM25 and one dense model, so systems that retrieve differently from both meet fewer distractors than they would in the full corpus.
