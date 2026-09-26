# nanobeir-hotpotqa

- **What it tests:** multi-hop question answering: find the two Wikipedia paragraphs needed to answer a question.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoHotpotQA (revision d79c0cd…), a 50-query sample of BEIR HotpotQA.
- **Upstream licence:** CC BY-SA 4.0, per the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663). The Hugging Face card's label is CC BY 4.0.
- **Size:** 5090 documents, 50 queries, 100 judgments.
- **Labels:** human (crowdworkers wrote questions over paragraph pairs), binary (grade 1), exactly two relevant paragraphs per query.
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 50 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). The second-hop paragraph often shares few words with the question, so single-shot retrieval caps recall. NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
