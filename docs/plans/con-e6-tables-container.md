# CON-E6: the tables container (v0.5.0)

**Status:** proposed, for approval before any code (CON-220).
**Milestone:** v0.5.0.
**Research:** [`docs/research/tables-container-2026-11-02.md`](../research/tables-container-2026-11-02.md) has the
sources for every choice below.

## What we are building
Today, Connapse has documents containers. A spreadsheet, CSV or JSON file in one is either cut into text passages or
skipped: XLSX, CSV and JSON were kept out of RAG on 2026-09-27 because text search can't answer questions like "how
many orders shipped late in March". Published results agree. On the TAG benchmark, passage retrieval answered 0% of
table questions and querying the data answered 17–55%.

CON-E6 adds a second container type: a **tables container**. Each file loaded into it becomes a real typed database
table. Agents query these tables through four tools: list, describe, read and aggregate. Every answer is still trimmed
to what the caller is allowed to see. Agent memory then moves onto the same layer, so one set of tools and one
history model serve both.

**The rule that shapes everything else: the agent never writes SQL.** It sends a structured request, such as "table
orders, columns region and total, where shipped_late is true, group by region, sum total". Connapse checks every
table and column name against what the caller may read. It then writes the SQL itself, with every value passed as a
parameter. Microsoft's Data API builder, Cube, dbt's metric tools and Airtable all work this way. The reference
Postgres MCP server let the model write raw SQL, and a `COMMIT;` broke out of its read-only guard.

