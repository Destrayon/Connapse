# Agent search eval (CON-142)

Does an agent answer better with Connapse RAG, with plain file search, or with both? Results and recommendation:
`results.md`. Pass bar, set before the run: `pass-bar.md`. Background research:
`docs/research/agent-file-search-plus-rag-2026-10-28.md`.

## Files

| File | What it does |
|---|---|
| `questions.json` | 48 questions (code/docs × exact/concept) with reference answers, required facts and evidence lines |
| `prompts/` | Shared base prompt and the per-setup tool guidance |
| `ingest.py` | Uploads a corpus into a Connapse container, waits for ingestion, creates a read-only PAT |
| `shrink_corpus.py` | Cuts the container to the evidence files plus N distractors and copies the same files out |
| `run_agent.py` | Runs `claude -p` per question for one setup (`rag`, `files`, `both`) |
| `judge.py` | `judge` grades answers with a separate model call; `summary` prints the score table and tool choice |
| `chunker_probe.py` | Times Semantic against Recursive ingestion on unseen files |
| `chunks_vs_files.py` | Literal search over stored chunks in Postgres against ripgrep over the original files |
| `chunk_coverage.py` | Share of source lines that survive word for word in the stored chunks |
| `bench_*.tsv` | grep / ripgrep / Zoekt / ast-grep timings |
| `scripts/` | The benchmark scripts that produced `bench_*.tsv`: grep, ripgrep, ast-grep and Zoekt |
| `results/` | Judged answers per setup and the measurement outputs |

The file-search tools live in `tools/Connapse.AgentSearch.Prototype` (an MCP server over ripgrep).

## Reproduce

Needs a running Connapse (the run used `http://localhost:5142`) with Ollama `nomic-embed-text`, Claude Code, and
ripgrep. The PAT goes in the `X-Api-Key` header, which `run_agent.py` sets from `CON142_PAT`. Keep the PAT file outside the
checkout so it cannot be committed.

```bash
python ingest.py --base http://localhost:5142 --email ... --password ... --corpus <corpus> --pat-out ~/con142-pat.txt
CON142_PAT=$(cat ~/con142-pat.txt) python run_agent.py --setup both --out runs --corpus <corpus> \
    --tool-server <Connapse.AgentSearch.Prototype.exe> --rg <rg.exe> --cwd <empty dir>
python judge.py --out runs judge && python judge.py --out runs summary
```
