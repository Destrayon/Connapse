# How should Connapse add a tables container that agents query with tools instead of text search?
**Date:** 2026-11-02
**Status:** Reviewed
**Built on:** agent-memory-design-2026-10-27.md, document-shape-robustness-2026-09-27.md (the decision to keep spreadsheets
out of RAG), agent-file-search-plus-rag results in `eval/agent-search/results.md` (CON-142), `eval/pass-bars.md` (CON-E3).
The Connapse MCP corpus was not available in the session that wrote this.

## Executive summary
Connapse should store table data as real typed PostgreSQL tables, one schema per tables container. Agents should
reach them through a small set of structured tools: list, describe, read with filters, and aggregate. The agent
never writes SQL. Connapse compiles each request into parameterized SQL using only table and column names it has
checked.

This is the shape of Microsoft's Data API builder MCP, Cube, dbt's MetricFlow tools and Airtable's filter API. It is
also what the one documented SQL-server compromise argues for. The Anthropic reference Postgres MCP server let the
model write raw SQL, and a `COMMIT;` escaped its read-only wrapper. Nearly everything can be reused rather than
built:
- Sylvan readers for CSV and Excel files.
- DuckDB's published type-sniffing algorithm, for working out column types.
- Frictionless Table Schema, as the stored description of a table's columns.
- Npgsql binary COPY, for loading rows.
- Connapse's own `IDocumentAccess`, all-sources permission rule and `VectorColumnManager` pattern.
- The CON-142 agent-eval harness.

Agent memory already has the right history model (close the old row, insert the new one). It moves onto the table
layer logically, as a built-in table the same tools can query, not by copying data.

The main uncertainty is the size of the win. The strongest published evidence is the TAG paper, where chunk
retrieval scored 0% on table questions and text-to-SQL scored 17%. That set has only 80 questions, so Connapse must
measure its own gap with a pass bar set in advance.

## Research brief
**Question:** how should a self-hosted .NET RAG platform add a container type for structured tables that agents
query with tools, reusing existing projects, libraries and designs?

**Sub-questions:** (a) existing projects that let agents query structured data, and what each offers for reuse;
(b) safety: no arbitrary SQL, per-user and per-container permissions, resource limits; (c) ingestion of CSV,
spreadsheets and tables inside documents, with schema inference; (d) moving agent memory onto the same layer while
keeping its history model; (e) how to prove the tables container beats text search.

**Out of scope:** writing to tables from agents (except memory), BI dashboards, charts, server-side LLM calls,
connector-specific sync details.

**Success criteria:** a recommended design per sub-question, with sources and named reuse, that the build plan
(`docs/plans/con-e6-tables-container.md`) can turn into tickets.

## (a) Existing tools that let agents query structured data
Products that enforce per-user permissions mostly avoid raw SQL. The market uses three query styles, and Connapse
should copy the structured one.

