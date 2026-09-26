# ragbench-emanual

- **What it tests:** consumer device manual retrieval: find the Samsung TV and remote manual passages that answer a user's question.
- **Source:** https://huggingface.co/datasets/galileo-ai/ragbench (revision 97808f3…), subset `emanual`. RAGBench (Friel, Belyi and Sanyal, https://arxiv.org/abs/2407.11005) built it from E-Manual QA (Nandy et al., EMNLP Findings 2021).
- **Upstream licence:** RAGBench is released under CC BY 4.0 (paper and Hugging Face card agree). The underlying E-Manual repository (https://github.com/abhi1nandy2/EMNLP-2021-Findings) is Apache-2.0; the manual text itself is Samsung's.
- **Size:** 222 documents (every distinct document in train, validation and test), 132 queries (66 Dev from validation, 66 Test from test; each is a distinct question — see below), 448 judgments (222 Dev, 226 Test).
- **Labels:** LLM-annotated relevant-sentence keys (not human); document relevance is derived from the sentence keys. The annotator was GPT-4 (gpt-4-0125-preview). Binary (grade 1).
- **Split rule:** validation → Dev (the only split to tune on), test → Test (headline numbers).
- **Duplicate rows:** the source lists every question twice per split, each row carrying its own LLM annotation; the adapter merges the pair into one query per distinct ID with the union of their relevant documents.
- **Known limitations:** only 222 distinct passages, shared heavily across questions, so the task is easy to saturate and 132 Test queries give a large MDE. 16 test rows and 2 validation rows list the same passage twice; the adapter collapses each pair to one document. No relevance key points past the end of its row's document list (0 of 940 validation keys and 0 of 870 test keys), so the adapter's out-of-range skip drops nothing.
