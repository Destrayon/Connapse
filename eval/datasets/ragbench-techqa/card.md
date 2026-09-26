# ragbench-techqa

- **What it tests:** technical support retrieval over IBM product documentation: find the Technote passages that answer a user's support question.
- **Source:** https://huggingface.co/datasets/galileo-ai/ragbench (revision 97808f3…), subset `techqa`. RAGBench (Friel, Belyi and Sanyal, https://arxiv.org/abs/2407.11005) built it from TechQA (Castelli et al., 2020): questions from IBM Developer forums answered by IBM Technotes.
- **Upstream licence:** RAGBench is released under CC BY 4.0 (paper and Hugging Face card agree). The underlying TechQA repository (https://github.com/IBM/techqa) is Apache-2.0; IBM distributed the full dataset after registration.
- **Size:** 4066 documents (every distinct document in train, validation and test; train documents act as distractors), 309 queries (152 Dev from validation, 157 Test from test; each is a distinct question — see below), 888 judgments (468 Dev, 420 Test).
- **Labels:** LLM-annotated relevant-sentence keys (not human); document relevance is derived from the sentence keys. The annotator was GPT-4 (gpt-4-0125-preview). Binary (grade 1).
- **Split rule:** validation → Dev (the only split to tune on), test → Test (headline numbers).
- **Duplicate rows:** the source lists every question twice per split, each row carrying its own LLM annotation; the adapter merges the pair into one query per distinct ID with the union of their relevant documents.
- **Known limitations:** 50 of 314 Test queries (and 16 of 304 Dev) have no relevant sentence key, so they count as no-answer and are excluded from averages. Each question ships with only the few documents RAGBench's own retriever found, so every document was already judged plausible for some question. No relevance key points past the end of its row's document list (0 of 4422 validation keys and 0 of 3929 test keys), so the adapter's out-of-range skip drops nothing.