## What we reuse
| Source | What we take | What we don't take |
|---|---|---|
| [Microsoft Data API builder MCP](https://learn.microsoft.com/en-us/azure/data-api-builder/mcp/data-manipulation-language-tools) (MIT, .NET) | The tool shapes. `describe_entities` becomes `table_describe`. `read_records` (select, filter, order, first, cursor) becomes `table_read`. `aggregate_records` (function, field, group by, having) becomes `table_aggregate`. We also take its per-role column hiding. | Its OData filter strings: a JSON filter is easier to validate. Its write tools. |
| [Google MCP Toolbox for Databases](https://github.com/googleapis/genai-toolbox) (Apache-2.0) | Admin-written saved queries with typed parameters, and parameters filled from the caller's login, never from the model. Saved queries come in a later ticket. | Raw SQL tools. |
| [DBHub](https://github.com/bytebase/dbhub) (MIT) | A small tool list, and a schema search instead of listing every table at once. | `execute_sql`. |
| [LangChain SQL agent](https://docs.langchain.com/oss/python/langchain/sql-agent) (MIT) | The working order: list tables, describe one with 3 sample rows, then query. Errors go back to the agent so it can retry. | Its trust in the model's SQL. |
| [LlamaIndex SQL retrievers](https://developers.llamaindex.ai/python/examples/index_structs/struct_indices/sqlindexdemo/) (MIT) | Embedding table and column descriptions in pgvector, so the right table is found when there are many. | — |
| [MotherDuck MCP](https://github.com/motherduckdb/mcp-server-motherduck) (MIT) | Output caps as defaults: 1,024 rows and about 50k characters per answer. | — |
| [Databricks Genie](https://docs.databricks.com/aws/genie/best-practices) / [Snowflake Cortex Analyst](https://docs.snowflake.com/en/user-guide/views-semantic/verified-query-repository) | A "trusted" badge on answers that came from an admin's saved query. Verified question-and-answer pairs that double as test questions. | — |
| [Sylvan.Data.Csv](https://github.com/MarkPflug/Sylvan) and [Sylvan.Data.Excel](https://github.com/MarkPflug/Sylvan.Data.Excel) (MIT) | Fast, streaming CSV, xlsx, xlsb and xls readers. They plug straight into Npgsql's bulk load. ExcelDataReader (MIT) is the fallback for merged cells. | EPPlus: its license has been noncommercial since v5. |
| [DuckDB's CSV sniffer](https://duckdb.org/docs/2.0/data/csv/auto_detection.html) | The column-type algorithm: sample about 20k rows, give each column an ordered list of candidate types, and drop a candidate at its first failing value. | DuckDB itself as the query engine: its docs say untrusted SQL is like untrusted shell code. |
| [Frictionless Table Schema v2](https://datapackage.org/standard/table-schema/) | The stored description of each table's columns: types, missing-value markers, decimal and thousands characters, keys. | — |
| [Baserow](https://baserow.io/docs/technical%2Fdatabase-plugin) | One real PostgreSQL table per user table, with generated names and the original headers in a catalog. | JSONB storage: it is slow to filter and the planner misestimates it. |
| [DataBench](https://huggingface.co/datasets/cardiffnlp/databench) (MIT) and its [grader](https://github.com/jorses/databench_eval) | Real tables and typed answers for the test set. The grading rules: numbers within a tolerance, lists as sets, categories by exact match. | — |
| **Connapse itself** | `IDocumentAccess` and the fail-closed rule. `VectorColumnManager`'s runtime-DDL pattern. Memory's history model, all-sources rule and untrusted-data notice. Tabula PDF tables (`PdfTables.cs`). The CON-142 agent eval and the CON-E3 pass-bar rules. | — |

## Design
### Container type
`Container` gains a `Kind` of `documents` (today's containers, the default) or `tables`. A container's kind is set
when it is created and never changes. A documents container keeps working exactly as now, including memory. A tables
container holds tables, not passages: its files are not embedded or chunked, and `search_knowledge` doesn't search
it.

### Storage
- **One schema per tables container.** Each table in it is a real PostgreSQL table with generated names (`t_<id>`,
  columns `c_<n>`), so a file's headers never reach SQL text.
- **A catalog in Connapse's own schema.** For each table it records the original name, description, source document,
  row count, a Frictionless schema per column, a "needs review" flag, and the reject list (rows that failed their
  column's type, kept rather than silently set to empty).
- **Loading.** Files load with Npgsql binary COPY. A changed file loads into a new table that is then swapped in, so
  readers never see half a load.
- **Runtime DDL.** It follows `VectorColumnManager` and stays the only code allowed to create or drop table-layer
  objects.
- **Deleting.** Removing a container drops its schema.

### Ingestion
- **CSV and TSV** use Sylvan.Data.Csv.
- **xlsx, xlsb and xls** use Sylvan.Data.Excel, with each sheet becoming a table. ODS is deferred.
- **Types** follow DuckDB's sniffer, extended with day-first and month-first dates and comma decimals. A format is kept
  only if every sampled value parses.
- **Headers.** v1 needs one header row. Blank and one-cell rows above the header are skipped. A sheet with several
  blocks, merged headers or number-only headers is loaded but flagged "needs review" in the UI and in
  `table_describe`.
- **Formulas.** Formulas give their last saved value. A file saved without those values is flagged, not loaded as
  blanks.
- **JSON.** A JSON array of flat objects loads as a table. Nested JSON is out of v1, because it is the "structured
  data" datatype question noted in memory and is not part of this epic.
- **Tables inside PDF and Office files** stay in their documents container as text. They are promoted to a queryable
  table only in a later ticket, and only when they pass a quality gate: one header row, the same column count on
  every row, and types parsing on at least 95% of cells. Each promoted row keeps its page and table number.

### Tools (MCP and REST)
| Tool | Does | Notes |
|---|---|---|
| `table_list` | Lists tables the caller can see, filtered by a search phrase. | The search uses the embedded descriptions once there are many tables. |
| `table_describe` | Shows the columns, types, description, row count, 3 sample rows and the "needs review" flags. | The row count and sample rows go through the same permission and time filters as a read. The catalog's table-wide count is never shown, because it would reveal how many rows the caller can't see. |
| `table_read` | Reads rows: columns, a filter, an order, a limit and a cursor. | The filter is JSON: `and`, `or`, `not`, `=`, `!=`, `<`, `<=`, `>`, `>=`, `in`, `between`, `is_null`, `contains`, `starts_with`. |
| `table_aggregate` | Runs `count`, `count_distinct`, `sum`, `avg`, `min` or `max`, with group by, having, order and limit. | |

- **Joins.** A request may join one other table in the same container on columns named in the request, and only
  where both columns have the same type. Cross-container joins are not offered.
- **Time.** Reads of a table with history take an optional `seen_as_of` time (see Memory and history). The memory
  table keeps its own time inputs instead.
- **Answers** are JSON rows under a fixed notice saying the cells are data, not instructions, as memory's answers
  already are.
- **Errors** say what to fix ("column 'regoin' not found; columns are region, total, …"), so the agent can retry.
- **Showing the SQL.** The SQL a request compiled to is shown on request, as dbt does, but only to admins.
- **Scopes.** All four tools need `knowledge:read`. Loading files needs `knowledge:write`, as uploads do today.

### Safety
Every layer below would stop the known attacks on its own if the others failed. The research doc lists each bypass
and which layer stops it.

1. **The compiler.** It accepts only the JSON request shape.
   - Table and column names must be ones in the catalog that the caller may read, and the compiler writes them itself
     as quoted generated names.
   - Every value is a bound parameter.
   - Nothing in the request can express a second statement, a function call, a catalog table, or a write.
2. **A separate login role and pool for table queries.** The role has `USAGE` on the table schemas and `SELECT` on
   their tables, and nothing on Connapse's own tables except the one memory view described under Memory and
   history. The pool resets each connection when it is returned. Grants are
   the boundary, not read-only mode: `COMMIT;` and `set_config` can undo read-only mode, but not a missing grant.
3. **Per-query limits.** Each query runs as one statement in a `BEGIN READ ONLY` transaction, with `SET LOCAL` values
   for `statement_timeout` (5 s), `lock_timeout`, `idle_in_transaction_session_timeout`, `work_mem` and
   `search_path`.
   - `temp_file_limit` is set on the role, because only a superuser can set it per query.
   - Connapse also caps answers at 1,024 rows and about 50k characters.
4. **No row-level security as the main control.** PostgreSQL row-level security is skipped by table owners, needs
   `LEAKPROOF` functions that only superusers can declare (Azure gives no superuser), and would be a second
   permission model drifting from Connapse's. Permissions live in the compiler instead.
5. **Read-only tools.** No table tool writes, so a prompt planted in a cell has no write-back path, which is the step
   the Supabase attack needed. Memory's writes stay in memory's own tools.

**Raw SQL is not offered.** If it is ever wanted, the route is an admin-only option that parses SQL with a libpg_query
binding (`pgsqlparser` or `Npgquery`), after its own research ticket. It is outside v0.5.0.

### Permissions
- **The container.** Access to a tables container uses today's container access checks.
- **Tables built from a document** (a CSV uploaded to a container, or synced from S3 or Azure later) carry that
  document's id. The table is visible only when `IDocumentAccess.ReadableAsync` says the caller can read the
  document, the same check memory uses.
- **Tables merged from several files** keep a hidden source-document column. The compiler adds a `WHERE` clause
  that limits rows to readable documents.
- **When access can't be checked**, the answer is empty with an "access could not be checked" error. Nothing falls
  back to showing everything.
- **Hidden columns.** Admins can hide columns per table. A hidden column cannot be selected, filtered on, grouped by,
  or used in a join; otherwise a filter would reveal its values one question at a time.

### Memory and history
Memory's history model becomes the table layer's standard. Memory's data is not copied.

- **History for synced tables.** Today a re-loaded file simply replaces its table. With history on, a re-sync
  compares rows by the table's key. Each changed row gets a close-and-insert: the old version is marked "seen until"
  and a new version is added, which is how `memory_facts` handles corrections.
  - That gives every table a `seen_as_of` read ("as Connapse saw it on 1 March") and a "what changed" read.
  - Re-syncs record when Connapse saw each change, not when it happened at the source, and the tool descriptions say
    so.
  - A table without a key column replaces rows and has no history, which is stated in `table_describe`.
- **Memory becomes a built-in table.** Every container's memory appears in the catalog as a fixed table, `memory`,
  with subject, attribute, value, valid from and to, recorded and superseded, author and sources. Its rows are
  physically today's `memory_facts`.
  - `table_read` and `table_aggregate` can query it, for example "count open facts per subject".
  - **The two kinds of time stay separate.** Memory's `as_of` means *when the fact was true* (valid time). A synced
    table's `seen_as_of` means *when Connapse saw the row*. The memory table keeps today's inputs and their exact
    predicates in `PostgresMemoryStore.QueryAsync`:
    - `as_of` returns rows not yet superseded whose valid range contains the date.
    - `history` returns every version.
    - `changed_from` and `changed_to` filter on when facts were recorded or superseded.

    Mixing the two would change existing answers. For example, a fact recorded in March but true since January must
    still be returned for February.
  - **How the query role reads it.** The query role reads memory through one view, `tables_memory.facts`. The view
    joins `memory_facts` to `memory_fact_sources`, and the role is granted `SELECT` on that view and nothing else of
    Connapse's. The compiler adds the container and the all-sources predicate, as it does for any table. Tests run
    memory reads with the real restricted login, not the app's login.
  - The memory tools keep their names, inputs and answers. Their reads go through the same compiler, and the
    existing memory time and permission integration tests must pass unchanged through it.
  - The rule that a fact shows only when the caller can read **all** its sources becomes a compiler predicate, and
    the author-only rule for facts with no sources stays.
  - Writes stay in `PostgresMemoryStore`. The locking in `CorrectAsync` and `ForgetAsync` took work to get right, and
    no agent-visible benefit justifies moving it.
- **The gate.** memory-v1 must show no regression, and the memory leak tests must pass, after the move.

## The proof
The question is whether an agent answers table questions better with the table tools than with text search. The
pass bar below is fixed now, before any data or results exist, and may not change once results exist. Ticket 1 copies
it into `eval/pass-bars.md`.

### Test set: `tables-v1`
- **Tables.** 8–12 tables from 1k to 100k rows, all as CSV files:
  - DataBench tables (MIT), for real-world mess.
  - Generated company-style tables (orders, tickets, invoices, staff), so answers can't come from the model's
    training data.
  - At least two pairs of tables that join on a key.
  - One table loaded twice with changes, for seen-as-of questions.

  CSV is the only format both setups can read today. TextParser reads CSV as text, and no parser reads XLSX. Comparing
  against a spreadsheet the text setup can't open would inflate the gain. Spreadsheet loading (ticket 11) is tested on
  its own fixtures, not in this comparison.
- **Questions.** 240 questions, split 60 for tuning and 180 held out for the test, in six families of about 30 test
  questions each: look up one value, filter and count, sum or average, group and top-k, join two tables, and value as
  Connapse saw it on a date.
- **Reserve sets.** Two more held-out sets of 180 questions are generated at the same time, from the same tables,
  families and generator, and sealed. They are used only for a re-test (see the outcome rules).
- **Answer keys.** Each question's answer comes from running a gold SQL query, never from a person's arithmetic. The
  SQL is kept with the question.
- **Mixed set.** 30 more questions whose answer needs prose and a table together, such as a policy in a document and
  a count in a table.
- **Permissions.** Some questions are asked as a user who can't see one of the source documents. Their correct answer
  is the trimmed one.

### How it runs
This follows CON-142 (`eval/agent-search/`).
- **The agent.** Claude Sonnet 5.5 through `claude -p`, with the same base prompt for every setup, at most 25 turns,
  one attempt per question.
- **The setups:**
  - **rag:** the same CSV files in a documents container, parsed as text by today's TextParser and searched with
    `search_knowledge` and `get_document` on default hybrid search. This is the text-search baseline.
  - **tables:** the four table tools over a tables container.
  - **both:** all six tools, used on the mixed set.
- **Answers** come back as typed JSON and are graded by the DataBench rules:
  - Numbers match within 1% relative (or exactly, for integers).
  - Lists match as sets.
  - Categories and booleans match exactly after normalizing case and spaces.

  No judge model is needed.
- **Load on the PC.** The agent runs in the cloud. Ollama runs CPU-only with containers capped, as the eval rules
  require.

### Pass bar for the tables container
The tables container ships only if all of these hold on the 180 held-out questions:
1. **Accuracy:** tables answers at least **25 points** more questions correctly than rag, with an exact McNemar test
   p < 0.01.
2. **Absolute accuracy:** tables answers at least **70%** correctly.
3. **Every family:** no family scores lower with tables than with rag.
4. **The mixed set:** "both" answers no fewer of the 30 mixed questions than rag does.
5. **No harm to text search:** v1, enterprise-v1, pdfqa-v1 and links-v1 show no Holm-significant drop and nDCG@10
   falls by at most 0.01 on each, because table containers are kept out of search.
6. **Leaks:** the CON-31 leak suite and the new table leak tests (ticket 7) pass with zero failures.
7. **Cost and speed:** 0 server AI tokens. `table_aggregate` p95 under 500 ms on a 100k-row table. Average agent
   cost per question at most 1.5x rag's.

### Gate for moving memory
Memory moves (ticket 12) only if:
- memory-v1 shows no Holm-significant drop in Recall@5 against `memory-v1-heldout-2026-10-27`;
- the memory p95 latency stays within the existing memory row of `eval/pass-bars.md`;
- the memory permission and time integration tests pass unchanged with the real restricted login.

This gate is measured in ticket 12's own PR. v0.5.0 needs both the tables pass bar and this gate.

**What each outcome means:**
- **All pass:** ship in v0.5.0.
- **Any result-informed change.** A change made after seeing held-out results is re-tested on the next sealed
  reserve set, with every bar re-run, and never by changing a bar. This covers a redesign after bar 1 or 2 fails and
  a fix for one family after bar 3 fails. There are two reserve sets, so at most two re-tests. If bar 1 or 2 still
  fails after them, CON-E6 stops at the eval and the build tickets after the proof are dropped.
- **Bar 5 or 6 fails, or the memory gate fails:** that work is blocked until it is fixed. A fix that touches table
  tools also counts as a result-informed change.

The size of the bar comes from the literature. TAG and TableRAG show gaps of 30–55 points, so a +10 bar would let a
weak tool path pass. A 25-point gain on 180 paired questions is well above the McNemar power floor, which is about
35 pairs for a 30-point gain.

## Build tickets, in order (one PR each)
1. **Write the pass bar and build the test set.** Add the tables pass bar and the memory gate to
   `eval/pass-bars.md`. Add the `tables-v1` dataset: CSV tables, questions, gold SQL, answer keys, the two sealed
   reserve sets and a card. Port the DataBench grader to C# with its fixtures. No product code.
2. **Run the text-search baseline.** Add the agent runner for the rag setup, reusing CON-142's harness. Run rag on
   tables-v1 and commit the report.
3. **Add the tables container type.** Add `Kind` to containers, a migration, a choice on the create page, the API
   field, and keeping tables containers out of search.
4. **Add the table catalog and storage.** Add a schema per container, the catalog tables, a DDL manager modelled on
   `VectorColumnManager`, the swap on reload, and dropping the schema on container delete.
5. **Load CSV files into tables.** Use Sylvan.Data.Csv, DuckDB-style type inference, a Frictionless schema, the
   reject list, and binary COPY. Uploads to a tables container go here instead of to the text parser.
6. **Build the safe query compiler and runner.** Compile the JSON request into parameterized SQL. Add the read-only
   login role and pool, the per-query limits and the output caps. Add tests for every bypass in the research doc,
   each of which must fail to run.
7. **Trim tables by permission.** Add document-level visibility, the hidden source column for merged tables,
   fail-closed handling, and hidden columns. Apply the same filters to `table_describe` counts and sample rows. Add
   planted-leak tests in the CON-31 style, including one where only rows the caller can't see change and that
   caller's describe output must stay the same.
8. **Give agents the table tools.** Add `table_list`, `table_describe`, `table_read` and `table_aggregate` over MCP
   and REST, with the untrusted-data notice, retryable errors, and joins on named columns.
9. **Keep history for synced tables.** Re-sync by key with close-and-insert, and add `seen_as_of` reads and a "what
   changed" read.
10. **Run the proof.** Run tables and both on tables-v1 and the mixed set, and the regression suites and leak tests.
    Write the results report against the pass bar. If a bar fails, the outcome rules above decide what happens next.
11. **Load spreadsheets into tables.** Use Sylvan.Data.Excel, with ExcelDataReader for merged cells. Add header
    detection, "needs review" flags, flags for formulas with no saved value, and flat JSON arrays.
12. **Move memory onto the table layer.** Add the `tables_memory.facts` view and its grant, list memory in the
    catalog, send memory reads through the compiler with memory's own time predicates, and make the all-sources rule
    a compiler predicate. Run the memory gate.
13. **Find tables by description.** Embed table and column descriptions, and search them in `table_list` when a
    container has many tables.
14. **Add admin-saved queries.** Add Toolbox-style saved queries with typed parameters, a "trusted" badge on their
    answers, and parameters filled from the caller's login.
15. **Promote document tables.** PDF and Office tables that pass the quality gate become tables, keeping their page
    and table number.

Tickets 1 and 2 come first so the bar and baseline exist before any table code. Tickets 3–9 build everything the
proof's six families need. Nothing ships until ticket 10 passes. Tickets 11–15 depend on it, and 13–15 can move to
after v0.5.0 if time runs short.

## Open questions for approval
- **Azure.** Can Azure Flexible Server set `temp_file_limit` on a role, and does it support the separate login role?
  Ticket 6 checks this on Azure PG17 first. If not, the limit falls back to the server parameter.
- **Per-container database roles.** These would add isolation if the compiler had a bug. They are left out for now,
  because their cost on Azure at many containers is unknown.
