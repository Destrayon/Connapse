# nanobeir-dbpedia

- **What it tests:** entity retrieval: find DBpedia entity abstracts that answer a keyword or natural-language entity query.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoDBPedia (revision 438f1c2…), a 50-query sample of BEIR DBPedia-Entity.
- **Upstream licence:** CC BY-SA 3.0, per the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663). The Hugging Face card's label is CC BY 4.0.
- **Size:** 6045 documents, 50 queries, 1158 judgments.
- **Labels:** human (crowdsourced); the original judgments are graded 0–2, but every judgment in the NanoBEIR qrels loads as grade 1, so the levels are not distinguished.
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 50 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). Many relevant documents per query (about 23 on average), so recall@10 is capped well below 1. NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
