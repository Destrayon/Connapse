# cqadupstack-programmers

- **What it tests:** duplicate-question retrieval on a software engineering forum: given a question title, find the earlier question it duplicates (title + body).
- **Source:** https://huggingface.co/datasets/mteb/cqadupstack-programmers (revision 339629e…), the BEIR CQADupStack Programmers subforum.
- **Upstream licence:** Apache License 2.0, per the BEIR paper, Appendix E (https://arxiv.org/abs/2104.08663). The Hugging Face card's label (Apache-2.0) matches.
- **Size:** 32176 documents, 876 queries, 1675 judgments.
- **Labels:** human (forum users and moderators marked duplicates), binary (grade 1).
- **Split rule:** the harness downloads the test qrels only → every query is Test; never tune against it.
- **Known limitations:** duplicates the forum never flagged are unjudged and score as misses. The largest corpus in v1, so it dominates ingestion time.
