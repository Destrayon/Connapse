# CON-146 results: do agents that go to the files first miss answers RAG would find?

Run on 2026-10-29 against the pass bar in `pass-bar.md`, which was committed before any scored run.

## In plain words
The agent never actually went to the files first. Given both kinds of tools, the "as it is today" setup (files_first)
called `search_knowledge` first on all 48 questions, then spent about half of its tool calls on grep and file reads.
Connapse's MCP server instructions already tell agents to prefer `search_knowledge`, and the agent follows them.

Having the files next to RAG helped more than it hurt. On the 36 hard questions it missed 2 of the 15 that RAG alone
answered (13%), but answered 6 that RAG alone missed. That is 19 against 15, for 9% more cost.

Neither new instruction passed the bar. Telling the agent to route by question type (rag_first) changed nothing on
the hard questions. Telling it to always search Connapse first (rag_always) answered 3 more hard questions and cost
11% less. The bar needed 4 more, and a gap of 3 out of 36 is within run-to-run noise (exact McNemar p = 0.45).

**Recommendation: leave the tools and instructions as they are.** That is the bar's outcome when both fixes fail
accuracy. The problem the ticket describes did not show up on this set: with Connapse's tools present, agents start
with RAG and use files to confirm and fill gaps. rag_always is the only change worth retesting, because it was
cheaper and did no harm. That retest should use more questions and repeated runs, and is not something to ship on
this evidence.

## Numbers
48 questions from EnterpriseRAG-Bench's erb-50k subsample (50,000 generated records of one company). 36 are hard:
the benchmark's "semantic" questions whose words overlap their gold record least, averaging 47% overlap. 12 are
controls: "basic" questions, averaging 78% overlap. The agent is Sonnet 5.5 through Claude Code 2.1.295 with one
attempt per question, and answers are graded by a separate Sonnet 5.5 judge.

| Setup | Hard correct (of 36) | Control correct (of 12) | Cost per question | Tool calls | First search is RAG | Questions with no file call |
|---|---|---|---|---|---|---|
| rag (reference) | 15 (42%) | 12 | $0.188 | 5.8 | 48/48 | 48/48 |
| files_first (baseline) | 19 (53%) | 12 | $0.204 | 8.4 | 48/48 | 15/48 |
| rag_first | 19 (53%) | 11 | $0.195 | 7.5 | 48/48 | 14/48 |
| rag_always | 22 (61%) | 12 | $0.181 | 6.8 | 48/48 | 19/48 |

| Fix vs files_first | Hard (bar: at least +4) | Cost (bar: at most +20%) | Control (bar: at most 1 fewer) | Won / lost on hard | Verdict |
|---|---|---|---|---|---|
| rag_first | +0 | −4.2% | −1 | 4 / 4 | fails accuracy |
| rag_always | +3 (+8.3 points) | −11.3% | +0 | 5 / 2 (p = 0.45) | fails accuracy |

**Miss rate** is the share of hard questions that rag answered and files_first got wrong: 2 of 15 (13%). In one,
files_first left out one of four required facts. In the other, it found nearby records but not the design the
question described. Going the other way, files_first answered 6 hard questions that rag missed.

Tool calls in files_first were 156 `search_knowledge`, 30 `get_document`, 139 `grep_files`, 72 `read_file` and 7
`find_files`. The per-question table is in `results/summary.txt`, and every answer with its tool calls and judge
verdict is in `results/<setup>/judged.jsonl`.

## Limits
- With one attempt per question, gaps of 3 to 4 questions are near the noise floor. Every bar decision here is a
  fail on accuracy, and no fix lost on cost or on the controls.
- In this harness all five tools are MCP tools, and Connapse's server instructions reach the agent. An agent whose
  file access comes from its own built-in tools (for example Claude Code's Grep and Read inside a container), or one
  that drops MCP server instructions, may still go to the files first. This run does not measure that case.
- The RAG side is Connapse's default hybrid search with no reranker, on CON-107's nomic-embed-text index. The
  benchmark's semantic questions are hard for every method (its paper reports 33–45% correct), and rag's 42% is in
  that range.
- The judge prompt is CON-142's, with one word changed: "about a codebase" now reads "about a company's records".
- In the rag setup the agent tried 5 calls to tool names that do not exist (`get_document_placeholder`,
  `connapexpress`). Those calls failed harmlessly and are counted in its tool calls.
