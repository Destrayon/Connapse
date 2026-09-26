# nanobeir-fever

- **What it tests:** fact checking: find the Wikipedia abstracts that support or refute a claim.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoFEVER (revision a8bfdf1…), a 50-query sample of BEIR FEVER.
- **Upstream licence:** CC BY-SA 3.0, per the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663). The Hugging Face card's label is CC BY 4.0.
- **Size:** 4996 documents, 50 queries, 57 judgments.
- **Labels:** human (annotators selected evidence), binary (grade 1).
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 50 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). Claims were written from Wikipedia sentences, so they share wording with their evidence and favour keyword matching. NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
