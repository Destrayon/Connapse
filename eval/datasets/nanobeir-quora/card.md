# nanobeir-quora

- **What it tests:** duplicate-question retrieval: find Quora questions that ask the same thing as the query.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoQuoraRetrieval (revision 2ab2d73…), a 50-query sample of BEIR Quora.
- **Upstream licence:** no licence stated. the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663) lists Quora among the datasets with no reported licence; Quora's original release (https://quoradata.quora.com/First-Quora-Dataset-Release-Question-Pairs) makes the data subject to Quora's Terms of Service, which allow non-commercial use. The Hugging Face card's label is CC BY 4.0.
- **Size:** 5046 documents, 50 queries, 70 judgments.
- **Labels:** human duplicate labels from Quora, extended by BEIR with transitive closure, binary (grade 1).
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 50 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). Queries and documents are both one-line questions, so the task says little about long-document retrieval. NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
