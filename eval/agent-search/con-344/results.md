# CON-344: one tool each for getting, deleting and uploading

Run on 2026-11-12. The bar is in `eval/pass-bars.md` under "One tool each for getting, deleting and uploading
(CON-344)" and was committed before either arm ran.

## Verdict: PASS

Agents with the merged `get_document` answered as well as agents with the separate `get_document` and
`get_documents`. The mean score per task moved by −0.006, and the 90% BCa interval of (merged − baseline) is
[−0.021, +0.009]. Its lower end is far above the −0.05 margin.

| | Baseline | Merged |
|---|---|---|
| Tools given to the agent | `search_knowledge`, `get_document`, `get_documents` | `search_knowledge`, `get_document` |
| Score, all 112 tasks | 0.6994 | 0.6935 |
| Memory set (64 tasks) | 0.474 | 0.4635 |
| Holdout set (48 tasks) | 1.000 | 1.000 |
| Answer accuracy | 0.7411 | 0.744 |
| Search calls per run | 1.99 | 2.03 |
| Tool calls per run | 2.10 | 2.12 |
| Runs that never answered | 0 of 336 | 1 of 336 |
| Cost at API list price | $8.41 | $8.28 |

`memory-search-compare baseline.runs.jsonl merged.runs.jsonl`: mean difference −0.006, 90% interval
[−0.0208, 0.0089], 95% interval [−0.0238, 0.0089], t-test p = 0.48, permutation p = 0.73. 3 tasks scored better and
5 worse. That spread is within the ±0.014 noise the CON-244 runs showed on this interval.

**What the PASS covers.** Agents almost never asked for several documents at once, so this eval mainly proves the
single-document path and the effect of the smaller tool list. It says little about the list form:

| | Baseline | Merged |
|---|---|---|
| List-form calls | 5 (`get_documents`) | 6 (`get_document` with `files`) |
| Runs using the list form | 5 of 336 | 6 of 336 |
| Single-document `get_document` calls | 35 | 26 |
| List-form calls that returned an error | 0 | 0 |
| Mean score of runs using the list form | 0.20 | 0.00 |

Every list-form run was on an alias or location task, the hardest kinds here, which is why their scores are low in
both arms. With 5 and 6 runs the gap between them is noise. All 6 merged list-form calls were well-formed and
returned the documents, so agents read the merged tool's `files` parameter correctly. The cross-surface and unit
tests, not this eval, hold the list form to the same results as `get_documents`.

## Setup

Both arms ran `memory-search --config hybrid --split test --workers 1 --repetitions 3 --model claude-sonnet-5-5
--effort medium`, with only `--tools` differing. The baseline binary was built from a0acaea8, which is `main` at
24e6a7b7 plus the harness-only commit that adds `--tools` (nothing under `src/` changes), so its MCP tools are
`main`'s. Its agents called `get_documents` 5 times, which confirms they had it. The merged arm ran on 20d3df80. Each arm took about 41 minutes. Embeddings came from CPU-only Ollama capped at 2 CPUs and 4 GB, and
the test containers were capped at 2 CPUs and 2 GB.

`memory-search-v1` uses only the getting pair. Deleting and uploading are held to identical results by the
cross-surface and unit tests, not by an agent eval.

## Tool-list size

Counted with tiktoken `o200k_base` over the full `tools/list` JSON:

| | Tools listed | Tokens |
|---|---|---|
| Before | 31 | 6,704 |
| After | 28 | 6,573 (−131, −2.0%) |

The six separate tools took 990 tokens. The three merged tools take 859: `get_document` 299, `upload_file` 333 and
`delete_file` 227.

## Files

- `baseline.json`, `merged.json`: each arm's report.
- `baseline.runs.jsonl`, `merged.runs.jsonl`: every run, the input to `memory-search-compare`.
