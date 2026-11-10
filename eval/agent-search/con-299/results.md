# CON-299 results: how well does text search answer table questions?

**rag answers 51 of the 180 `test` questions (28.3%).** This is the baseline the tables container must beat under
the bar in `eval/pass-bars.md` ("Tables container (CON-E6)"). The run conforms to that bar's setup.

## The run

- **Data:** `tables-v1`, the 180 `test` questions and the 30 mixed questions, one attempt each, 210 agent runs.
- **Setup (rag):** the CSV files in a documents container, parsed as text by TextParser with default chunking, searched
  with `search_knowledge` and `get_document` on default hybrid search. Every answer is typed JSON from the answer
  tool, graded by `TableAnswerGrader`. No judge model.
- **Agent:** `claude -p` with claude-sonnet-5-5 at effort medium, at most 25 tool calls, on the Claude subscription.
- **Embeddings:** CPU-only Ollama capped at 2 CPUs and 4 GB, with the eval embedding cache.
- **When:** 2026-11-09 23:46 to 2026-11-10 00:29 UTC. Exit code 0.
- **Usage:** the subscription's seven-day window went from 25% to 26%. At API list price the run would have cost
  $20.70, or $0.0931 per question.
- **Files:** `rag.json` is the report and `rag.runs.jsonl` holds every run with its tool calls, answer and grade.

## Results

| Family | Correct | Accuracy | Tool calls |
|---|---|---|---|
| lookup | 25/30 | 83.3% | 2.83 |
| seen_as_of | 11/30 | 36.7% | 2.33 |
| group_topk | 7/30 | 23.3% | 3.87 |
| sum_avg | 4/30 | 13.3% | 5.67 |
| filter_count | 3/30 | 10.0% | 4.93 |
| join | 1/30 | 3.3% | 5.53 |
| **All test** | **51/180** | **28.3%** | 4.19 |
| Mixed set | 1/30 | 3.3% | 6.63 |

- **Admin user:** 51/168. **Restricted user:** 0/12, with 0 leaks (no answer equal to the admin answer).
- **seen_as_of:** 0/16 for questions about the first inventory load and 11/14 for the second. A documents container
  keeps one version of a file at a path, so the first load is gone once the second is ingested.
- **Outcomes:** 155 runs answered and 55 ended without calling the answer tool. 26 of the answered runs answered
  "Unable to determine…". No run reached the 25-call cap.

## Why text search fails on tables

Search returns a few chunks of rows, which is enough to look up one row (lookup, 83%) but not to count, sum, rank or
join over a whole table. The agent then calls `get_document` to read the file: 143 of its 182 `get_document` calls
returned more than 100,000 characters (orders.csv returns 6.3 million), past the size Claude Code passes on from an
MCP tool, so the agent sees only the start of the file. Most runs give up there. The 55 runs without an answer
stopped after 2–5 calls, and the "Unable to determine" answers say the file was too large to read.

The restricted questions need a count or ranking over the ticket files the restricted user can still see (12,000 to
17,000 rows), so they fail for the same reason.

## One grading false positive

The single correct mixed answer, `mixed-017`, is the text "Unable to determine: the invoices file (~12,000 rows) was
too large to read…". The grader cleans a text answer to its digits as DataBench does, which turns it into 12000,
within 1% of the gold 11980.4. Read by hand, rag scored 0/30 on the mixed set. The graded score stays 1/30 because
the bars may not change once results exist, and a 1/30 bar for "both" is the stricter one. No `test` answer is
affected: the other correct text answers are category names.

## What the tables container must reach

On the same 180 questions, from this run:

- at least 25 points above rag, so at least 53.3% (96/180), with an exact McNemar p < 0.01;
- at least 70% absolute (126/180), which is the binding bar;
- no family below rag's count in the table above;
- "both" at least 1/30 on the mixed set;
- an average agent cost per question of at most 1.5 × $0.0931 = $0.140.
