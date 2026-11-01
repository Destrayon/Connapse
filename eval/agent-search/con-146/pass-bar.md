# CON-146 pass bar

Written and committed on 2026-10-29, before any scored run. The run may not change it.

## What runs
- 48 questions in `questions.json`, drawn from EnterpriseRAG-Bench's erb-50k subsample (50,000 generated documents of
  one fictional company: Slack, email, tickets, docs, CRM notes). Each question has 1 or more gold documents and a
  list of required facts written by the benchmark.
  - **36 hard** questions: the "semantic" questions, which the benchmark wrote with roundabout wording, whose words
    overlap their gold documents least. On average 47% of a hard question's content words appear in its gold
    documents (all 125 semantic questions: 63%).
  - **12 control** questions: "basic" questions chosen by a stable hash of their ID (78% overlap), to show that a fix
    does not break questions that files already answer.
- The agent sees the same 50,000 documents two ways: Connapse search over the restored CON-107 erb-50k index
  (`search_knowledge`, `get_document`; default hybrid search, no reranker), and the same text as files on disk
  (`grep_files`, `find_files`, `read_file`; CON-142's ripgrep MCP server).
- Four setups, same agent and base prompt, differing only in tools and tool instructions (`prompts/`):
  - **rag**: Connapse tools only. This is the reference for "what RAG finds".
  - **files_first**: all five tools, each described, with no advice on which to use. This is the agent as it behaves
    today with file access, and it is the baseline.
  - **rag_first**: all five tools plus instructions to use `search_knowledge` first when the question describes
    something in its own words and to grep first when it names an exact value (CON-142's routing rules).
  - **rag_always**: all five tools plus the instruction to call `search_knowledge` before any file tool on every
    question. Forced tool choice is not available on Sonnet 5.5, so this is the closest "tool order" fix.
- Agent: Claude Sonnet 5.5 through Claude Code 2.1.295 `claude -p`, built-in tools off, at most 25 turns, one attempt
  per question per setup, one question at a time.
- Grading: CON-142's judge (a separate Sonnet 5.5 call that sees the question, reference answer, required facts and
  the answer, but not the setup). Correct means every required fact is present and nothing contradicts the reference.
- Cost: `total_cost_usd` that Claude Code reports per question (agent only, not the judge).

## Measured, not gated
**Miss rate**: of the hard questions rag answers correctly, the share that files_first gets wrong. This is the
ticket's "how often going files-first misses what RAG finds".

## The bar
A fix (rag_first or rag_always) passes only if all three hold against files_first:
1. **Accuracy:** it answers at least 10 percentage points more of the 36 hard questions correctly, which means at
   least 4 more questions.
2. **Cost:** its average cost per question over all 48 is at most 20% above files_first's.
3. **No harm:** it answers at most 1 fewer of the 12 control questions correctly.

## What each outcome means
- One fix passes: **ship that instruction** in Connapse's MCP server instructions and tool descriptions. If both
  pass, ship the one with more hard questions right; on a tie, the cheaper one.
- Bar 1 passes but bar 2 or 3 fails: **change it** — the fix finds answers but needs cheaper or narrower wording
  before shipping.
- Bar 1 fails for both: **leave the tools as they are**. If the miss rate is under 10% as well, files-first missing
  RAG's answers is not a real problem at this scale and that is the finding.

## Known limits, stated before the run
- One attempt per question; a 4-question gap on 36 is near the noise floor. The results report the per-question table
  and an exact McNemar test for each fix against files_first as context; the bar above decides.
- The benchmark's own results show semantic questions are hard for every method (BM25 44.8%, vector search 32.8%, a
  Bash agent 42.4% correct), so rag may score low too. A low rag score limits how much a RAG-first fix can add.
- The documents are generated, and many near-duplicates share the company's vocabulary, which makes grep noisier than
  on a real corpus of this size.
- Connapse's MCP server instructions (which already say to prefer `search_knowledge`) reach the agent in every setup
  that has the Connapse tools, as they would in production.
