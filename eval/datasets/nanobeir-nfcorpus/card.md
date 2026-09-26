# nanobeir-nfcorpus

- **What it tests:** medical retrieval: find PubMed articles relevant to a lay nutrition question.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoNFCorpus (revision dd542a7…), a 50-query sample of BEIR NFCorpus.
- **Upstream licence:** the original site (https://www.cl.uni-heidelberg.de/statnlpgroup/nfcorpus/) says it is free to use for academic purposes and that other uses of the NutritionFacts.org data need the author's permission; the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663) lists it among the datasets with no reported licence. The Hugging Face card's label is CC BY 4.0.
- **Size:** 2953 documents, 50 queries, 2518 judgments.
- **Labels:** derived automatically from NutritionFacts.org citation links; the original is graded 0–2 by link type, but every judgment in the NanoBEIR qrels loads as grade 1.
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 50 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). About 50 relevant documents per query, so recall@10 is capped far below 1 and nDCG@10 is the more useful metric. NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
