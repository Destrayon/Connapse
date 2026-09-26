# nanobeir-fiqa

- **What it tests:** financial opinion question answering: find forum answers to investment questions.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoFiQA2018 (revision 4163ba0…), a 50-query sample of BEIR FiQA-2018.
- **Upstream licence:** not stated by the original authors. the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663) lists FiQA-2018 among the datasets with no reported licence (challenge site: https://sites.google.com/view/fiqa/home). Some secondary mirrors label it CC BY-SA 4.0; that label does not come from the authors. The Hugging Face card's label is CC BY 4.0.
- **Size:** 4598 documents, 50 queries, 123 judgments.
- **Labels:** derived from the StackExchange answers posted to each question (not a separate relevance judgment), binary (grade 1).
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 50 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). Only the answers posted to a question count as relevant, so a good answer from another thread scores as a miss. NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
