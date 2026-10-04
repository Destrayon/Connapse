# Connapse eval harness

Measures retrieval quality across a portfolio of public datasets by indexing them through Connapse's real
upload and ingestion path and searching through `IKnowledgeSearch`. Design:
`docs/superpowers/specs/2026-09-25-eval-core-harness-design.md`.

Needs Docker (throwaway PostgreSQL + MinIO) and the embedding provider the config uses (Ollama by default).
During a run Testcontainers publishes those throwaway PostgreSQL and MinIO containers with default credentials on
host ports, so run it on a trusted machine.

    dotnet run --project tests/Connapse.Eval -- datasets verify --suite v1
    dotnet run --project tests/Connapse.Eval -- run --suite v1 --config hybrid
    dotnet run --project tests/Connapse.Eval -- run --suite v1 --config keyword
    dotnet run --project tests/Connapse.Eval -- compare eval/runs/<keyword-run> eval/runs/<hybrid-run>
    dotnet run --project tests/Connapse.Eval -- vector-index --suite dev   # production vector index vs exact search

## Extraction runs

`extract` checks what Connapse's parsers and chunker keep from raw files (PDF, DOCX, PPTX, text) before any
retrieval. It uploads each file under its own extension, then checks the parsed text and the stored chunks:
olmOCR-bench checks for text present, headers and footers absent, reading order and table cells; a
fails-loudly check for files Connapse cannot read; and a no-silent-failure check on every file. Design:
`docs/superpowers/specs/2026-09-27-raw-file-extraction-evals-design.md`.

    dotnet run --project tests/Connapse.Eval -- datasets pin --suite extract-v1     # first time: downloads ~335 MB
    dotnet run --project tests/Connapse.Eval -- extract --suite extract-v1
    dotnet run --project tests/Connapse.Eval -- compare eval/runs/<before> eval/runs/<after>

- It needs Docker but not Ollama: chunks are embedded with an offline hashing embedder. The Semantic chunker
  places boundaries by embedding similarity, so pass `--real-embedder` (with the config's provider running)
  when chunk-level results must match production.
- The headline numbers are the silent-failure rate, the fails-loudly pass rate, and olmOCR's native-PDF score
  at the parsed and chunk levels. Scanned-PDF categories are reported apart from the headline, since
  Connapse has no OCR.
- The no-silent-failure check catches missing text, not garbled text: a Latin-1 file decoded as UTF-8 is
  indexed with replacement characters and still passes it. Its `present` checks fail instead.
- Every run writes `documents/<dataset>.jsonl` (one row per file) and `checks/<dataset>.jsonl` (one row per
  check and level).

## PDF question answering

`pdfqa-v1` asks questions whose answers sit in a table cell, a column, a sentence split by a running
header, or a scan, and scores whether the passage holding the answer is retrieved. Use it to judge parser
work by its effect on answers; re-run it after parsing changes and compare against the previous run.

    dotnet run --project tests/Connapse.Eval -- datasets fetch --suite pdfqa-v1
    dotnet run --project tests/Connapse.Eval -- run --suite pdfqa-v1 --config hybrid

- Each retrieved chunk is judged against the question's evidence strings (`eval/datasets/pdfqa/card.md`);
  rankings are over chunks, not documents.
- Scores are broken down by question kind (`kind:table-ruled`, `kind:scan-degraded`, ...) in the console
  and in `report.html`.

## Enterprise data

`enterprise-v1` scores retrieval over the shapes Connapse's connectors bring in: Slack threads, email,
Linear and Jira tickets, HubSpot records, meeting transcripts, GitHub pull requests, Confluence pages and
Drive files, from EnterpriseRAG-Bench (`eval/datasets/erb-50k/card.md`). It is built locally, not
downloaded (needs Python with `pyarrow` and `requests`; about 20 minutes and 1.4 GB):

    python eval/tools/build_enterprise_suite.py
    dotnet run --project tests/Connapse.Eval -- run --suite enterprise-v1 --config hybrid

- Scores break down by question category (`kind:`) and by the source holding the answer (`source:`).
- `enterprise-full` is the whole 512,000-document corpus; use it to check that an `enterprise-v1` result
  holds at scale.

## Files and reports

- Datasets, versions and checksums: `eval/MANIFEST.json`; one card per dataset in `eval/datasets/`.
- Configs: `eval/systems/connapse/*.json` (`searchMode` plus Connapse configuration keys).
- Runs land in `eval/runs/` (gitignored) with `report.html`; downloads and the embedding cache in `eval/.cache/`.
- Headline numbers use the test split. Tune only on dev splits (RAGBench validation) or the `dev` suite.
- The `dev` suite is eight BEIR domains (100 dev queries each, disjoint from the NanoBEIR test queries) for tuning
  search settings. Its files are built locally, not downloaded: start the TEI container shown in
  `eval/tools/build_dev_suite.py`, then `python eval/tools/build_dev_suite.py` (about an hour, mostly MS MARCO),
  and run with `--suite dev`. Its reports score these dev queries as the test split; the suite shares no dataset with `v1`.
- A difference is significant when its Holm-corrected permutation p < 0.05; each report prints the minimum
  detectable effect, so "no difference" and "too few queries to tell" read differently.
- `--limit-queries N` is for quick smoke runs: it keeps up to N test and up to N dev queries per dataset, each
  in source order. A resumed run must use the same limit it was started with.
- `pool` lists top-10 documents that have no judgment; grading them is sub-project 2.
- Exit codes: 0 success; 1 hard failure (checksum, invalid dataset, download timeout); 2 usage error; 130 cancelled with Ctrl+C.
