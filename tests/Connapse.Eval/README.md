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

## Comparing configs

Indexing is most of a run's time, and configs that differ only in how they search can share an index.
Give `run` several configs to index each dataset once and search it under each:

    dotnet run --project tests/Connapse.Eval -- run --suite enterprise-v1 --config hybrid,hybrid-rerank,hybrid-a075

- Each config still gets its own run folder, so `compare` works as usual.
- Configs are grouped by their index-time settings, and each group gets its own index:
  - **Search-time:** the `Knowledge:Search:` keys in `SystemConfig.SearchTimeKeys`, plus `searchMode` and `captureCandidates`.
  - **Index-time:** everything else, including embedding, chunking, upload settings and `chunkingStrategy`.
  - A setting not on the search-time list counts as index-time.
- Search settings are applied between passes through the Settings store, as the Settings page does. The run stops if the live settings don't match the config.
- `--resume` takes one config. Resume each run folder on its own.

## Index cache

A run that indexes every dataset of its suite saves the built index, and a later run with the same key restores it in minutes instead of indexing for hours.

- **Storage:** the PostgreSQL database goes into a Docker volume, `connapse-eval-index-<key>`, one generation folder per save. The harness's per-dataset state goes into `eval/.cache/index-snapshots/<key>.json`, which names the generation it belongs to and is written only after that dump completes.
- **What the key covers:**
  - the datasets: names, versions and file hashes;
  - the index-time settings and chunking strategy;
  - non-search `Knowledge__` environment variables;
  - the contents of the source that ingests documents: `src/**` except Connapse.Search and Connapse.Agents, plus the eval harness's `Datasets`, `Model` and `Systems` folders and the root build files.
- **When the key changes:** changing search code keeps the key. Changing ingestion, storage, the web project or a dataset makes a new one. Uncommitted edits count, and so do new files git doesn't ignore. Files git ignores don't, so the key ring the host writes into `src/Connapse.Web/appdata/` on startup can't change the key.
- **Recording:** the run manifest's `indexCache` says whether a run `saved` or `restored` its index.
- **Turning it off:** `--index-cache off` neither reads nor writes the cache.
- **Freeing space:** delete old snapshots with `docker volume rm connapse-eval-index-<key>` and the matching JSON file.

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
downloaded (needs Python with `pyarrow`, `requests`, `numpy` and PyStemmer; about 20 minutes, 1.4 GB of
disk, and a peak of about 2 GB of memory):

    python eval/tools/build_enterprise_suite.py
    dotnet run --project tests/Connapse.Eval -- run --suite enterprise-v1 --config hybrid

- Scores break down by question category (`kind:`) and by the source holding the answer (`source:`).
- `enterprise-full` is the whole 512,000-document corpus; use it to check that an `enterprise-v1` result
  holds at scale.
- `enterprise-10k` is a fixed 10,000-document subset of `enterprise-v1` holding every graded document
  (`eval/datasets/erb-10k/card.md`). It indexes in a few hours under `eval/tools/run_light.sh` with the GPU
  (one embedding request in flight) instead of about 15, so use it for routine no-harm checks against a
  baseline on the same set:

      EVAL_GPU=1 EVAL_CONFIGS=hybrid,hybrid-links eval/tools/run_light.sh enterprise-10k

## Link questions

`links-v1` asks for a record linked to the one a question describes: the pull request that fixed an issue, a
ticket's parent, its duplicates, the other sub-tasks of its parent. It is the yardstick for graph retrieval;
the bars it must clear are in `eval/pass-bars.md`. Records are snapshots of Connapse's own GitHub issues and
pull requests and of Apache Flink's public Jira, rendered with the connectors' own renderers; the answers come
from the GitHub and Jira APIs. Built locally (needs `gh` signed in; about 15 minutes):

    python eval/tools/build_link_suite.py links-connapse-github
    python eval/tools/build_link_suite.py links-flink-jira
    dotnet run --project tests/Connapse.Eval -- run --suite links-v1 --config hybrid

- Scores break down by `kind:` and by `hops:1` / `hops:2`. Questions and how they were made: `eval/datasets/links-*/card.md`.

