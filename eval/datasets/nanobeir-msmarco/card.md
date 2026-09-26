# nanobeir-msmarco

- **What it tests:** web passage retrieval: find a passage that answers a real Bing search query.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoMSMARCO (revision 7b8ff22…), a 50-query sample of BEIR MS MARCO (passage).
- **Upstream licence:** MS MARCO's terms allow non-commercial research use only (BEIR describes it as "MIT License for non-commercial research purposes", the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663); terms at https://microsoft.github.io/msmarco/). The harness only downloads the files at run time and never redistributes them. The Hugging Face card's label is CC BY 4.0.
- **Size:** 5043 documents, 50 queries, 50 judgments.
- **Labels:** human (Bing annotators marked passages used in an answer), binary (grade 1), one relevant passage per query.
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 50 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). Judgments are sparse: other passages that answer the query are unjudged and score as misses, so check `pool` output before reading a loss as real. NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
