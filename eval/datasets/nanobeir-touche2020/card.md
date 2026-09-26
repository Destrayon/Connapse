# nanobeir-touche2020

- **What it tests:** argument retrieval: find args.me arguments for or against a controversial question.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoTouche2020 (revision 0d2f26e…), a 49-query sample of BEIR Touché-2020.
- **Upstream licence:** CC BY 4.0, per the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663). The Hugging Face card's label (CC BY 4.0) matches.
- **Size:** 5745 documents, 49 queries, 932 judgments.
- **Labels:** human; the original judgments are graded (BEIR maps negatives to 0), but every judgment in the NanoBEIR qrels loads as grade 1.
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 49 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). Keyword search is known to beat dense retrieval on Touché, so a hybrid loss here is expected rather than a bug. NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
