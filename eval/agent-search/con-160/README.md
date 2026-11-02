# CON-160: do document and container summaries help agents find answers they miss today?

This reruns CON-146's `files_first` agent (today's tools) next to the same agent with summaries: each
`search_knowledge` hit carries its record's document summary, and `container_describe` returns the container summary.
The pass bar is in `pass-bar.md`, written before any run. It reuses CON-146's questions, prompts, judge and file tools
and CON-142's runner, all unchanged.

| File | What it does |
|---|---|
| `summarize.py` | Writes a document summary with Connapse's own per-document prompt and truncation, answered by Claude Haiku through `claude -p`, and caches it. |
| `test_summarize.py` | Checks that the copied prompt, template and truncation still match the C# source. |
| `summary_proxy.py` | MCP stdio proxy in front of Connapse's `/mcp`: forwards every tool, and adds a `Document summary:` line to each `search_knowledge` hit. |
| `ollama_shim.py` | Answers Ollama's `/api/chat` with Haiku, so Connapse's own rollup job writes the container summary. |
| `run.py` | Runs a setup (`run`), grades answers (`judge`), lists the reruns the no-drop rule needs (`tiebreak`), and prints the pass-bar checks (`summary`). |
| `prompts/summaries.md` | The two sentences the summaries setup adds to CON-146's prompt. |

## Reproducing
1. Restore CON-107's erb-50k snapshot (pg_dump generation `584b7e6093414329893c6dfd8c529c42`) into pgvector, start a
   CPU-only Ollama with `nomic-embed-text`, and fill MinIO bucket `aikp-files` under `<containerId>/` from CON-146's
   `export_corpus.py` output, so `get_document` can read the records. Set `Knowledge__Storage__MinIO__BucketName=aikp-files`:
   `appsettings.json` names `connapse-files`, and with the wrong bucket every read still fails as "could not be read
   from storage".
2. Start `ollama_shim.py`, then Connapse.Web with `Knowledge__Summary__Enabled=true`, `Knowledge__LLM__Provider=Ollama`
   and `Knowledge__LLM__BaseUrl` pointing at the shim. The 5-minute summary sweep writes the container summary.
3. Create a `knowledge:read` PAT, then `run.py run` for `baseline` and `summaries`, `run.py judge`, `run.py tiebreak`
   and the reruns it lists, and `run.py summary`. Arguments are in the docstrings.

Cap every container (`--cpus 2 --memory 4g`) and run one question at a time.
