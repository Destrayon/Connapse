# CON-174: does a folder summary help when the agent reads it first?

CON-160 asked whether container summaries help agents and answered "no", but none of its 48 runs called
`container_describe`, and the summary it would have shown was built from 7 of 50,000 documents (see pass-bar.md). This
test makes the agent read a representative summary before it searches, and measures whether that helps.

The bar, the setups and the outcomes are in [pass-bar.md](pass-bar.md), written before any run. The instructions that
did and did not get agents to read the summary first are in [instructions.md](instructions.md).

## Files

- `container_summary.py` builds the container summary: 20 random records (seed 174, no gold records), each summarised
  with Connapse's per-document prompt (`summarize.py`, copied from CON-160), then Connapse's container prompt as for a clustered
  50,000-document container.
- `describe_proxy.py` sits between the agent and Connapse's MCP server and puts that summary in `container_describe`'s
  `Summary:` line. Everything else passes through unchanged. The stack runs with summaries off.
- `prompts/describe-v*.md` are the instruction candidates, tried in order in the quick tests.
- `quick-questions.json` holds the 10 quick-test questions (semantic, none of the 48 scored ones), from `run.py pick-quick`.
- `run.py` runs the quick tests, the full run, the judge and the summary. Its docstring lists the commands.
- `test_summarize.py` checks that `summarize.py`'s per-document prompt, template and truncation still match the C#
  source.
- `test_describe.py` holds offline checks: the prompt copies match the C# source, the proxy changes only the placeholder,
  and the sample is fixed. Run it with `python -m pytest test_describe.py`.

## Stack

This is CON-160's stack (CON-107's erb-50k snapshot in pgvector, CPU-only Ollama with `nomic-embed-text`, MinIO
bucket `aikp-files` filled from CON-146's `export_corpus.py`) with container names `con174-*`, Connapse on port 5174 and
summaries off. Containers are capped at 2 CPUs and 4 GB, Connapse runs at BelowNormal priority, and scratch lives in
`E:/tmp/con-174`.
