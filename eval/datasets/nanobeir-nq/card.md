# nanobeir-nq

- **What it tests:** open-domain question answering: find the Wikipedia passage that answers a real Google search question.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoNQ (revision 7754014…), a 50-query sample of BEIR Natural Questions.
- **Upstream licence:** CC BY-SA 3.0, per the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663). The Hugging Face card's label is CC BY 4.0.
- **Size:** 5035 documents, 50 queries, 57 judgments.
- **Labels:** human (annotators marked the answering passage), binary (grade 1).
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 50 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). Mostly one relevant passage per query, so other answering passages score as misses. NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