| Tool | How the agent queries | Safety model | Status | What Connapse reuses |
|---|---|---|---|---|
| [Reference Postgres MCP server](https://github.com/modelcontextprotocol/servers-archived/tree/main/src/postgres) | one `query(sql)` tool; schemas as resources | a READ ONLY transaction wrapper, escaped with `COMMIT;` plus a second statement ([Datadog Security Labs](https://securitylabs.datadoghq.com/articles/mcp-vulnerability-case-study-SQL-injection-in-the-postgresql-mcp-server/)) | MIT, archived 2025 | the anti-pattern, and its fixes |
| SQLite MCP server (same archive) | raw SQL tools | same wrapper approach | archived | nothing |
| [DBHub](https://github.com/bytebase/dbhub) | `execute_sql`, `search_objects`, custom parameterized SQL in TOML | read-only mode, row limit, timeout | MIT, active | a small tool surface (two tools, about 1.4k tokens, vendor claim); schema search instead of dumping every schema |
| [Google MCP Toolbox for Databases](https://github.com/googleapis/genai-toolbox) | admin-written tools in YAML: description, typed parameters, a SQL statement with `$1` placeholders | per-tool auth; "authenticated parameters" filled from the caller's token, never from the model | Apache-2.0, active | admin-curated parameterized queries; identity-bound parameters |
| [Data API builder SQL MCP](https://learn.microsoft.com/en-us/azure/data-api-builder/mcp/data-manipulation-language-tools) | `describe_entities`; `read_records` (select, filter, orderby, first, cursor); `aggregate_records` (function, field, groupby, having) | server builds deterministic SQL from structured parameters; per-role entity and field include/exclude; row policies | MIT, .NET | the closest match: copy its tool shapes and per-role field projection |
| [MotherDuck/DuckDB MCP](https://github.com/motherduckdb/mcp-server-motherduck) | raw SQL `execute_query` | read-only default, 1,024-row and 50k-character output caps | MIT | output caps as defaults |
| [Supabase MCP](https://simonwillison.net/2025/Jul/06/supabase-mcp-lethal-trifecta/) | `execute_sql` and management tools | an injected support ticket made an agent read a private table and write it back, through a key that bypassed row-level security ([General Analysis](https://generalanalysis.com/blog/supabase-mcp-blog)) | Apache-2.0 | keep tools read-only so there is no write-back path; never bypass permissions |
| [LangChain SQL agent](https://docs.langchain.com/oss/python/langchain/sql-agent) | list tables, schema with three sample rows, LLM query checker, run query | none built in; its docs call the example insecure | MIT | the order list → describe with samples → query, with errors returned so the agent retries |
| [LlamaIndex SQL retrievers](https://developers.llamaindex.ai/python/examples/index_structs/struct_indices/sqlindexdemo/) | finds the right table by embedding schema descriptions, adds similar rows | left to the operator | MIT | embed table and column descriptions in pgvector to find tables when there are many |
| [Vanna](https://github.com/vanna-ai/vanna) | model writes SQL, helped by retrieved question/SQL pairs | enforcement left to the database | MIT | nothing required; example pairs are optional |
| [Dataherald](https://github.com/Dataherald/dataherald) | NL-to-SQL with "golden SQL" examples | separate enterprise auth service | Apache-2.0; company listed out of business (secondary) | don't depend on it |
| [Cube](https://cube.dev/articles/analytics-mcp-server) | JSON query of measures, dimensions, filters, time dimension | the semantic layer does joins and row security, not the model | core Apache-2.0 | the JSON query shape |
| [dbt MCP / MetricFlow](https://docs.getdbt.com/docs/dbt-ai/mcp-available-tools) | `list_metrics`, `get_dimensions`, `query_metrics` | service-token scopes | Apache-2.0 | discover-then-query; showing the agent the SQL its request compiled to |
| [Snowflake Cortex Analyst](https://docs.snowflake.com/en/user-guide/views-semantic/verified-query-repository) | semantic model plus verified question/SQL pairs | platform roles | proprietary | verified queries double as an eval set; "90%+" accuracy has no public method (vendor claim) |
| [Databricks Genie](https://docs.databricks.com/aws/genie/best-practices) | instructions plus "trusted" parameterized queries; answers using them are badged | Unity Catalog | proprietary | a "trusted" badge on answers from curated queries |
| [Airtable MCP](https://airtable.com/developers/agents/mcp/getting-started) | record tools; community servers filter with `filterByFormula` | OAuth scopes, the user's own permissions | proprietary | a filter expression as the query language; 100 records per page |
| Notion | data-source query with filter and sort JSON; a hosted read-only SQL tool is reported (secondary, conflicting) | workspace permissions | proprietary | weak evidence only |
| Glean | no public documentation of agent table querying found | — | — | none |

**Accuracy evidence is thin and mostly vendor-run.**
- data.world's benchmark got 16.7% with text-to-SQL and 54.2% with a knowledge graph on 43 questions
  ([arXiv 2311.07509](https://arxiv.org/pdf/2311.07509), vendor-authored).
- A 2026 preprint with 100 questions found that a 4 KB description of the data raised accuracy from about 46–50% to
  about 68%. The three frontier models it tested scored about the same (arXiv 2604.25149, preprint).

The consistent lesson is that good table and column descriptions matter more than which model runs the agent.

**Takeaway for Connapse:** copy Data API builder's three tools as the default (describe, read, aggregate). Add
Toolbox-style admin-defined parameterized queries later. Leave raw SQL out.

## (b) Safety: no arbitrary SQL, Connapse's permissions, resource limits
The safe design layers a structured query compiler over a database role that can only read. Every documented bypass
needs the attacker to control the structure of the SQL text, and the compiler removes that.

1. **Structured query compiler.** The agent sends JSON: a table, columns, filters from a fixed operator set,
   group-by, aggregates, order and limit. Connapse checks every name against the caller's permitted tables and
   columns, quotes the names itself and binds every value as a parameter. Statement chaining, dangerous functions,
   catalog reads and DDL have no grammar to express them. Per-user row trimming is added by the compiler as `WHERE`
   predicates, which keeps permissions in application code where Connapse already enforces them.
2. **A login role that can only read table data.** Table queries run on their own connection pool, under a role
   with `USAGE` on the table schemas and `SELECT` on their tables, and nothing on Connapse's own tables. Datadog's
   recommended mitigation is exactly this. Read-only transaction mode is not a boundary: `SET TRANSACTION READ
   WRITE`, `set_config` and `COMMIT;` all undo it. Grants are the boundary. A per-container role, switched with
   `SET LOCAL ROLE`, would add cross-container isolation if the compiler had a bug. It is optional, and whether
   Azure Flexible Server allows it at scale is unverified.
3. **Per-transaction limits.** Each query runs as one statement inside `BEGIN READ ONLY`, with `SET LOCAL` values
   for `statement_timeout`, `lock_timeout`, `idle_in_transaction_session_timeout`, `work_mem` and `search_path`. A
   non-superuser can set all of these. `temp_file_limit` is superuser-only, so it is set on the role with `ALTER
   ROLE`. Azure made it a modifiable server parameter in October 2024 (secondary source); whether `azure_pg_admin`
   may set it per role is unverified. The application also caps rows and bytes returned.
4. **A separate pool, reset on return.** Datadog showed `SET` changes leaking between sessions through a shared pool.
5. **Row-level security only as a backstop.** PostgreSQL row-level security has pitfalls
   ([PG17 §5.9](https://www.postgresql.org/docs/17/ddl-rowsecurity.html)):
   - Table owners and `BYPASSRLS` roles skip it.
   - Referential checks open covert channels.
   - Only superusers can declare the `LEAKPROOF` functions it needs, and Azure gives no superuser.

   Running a second permission model beside Connapse's own invites drift.
6. **Cell contents are untrusted text.** Rows go back to the agent as labelled data under a fixed notice, the way
   memory already does (spotlighting, [arXiv 2403.14720](https://arxiv.org/abs/2403.14720)). The tools are
   read-only, which removes the write-back step the Supabase attack needed.

**Known bypasses and what stops each:**

| Bypass | Stopped by |
|---|---|
| `COMMIT;` then arbitrary SQL (reference Postgres MCP server) | no SQL input (1), read-only grants (2), one statement per command (3) |
| `SET default_transaction_read_only = off`, `set_config` | not expressible (1); grants, not modes, are the boundary (2) |
| `RESET ROLE` out of a per-container role | not expressible (1); the pool's own role still reads only table data (2) |
| `pg_read_file`, `COPY … TO PROGRAM`, `lo_import` | superuser-only or separately granted ([PG17 §9.28](https://www.postgresql.org/docs/17/functions-admin.html)); never granted |
| `dblink`, `postgres_fdw` | not installed; on Azure only if allow-listed ([MS Learn](https://learn.microsoft.com/en-us/azure/postgresql/flexible-server/concepts-extensions)) |
| `pg_sleep`, huge sorts, cartesian joins | not expressible; timeouts, `work_mem`, `temp_file_limit`, row caps |

**Permissions reuse.** Container access uses the existing `knowledge:read` scope and container checks. A table built
from a document (a synced CSV, a spreadsheet in S3) is visible only when `IDocumentAccess.ReadableAsync` says the
caller can read that document, which is the check memory uses today. A table merged from several files keeps a
hidden source-document column, and the compiler limits rows to readable documents. When access cannot be checked,
the answer is empty, never everything (fail closed, as in `ScopePredicate` and `MemoryService`).

**Option A, validated raw SQL, if ever wanted.** Raw SQL could be parsed with libpg_query through a .NET binding:
- [`pgsqlparser`](https://www.nuget.org/packages/pgsqlparser) (libpg_query 18.1, .NET 8–10)
- [`Npgquery`](https://www.nuget.org/packages/Npgquery/) (MIT)

Neither is an official pganalyze binding, and allow-listing functions is hard (overloads, `set_config`,
user-defined functions). Keep it out of v1.

**DuckDB as the engine is not safer.** Its own docs say to treat untrusted SQL like untrusted shell code
([Securing DuckDB](https://duckdb.org/docs/2.0/operations_manual/securing_duckdb/overview.html)). It has no roles or
grants, and run in-process it shares the web app's memory.

## (c) Ingestion: CSV, spreadsheets, tables inside documents
Connapse should reuse the Sylvan readers, DuckDB's sniffing algorithm and Frictionless Table Schema, and load real
typed tables with binary COPY.

- **CSV/TSV: [Sylvan.Data.Csv](https://github.com/MarkPflug/Sylvan)** (MIT). It detects the delimiter from the header
  row. It is a `DbDataReader`, so it feeds Npgsql COPY and Sylvan's `SchemaAnalyzer` directly. [Sep](https://github.com/nietras/Sep)
  (MIT) is faster, at about 21 GB/s against 15 GB/s on its own benchmark, but parsing will not be the bottleneck
  next to the database load. CsvHelper is 10–20x slower.
- **Excel: [Sylvan.Data.Excel](https://github.com/MarkPflug/Sylvan.Data.Excel)** (MIT). It reads xlsx, xlsb and xls,
  streaming. On its author's benchmark it read 65k rows in 162 ms with 0.67 MB allocated, against 1,072 ms and
  728 MB for ClosedXML ([benchmarks](https://github.com/MarkPflug/Benchmarks/blob/main/docs/ExcelReaderBenchmarks.md)).
  Other options:
  - ExcelDataReader (MIT) is the fallback, and the only reader that documents merged-cell ranges.
  - EPPlus is excluded, because its license has been noncommercial since v5.
  - ODS has no mature .NET reader, so it is deferred.
  - Every reader returns a formula's last saved value. Files saved without cached values come back blank and are
    flagged.
- **Type inference: DuckDB's sniffer** ([docs](https://duckdb.org/docs/2.0/data/csv/auto_detection.html)).
  1. Pick the dialect whose rows all have the same column count.
  2. Sample about 20k rows from several offsets.
  3. Give each column an ordered candidate list (null, boolean, time, date, timestamp, integer, decimal, text) and
     drop a candidate on its first failing value.
  4. Treat row 1 as a header if its types differ from the rows below.

  Sylvan's `SchemaAnalyzer` already does most of this in .NET. Connapse adds day-first and month-first dates and
  decimal and thousands separators, keeping a variant only if it parses every sampled value. Rows that fail the
  inferred type at load go to a reject list and are never silently set to null. Polars infers from only 100 rows and
  pandas per chunk, which is where their failures come from.
- **Schema format: [Frictionless Table Schema v2](https://datapackage.org/standard/table-schema/).** It covers field
  types, `missingValues`, `decimalChar`, `groupChar`, true and false values, date formats, primary and foreign keys,
  and constraints. No maintained .NET implementation exists, so Connapse writes a small serializer.
- **Messy spreadsheets: one header row in v1.**
  - The best published detectors, [TableSense](https://arxiv.org/pdf/2106.13500) (86.5% precision, 91.3% recall)
    and [SpreadsheetLLM](https://arxiv.org/abs/2407.09025) (78.9% F1), released no usable code.
  - [Auto-Tables](https://arxiv.org/pdf/2307.14565) found over 30% of real tables are not in relational form.

  v1 uses cheap rules instead: skip blank or one-cell leading rows, and take the first mostly-text row above typed
  rows as the header. A blank row or column ends a table. A sheet with several blocks, merged header cells or numeric
  headers is flagged "needs review".
- **Storage: typed tables, not JSONB.** Each table becomes one PostgreSQL table with generated names (`t_<id>`,
  columns `c_<n>`) in a schema per container. The original headers live in a catalog.
  - Precedent: Baserow creates a real table per user table ([docs](https://baserow.io/docs/technical%2Fdatabase-plugin)).
  - JSONB keeps no per-key statistics, so the planner misestimates; Heap reported queries about 2000x slower than on
    typed columns ([Heap](https://heap.io/blog/when-to-avoid-jsonb-in-a-postgresql-schema), secondary).
  - The runtime DDL follows the pattern of Connapse's `VectorColumnManager`.
  - A changed file loads into a new table that is swapped in, rather than altering the live one.
- **Loading: Npgsql binary COPY** (`BeginBinaryImport`). One benchmark, not Npgsql-specific, measured COPY at
  3.6x faster than any other method ([Tiger Data](https://www.tigerdata.com/blog/benchmarking-postgresql-batch-ingest),
  secondary).
- **Tables inside PDFs and Office files.** Connapse already extracts them to Markdown (Tabula, `PdfTables.cs`, #642).
  Structure scores such as TableFormer's 96.8 TEDS-S on FinTabNet ([arXiv 2203.01017](https://arxiv.org/pdf/2203.01017))
  don't measure cell content, and Camelot scored 73.0 in the same paper.
  - Pitfalls when making such tables queryable:
    - Multi-row headers have to be flattened into one.
    - Section rows that span the table have to become a column of their own.
    - A table split across pages has to be joined back together.
    - Numbers carry footnote marks, parentheses for negatives, currency signs and "—" for empty.
  - A document table should become queryable only when it has one header row and a consistent column count, and
    types parse on at least 95% of cells. Otherwise it stays RAG text only. Each promoted row keeps the page and
    table it came from.
- **Linking a promoted table back to its passage** (added 2026-11-02 at the user's request). Three established tools
  keep a link between an extracted table and the text around it:
  - [LlamaIndex's recursive retriever](https://developers.llamaindex.ai/python/examples/query_engine/pdf_tables/recursive_retriever/)
    stores an `IndexNode` summary of each table in the same vector index as the text. Each node has an `index_id`
    such as `pandas0`, and when search returns that node the `RecursiveRetriever` looks the id up and queries the
    table's own engine ("Retrieved node with id, entering: pandas0").
  - [Azure Document Intelligence layout](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/prebuilt/layout)
    returns each table with `spans` (offset and length in the document's `content`) and `boundingRegions` (page and
    polygon). Sections and figures list their parts in `elements` as JSON pointers such as `/paragraphs/0`, which
    ties each table to its place in the text and in the section tree.
  - [Unstructured's document elements](https://docs.unstructured.io/open-source/concepts/document-elements) keep a
    table as its own `Table` element with `text_as_html`, `page_number`, `element_id` and `parent_id`. Composite
    chunks are formed from non-table elements only, and each chunk lists its sources in `orig_elements`.

  All three return the link with the text, so the reader can reach the table in one step. Connapse already has the
  page and table number from `PdfTables.cs`, so it can store the link in its catalog and show it on search hits.
  None of the three checks whether the reader may see the table; Connapse must.

## (d) Moving agent memory onto the table layer
Memory's history model is the one the table layer needs for changing data. Memory should move onto the layer
logically, keeping its table and rules, rather than having its data copied.

- `memory_facts` already keeps two time axes per row: when a fact was true (`valid_from`, `valid_to`), and when
  memory held it (`recorded_at`, `superseded_at`). A correction closes the old row and inserts a new one in one
  locked transaction, and forgetting deletes every version (`PostgresMemoryStore`,
  agent-memory-design-2026-10-27.md).
- A synced table needs the same model. When a CSV is re-synced, a changed row is one whose value changed. Closing
  the old row and inserting the new one gives every synced table "as of a date" and "what changed" queries.
  Re-syncs record when Connapse saw each change, not when it happened at the source, so those queries answer "as
  of when Connapse saw it". That history model becomes the table layer's standard, available to any table.
- Memory then becomes a built-in table in the catalog: a fixed schema with subject, attribute, value, the two time
  axes, author and sources. Its rows are physically `memory_facts`.
  - The table tools can read and aggregate memory.
  - The memory tools keep their names and answers, and run through the same compiler.
  - The all-cited-sources-readable rule becomes a compiler predicate.
  - The memory notice becomes the table layer's untrusted-data notice.
- Copying memory into a dynamic table is not proposed. It would risk the history guarantees, which took the
  locking work in `CorrectAsync` and `ForgetAsync` to get right, and it gains nothing an agent can see.
  `memory-v1` must show no regression after the move.

## (e) Proving the tables container beats text search
The proof scores agents' final answers, because retrieval metrics don't apply to a tool that returns rows. The
literature gives no reason to set a small bar.

- **Evidence that chunk retrieval fails on table questions:**
  - [TAG](https://arxiv.org/html/2408.14717v1) (80 modified BIRD questions): RAG 0%, RAG plus LM reranking 2%,
    Text2SQL 17%, a hand-written query-plus-LM pipeline 55%.
  - [TableRAG](https://arxiv.org/html/2410.04739v3) (GPT-3.5): pasting whole tables scored 4.6% and 9.1% on two
    sets; schema-plus-tool methods scored 43–49%.

  No controlled study compares chunk RAG with SQL on spreadsheets specifically.
- **Benchmarks whose licenses allow copying subsets into the repo:**
  - [DataBench](https://huggingface.co/datasets/cardiffnlp/databench) (MIT): 80 real tables, 1,822 questions with
    typed answers.
  - [Spider 2.0-lite](https://github.com/xlang-ai/Spider2) (MIT).
  - [TableBench](https://huggingface.co/datasets/Multilingual-Multimodal-NLP/TableBench) (Apache-2.0), though it is
    scored by ROUGE.

  BIRD is CC BY-SA, which would carry its terms into the repo. Scores reported on BIRD and Spider carry known
  annotation errors (a CIDR 2026 study, secondary).
- **Scoring.** Use DataBench's grader ([databench_eval](https://github.com/jorses/databench_eval)):
  - Numbers match within a tolerance.
  - Lists are compared as sets.
  - Categories and booleans match exactly after normalizing.

  The agent answers in a fixed JSON shape, so no LLM judge is needed.
- **Setup, following CON-142** (`eval/agent-search/`): the same agent and prompt, varying only the tools. Answer
  keys come from executing gold SQL.
- **Sample size.** With a McNemar paired test at α = 0.05 and 80% power, a 10-point gain needs about 150 paired
  questions and a 30-point gain about 35. So 180 test questions supports an overall verdict and a rough per-family
  reading.
- **Bar size.** TAG and TableRAG show 30–55-point gaps, so a +10 bar would let a weak tool path pass. +25 still sits
  inside the published effects.

## Conflicts and uncertainties
- Datadog's article gives two different dates for the reference Postgres server's deprecation.
- Notion's hosted SQL tool and its pricing tier are reported only by conflicting secondary sources.
- Accuracy claims for Cortex Analyst, Genie and dbt are vendor-run on small or unpublished sets.
- Whether Azure Flexible Server allows `ALTER ROLE … SET temp_file_limit` and many per-container roles is unverified.
  Run `SELECT name, context FROM pg_settings` on Azure PG17 before relying on either.
- Sylvan's merged-cell support is undocumented. The Sep and Sylvan speed numbers are each library author's own.
- TAG's RAG-0% result comes from 80 questions, and it left its aggregation questions unscored.
- WikiTableQuestions' license is given as CC BY-SA in one source and CC BY in another.

## Gaps — what we did not find
- How Glean exposes tables to agents.
- A controlled chunk-RAG versus SQL comparison on spreadsheets.
- A maintained, official .NET binding of libpg_query, or a .NET Table Schema library.
- Released code for any spreadsheet table detector.
- Independent accuracy numbers for structured-DSL tools against raw text-to-SQL on the same questions.

## Source quality assessment
- **Primary sources:** the safety design rests on PostgreSQL docs, Datadog's write-up, General Analysis's write-up
  and the DuckDB docs. Library choices rest on the projects' own docs and source. The evaluation evidence rests on
  two peer-reviewed or arXiv papers (TAG, TableRAG) and the DataBench and Spider licenses.
- **Vendor-run:** library speed numbers, and product accuracy claims.
- **Secondary:** the Supabase vendor response, and Azure's `temp_file_limit` parameter.

## Sources
- **Primary:**
  - [Datadog Security Labs](https://securitylabs.datadoghq.com/articles/mcp-vulnerability-case-study-SQL-injection-in-the-postgresql-mcp-server/)
  - [General Analysis](https://generalanalysis.com/blog/supabase-mcp-blog)
  - [PG17 row security](https://www.postgresql.org/docs/17/ddl-rowsecurity.html)
  - [PG17 admin functions](https://www.postgresql.org/docs/17/functions-admin.html)
  - [Azure extensions](https://learn.microsoft.com/en-us/azure/postgresql/flexible-server/concepts-extensions)
  - [Securing DuckDB](https://duckdb.org/docs/2.0/operations_manual/securing_duckdb/overview.html)
  - [DuckDB CSV auto-detection](https://duckdb.org/docs/2.0/data/csv/auto_detection.html)
  - [Data API builder MCP](https://learn.microsoft.com/en-us/azure/data-api-builder/mcp/data-manipulation-language-tools)
  - [MCP Toolbox](https://github.com/googleapis/genai-toolbox)
  - [DBHub](https://github.com/bytebase/dbhub)
  - [MotherDuck MCP](https://github.com/motherduckdb/mcp-server-motherduck)
  - [LlamaIndex recursive retriever over PDF tables](https://developers.llamaindex.ai/python/examples/query_engine/pdf_tables/recursive_retriever/)
  - [Azure Document Intelligence layout](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/prebuilt/layout)
  - [Unstructured document elements](https://docs.unstructured.io/open-source/concepts/document-elements)
  - [LangChain SQL agent](https://docs.langchain.com/oss/python/langchain/sql-agent)
  - [LlamaIndex SQL demo](https://developers.llamaindex.ai/python/examples/index_structs/struct_indices/sqlindexdemo/)
  - [dbt MCP tools](https://docs.getdbt.com/docs/dbt-ai/mcp-available-tools)
  - [Cortex verified queries](https://docs.snowflake.com/en/user-guide/views-semantic/verified-query-repository)
  - [Genie best practices](https://docs.databricks.com/aws/genie/best-practices)
  - [Airtable MCP](https://airtable.com/developers/agents/mcp/getting-started)
  - [Sylvan](https://github.com/MarkPflug/Sylvan)
  - [Sylvan.Data.Excel](https://github.com/MarkPflug/Sylvan.Data.Excel)
  - [Sep](https://github.com/nietras/Sep)
  - [Excel reader benchmarks](https://github.com/MarkPflug/Benchmarks/blob/main/docs/ExcelReaderBenchmarks.md)
  - [EPPlus license change](https://github.com/EPPlusSoftware/EPPlus/wiki/License-change)
  - [Frictionless Table Schema](https://datapackage.org/standard/table-schema/)
  - [Baserow database plugin](https://baserow.io/docs/technical%2Fdatabase-plugin)
  - [TableSense](https://arxiv.org/pdf/2106.13500)
  - [SpreadsheetLLM](https://arxiv.org/abs/2407.09025)
  - [Auto-Tables](https://arxiv.org/pdf/2307.14565)
  - [TableFormer](https://arxiv.org/pdf/2203.01017)
  - [PubTables-1M](https://arxiv.org/abs/2110.00061)
  - [TAG](https://arxiv.org/html/2408.14717v1)
  - [TableRAG](https://arxiv.org/html/2410.04739v3)
  - [DataBench](https://huggingface.co/datasets/cardiffnlp/databench)
  - [databench_eval](https://github.com/jorses/databench_eval)
  - [Spider2](https://github.com/xlang-ai/Spider2)
  - [BIRD mini-dev](https://github.com/bird-bench/mini_dev)
  - [TableBench](https://huggingface.co/datasets/Multilingual-Multimodal-NLP/TableBench)
  - [Spotlighting, arXiv 2403.14720](https://arxiv.org/abs/2403.14720)
  - [pgsqlparser](https://www.nuget.org/packages/pgsqlparser)
  - [Npgquery](https://www.nuget.org/packages/Npgquery/)
- **Vendor or preprint:**
  - [data.world benchmark, arXiv 2311.07509](https://arxiv.org/pdf/2311.07509)
  - arXiv 2604.25149 (the semantic-description study)
  - [Cube analytics MCP](https://cube.dev/articles/analytics-mcp-server)
- **Secondary:**
  - [Simon Willison on Supabase MCP](https://simonwillison.net/2025/Jul/06/supabase-mcp-lethal-trifecta/)
  - [Heap on JSONB](https://heap.io/blog/when-to-avoid-jsonb-in-a-postgresql-schema)
  - [Tiger Data COPY benchmark](https://www.tigerdata.com/blog/benchmarking-postgresql-batch-ingest)
  - [postgresqlco.nf temp_file_limit](https://postgresqlco.nf/doc/en/param/temp_file_limit/)
  - [dbatools CSV benchmark](https://dbatools.io/new-csv-library/)
  - [Docling discussion 2744](https://github.com/docling-project/docling/discussions/2744)
