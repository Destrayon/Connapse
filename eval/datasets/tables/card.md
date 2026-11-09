# tables

The tables container's test set, built for CON-298 (ticket 1 of [CON-E6](../../../docs/plans/con-e6-tables-container.md)).
Suite `tables-v1`. It was fixed before any table code existed, so the proof against the rag baseline can't be tuned
to it. The pass bars it serves are in [pass-bars.md](../../pass-bars.md#tables-container-con-e6), and the reasoning
is in [the research doc](../../../docs/research/tables-container-2026-11-02.md).

- **Tables (14 CSV files, built, not committed):**
  - four DataBench tables (Forbes, NBA, Bank, Supermarket) from `cardiffnlp/databench` at revision
    `e75d53add267d2f9cfa32efd65ad77f0807adfad`, MIT licence. Forbes drops its long `bio` and `about` text columns;
    dates become ISO 8601;
  - a fictional company's tables: employees (1,500), customers (5,000), products (1,200), orders (100,000),
    invoices (12,000), tickets (20,000, split by queue into `tickets-support`, `tickets-billing` and
    `tickets-security`), and inventory in two loads (`inventory.load1.csv`, then `inventory.load2.csv`, which
    replaces it).
- **Questions (240):** 60 tune and 180 test in `questions.jsonl`, 100 per family before the split across six families:
  lookup, filter and count, sum and average, group and top-k, join, and seen-as-of (asked against load 1 or load 2
  through the `{as_of}` placeholder). Every answer is the result of `gold_sql` run on SQLite over the CSVs. A question
  is rejected if its top-k ties at the cut, or if its restricted or seen-as-of answer is within the grader's tolerance
  of the other answer.
- **Restricted user:** some ticket questions are asked as `restricted`, a user who can't read
  `tickets-security.csv`. `answer` is the trimmed answer and `admin_answer` the full one. An answer equal to
  `admin_answer` is a leak.
- **Sealed reserves:** `reserve-1.jsonl.gz` and `reserve-2.jsonl.gz`, 180 questions each, drawn the same way. Nobody
  opens them until a re-test after a result-informed change is due, and each is used once.
- **Mixed (30):** `mixed.jsonl` asks questions that need a rule from one of six policy documents in `docs/` together
  with the tables, for example the discount approval threshold applied to orders.
- **Linked set (`linked/`):** ten fictional reports, five Word and five PDF, each with prose and one table, plus a CSV
  copy of each table in `linked/tables/`. `linked/questions.jsonl` has 20 table questions answered by gold SQL over
  the copies and 10 prose questions; `linked/reserve-linked.jsonl.gz` is a sealed reserve of 30. Every table passes
  the promotion gate: one header row, the same column count on every row, and types that parse on at least 95% of
  cells.

Answers are graded by `TableAnswerGrader` (tests/Connapse.Eval/Metrics), a port of DataBench's `default_compare` with
four documented changes. The `tables-questions` adapter that loads this set arrives with the tables proof ticket.

## Rebuilding

    python eval/tools/tables/build_tables.py [--databench-dir <dir>]   # CSVs into eval/.cache/datasets/tables/v1
    python eval/tools/tables/build_questions.py                        # questions, reserves, mixed, policy docs
    python eval/tools/tables/build_linked.py [--pdf]                   # linked set; --pdf re-renders the PDFs

The scripts need Python with pandas and pyarrow (DataBench parquet) and pypdf (PDF check). The DataBench files are
downloaded at the pinned revision and checked by SHA-256, or read from `--databench-dir`. Every output except the
PDFs rebuilds byte-identically. LibreOffice stamps each PDF with a time, so the PDFs are committed as built and pinned
in `eval/MANIFEST.json` like every other file.
