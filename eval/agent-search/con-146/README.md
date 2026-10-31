# CON-146: files-first versus RAG on hard questions

This asks whether an agent that has both file search and Connapse search misses answers that RAG alone would find,
on questions whose wording does not match the records. It also asks whether tool instructions fix that. The pass
bar is in `pass-bar.md` and the results are in `results.md`. The work reuses CON-142's runner, judge and ripgrep MCP
server (`../run_agent.py`, `../judge.py`, `tools/Connapse.AgentSearch.Prototype`), all unchanged.

| File | What it does |
|---|---|
| `select_questions.py` | Picks the 36 hard and 12 control questions from erb-50k and writes `questions.json` with word-overlap and fact-grounding numbers. |
| `export_corpus.py` | Writes the 50,000 records to disk under the same paths and text the eval harness uploaded to Connapse, and checks the paths against Connapse's. |
| `run.py` | Runs a setup (`run`), grades answers (`judge`), and prints the tables and pass-bar checks (`summary`). |
| `prompts/` | The base prompt plus each setup's tool description and instructions. |
| `results/` | Every judged answer with its tool calls, plus the summary output. |

## Reproducing
1. Restore CON-107's erb-50k index snapshot into a pgvector container. Its pg_dump generation
   `584b7e6093414329893c6dfd8c529c42` holds the `eval-erb-50k` container.
2. Start a CPU-only Ollama with `nomic-embed-text`.
3. Run Connapse.Web against that database with the eval harness's admin credentials (see
   `tests/Connapse.Eval/Systems/EvalHost.cs`), and create a `knowledge:read` PAT.
4. Run `export_corpus.py`, then use `run.py run` once for each setup, then `run.py judge` and `run.py summary`.

Each command's arguments are in the docstrings. Cap every container (`--cpus 2 --memory 4g`) and run one question at a
time. The four setups cost about $37 in agent calls.

## Sources
- EnterpriseRAG-Bench, Onyx, MIT license: the paper (https://arxiv.org/abs/2605.05253) and its repository
  (https://github.com/onyx-dot-app/EnterpriseRAG-Bench). This is where the corpus, questions and required facts
  come from. It is also the source of the semantic questions' "low keyword overlap" design and the baseline numbers
  for BM25, vector search and Bash agents on them.
- Anthropic, "Define tools" (https://platform.claude.com/docs/en/agents-and-tools/tool-use/define-tools): tool
  descriptions are the main lever on which tool a model picks, so the fixes here are written as instructions.
- Anthropic API errors, "forced tool use not supported"
  (https://platform.claude.com/docs/en/api/errors#forced-tool-use-not-supported): Sonnet 5.5 rejects a forced
  `tool_choice`, so "tool order" can only be tested as an instruction (rag_always).
