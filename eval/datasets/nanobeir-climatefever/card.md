# nanobeir-climatefever

- **What it tests:** climate claim verification: find Wikipedia passages that support or refute a real-world climate claim.
- **Source:** https://huggingface.co/datasets/zeta-alpha-ai/NanoClimateFEVER (revision 96741bf…), a 50-query sample of BEIR Climate-FEVER.
- **Upstream licence:** not stated by the original authors. the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663) lists Climate-FEVER among the datasets with no reported licence, the repository https://github.com/tdiggelm/climate-fever-dataset has no licence file, and the authors' own Hugging Face upload (tdiggelm/climate_fever) says "unknown". The Hugging Face card's label is CC BY 4.0.
- **Size:** 3408 documents, 50 queries, 148 judgments.
- **Labels:** human (annotators judged evidence sentences), binary (grade 1).
- **Split rule:** source ships test only → every query is Test; never tune against it.
- **Known limitations:** 50 queries → the minimum detectable nDCG@10 difference is large (see each report's MDE). Evidence labels are known to be noisy, so absolute scores are low for every system. NanoBEIR ships no title column, so documents are indexed as body text only and reports show a text snippet in place of a title.
