# nanobeir-scidocs

- **What it tests:** citation prediction: given a paper title, find the abstracts of papers it cites.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoSCIDOCS (revision 484eb90…), a 50-query sample of BEIR SCIDOCS.
- **Upstream licence:** GNU GPL v3.0, per the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663). The Hugging Face card's label is CC BY 4.0.
- **Size:** 2210 documents, 50 queries, 244 judgments.
- **Labels:** derived from citations (not manual relevance judgments), binary (grade 1).
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 50 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). Citation is a weak proxy for relevance, so absolute scores are low for every system. NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
