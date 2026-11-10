# memory-agent-sealed-1

**Sealed: don't open these files while changing memory search.** This set decides whether the memory search change
planned in CON-313 replaces today's tools (the bar is in `eval/pass-bars.md`). It stays a fair test only while
nobody tunes search code, prompts or wording to its questions, so the files are committed gzipped and read only by
the `memory-agent` command. Score it with `--dataset memory-agent-sealed-1`; tune against `memory-agent` instead.

- **Built by** `python -I eval/tools/build_memory_agent.py eval/datasets/memory-agent-sealed-1 --spec
  eval/tools/memory-agent-sealed-1.spec.json.gz` (CON-321), with the same wording checks as `memory-agent`. The spec
  holds the facts and tasks, gzipped for the same reason, and the output is the same bytes on every run.
- **Facts** (`facts.jsonl.gz`, 252): a project manager's memory at the made-up company Quorvane Logistics. 102 are
  hand-written, 9 of them with an older value that was later corrected; 150 are filler about 30 made-up services.
  Two subjects have no purpose fact, so a question that describes one of them has to go through its other facts.
- **Tasks** (`tasks.jsonl.gz`), all in the test split: 8 exact, 30 reworded, 18 indirect, 12 partial and 8 absent
  find tasks, and 10 new, 8 update and 6 duplicate save tasks. The kinds mean the same as in `memory-agent`'s card.