## Memory and search

`memory-search` and `memory-learned` score how well a Claude agent answers with search alone, through the real MCP
tools `search_knowledge` and `get_document`. A run scores 1 when the agent cites the current document and no
superseded one. CON-244 compared these runs with container memory, which CON-255 removed because it did no better,
so these memory-off scores are the ones to match; the rules and results are in `eval/pass-bars.md`. `memory-search`
registers 112 test tasks; `memory-learned` registers its 104 session-2 test tasks, after uploading the late
documents. Any other run, or fewer than 3 repetitions, is marked diagnostic only. The run stops before any agent
starts if a document fails to index.

Each agent is a headless Claude Code session (`claude -p`) signed in with your Claude subscription, so `claude`
must be on the PATH and logged in. The eval serves its tools to the session as a loopback MCP server and starts
Claude Code with no built-in tools, no settings, CLAUDE.md, skills or plugins, and with `ANTHROPIC_API_KEY`,
`ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_BASE_URL` removed, so usage comes out of the subscription. The report's
`subscription` section shows the rate-limit windows at the first and last run, and `tokens.apiListPriceUsd` is
what the same tokens would cost at API list price. If the subscription limit is reached, the run stops. Try
`--split dev --repetitions 1` first:

    dotnet run --project tests/Connapse.Eval -- memory-search --split test --out eval/runs/memory-search.json
    MEMORY_SEARCH_SPLIT=dev eval/tools/run_light.sh memory-search

- Options: `--model` (default `claude-sonnet-5-5`), `--effort` (default `medium`), `--repetitions` (default 3),
  `--workers` (default 1), `--limit-tasks`, `--agent-dir` (the empty folder Claude Code runs in; default under the
  temp folder). Every agent run, with its tool calls, lands in `<out>.runs.jsonl`.
- Tasks and corpus, and how they were made: `eval/datasets/memory-search/card.md` and
  `eval/datasets/memory-learned/card.md`.

## Table approaches side by side

`tables-arms` asks the tables questions of an agent given one arm's tools (CON-327, then CON-336 with a second
set). Arm `structured`, the only arm, gets `table_list`, `table_describe`, `table_read`, `table_aggregate`,
`search_knowledge` and `get_document`. The free-form SQL and GraphQL arms were removed with their tools in CON-345
(`docs/research/con-345-free-form-table-tools-removed.md`). Data, prompt, agent and grading are the `tables-rag`
setup's. Each question runs once by default (CON-336's bar); with `--runs 3` a question counts as right when 2 of the
3 runs are. `--set flex` asks the flexibility set (`eval/datasets/tables/flex`) instead, its six files uploaded into
one container. The report gives the arm's accuracy (overall, per family, admin and restricted), tool calls, search
calls, confident wrong answers, tokens, cost and wall time. The run stops before starting anything if the build lacks
one of the arm's tools. Each run waits for 8 GB of free memory (`--min-free-gb`). It needs Docker and Ollama for the
search side. The tables-v1 CSVs are built locally, not downloaded, so build them once before the first run, then try
a slice:

    python eval/tools/tables/build_tables.py
    dotnet run --project tests/Connapse.Eval -- tables-arms --split tune --limit-tasks 10 --out eval/runs/tables-arms-dry.json

- Options: `--arms structured` (the default), `--runs` (default 1), `--set tables-v1|flex`, and the `tables-rag`
  options except `--skip-mixed` (mixed questions are not asked). Anything but 1 run and the set's full test questions
  (180 for tables-v1, 70 for flex) is marked diagnostic only. Every run lands in `<out>.runs.jsonl` with its arm, run
  number, calls, cost and wall time as soon as it finishes, so a run that is cut short keeps what it measured; a
  finished run rewrites the file in schedule order. Run the same command again with `--resume` to keep those saved
  runs and ask only the missing ones; the report counts them under `resumedRuns`.
- `tables-arms-score --runs-file <out>.runs.jsonl --out <file>` builds the report from those lines without asking
  anything again, using each run's recorded grade. Pass the same options the run used; it needs no Docker or Ollama.

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
