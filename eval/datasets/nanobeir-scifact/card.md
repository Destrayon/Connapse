# nanobeir-scifact

- **What it tests:** scientific claim verification: find abstracts that support or refute a claim.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoSciFact (revision 309f1d1…), a 50-query sample of BEIR SciFact.
- **Upstream licence:** CC BY-NC 2.0 (non-commercial), per the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663). The Hugging Face card's label is CC BY 4.0.
- **Size:** 2919 documents, 50 queries, 56 judgments.
- **Labels:** human (expert annotators), binary (grade 1).
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 50 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
