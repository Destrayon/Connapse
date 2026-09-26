# Connapse eval harness

Measures retrieval quality across a portfolio of public datasets by indexing them through Connapse's real
upload and ingestion path and searching through `IKnowledgeSearch`. Design:
`docs/superpowers/specs/2026-09-25-eval-core-harness-design.md`.

Needs Docker (throwaway PostgreSQL + MinIO) and the embedding provider the config uses (Ollama by default).

    dotnet run --project tests/Connapse.Eval -- datasets verify --suite v1
    dotnet run --project tests/Connapse.Eval -- run --suite v1 --config hybrid
    dotnet run --project tests/Connapse.Eval -- run --suite v1 --config keyword
    dotnet run --project tests/Connapse.Eval -- compare eval/runs/<keyword-run> eval/runs/<hybrid-run>

- Datasets, versions and checksums: `eval/MANIFEST.json`; one card per dataset in `eval/datasets/`.
- Configs: `eval/systems/connapse/*.json` (`searchMode` plus Connapse configuration keys).
- Runs land in `eval/runs/` (gitignored) with `report.html`; downloads and the embedding cache in `eval/.cache/`.
- Headline numbers use the test split. Tune only on dev splits (RAGBench validation).
- A difference is significant when its Holm-corrected permutation p < 0.05; each report prints the minimum
  detectable effect, so "no difference" and "too few queries to tell" read differently.
- `pool` lists top-10 documents that have no judgment; grading them is sub-project 2.
