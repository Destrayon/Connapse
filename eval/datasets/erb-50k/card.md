# erb-50k

- **What it tests:** retrieval over enterprise data shapes: Slack threads, email, Linear and Jira tickets, HubSpot CRM records, Fireflies meeting transcripts, GitHub pull requests, Confluence pages and Google Drive files (#662). Scores break down by question category (`kind:`) and by the source holding the answer (`source:`).
- **Source:** [EnterpriseRAG-Bench](https://github.com/onyx-dot-app/EnterpriseRAG-Bench) (Onyx), Hugging Face `onyx-dot-app/EnterpriseRAG-Bench` at revision `69916e31`.
- **Upstream licence:** MIT. Built into `eval/.cache` by `eval/tools/build_enterprise_suite.py`; never committed.
- **Corpus:** 50,000 of the benchmark's 511,958 LLM-generated documents of one fictional company:
  - all 722 gold documents;
  - 38,624 hard distractors: each scored question's top 100 by BM25 over the whole corpus;
  - 10,654 more chosen by a stable hash of the document ID.

  Pooling by question skews the sources toward what questions ask about: Slack is 34% here against 56% in the full corpus.

  Four document IDs appear twice upstream (three pair different sources, one is a Jira ticket stored twice); the build keeps the first copy of each and lists the dropped IDs in build.json.
- **Questions:** 470 of 500, scored on document retrieval against `expected_doc_ids`. "High level" (10) and "info not found" (20) have no gold documents; the benchmark grades them on generated answers, so they're left out.
- **Reference:** BM25 over the full corpus finds 81.7% of gold documents in its top 100.
- **Known limitations:**
  - Generated text: realistic structure and noise, but not real users' writing.
  - The benchmark's own leaderboard scores LLM-judged answers; these numbers are retrieval only and are not comparable to it.
