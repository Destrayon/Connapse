# nanobeir-arguana

- **What it tests:** counter-argument retrieval: given a full argument as the query, find its best counter-argument.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoArguAna (revision 8f4a982…), a 50-query sample of BEIR ArguAna.
- **Upstream licence:** CC BY 4.0, per the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663). The Hugging Face card's label (CC BY 4.0) matches.
- **Size:** 3635 documents, 50 queries, 50 judgments.
- **Labels:** derived from the debate portal's argument/counter-argument pairing (one counter-argument per query), binary (grade 1).
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 50 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). Queries are whole arguments, so they are long and lexically close to many documents. NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
