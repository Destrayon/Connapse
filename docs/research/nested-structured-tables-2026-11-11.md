# How should nested JSON, JSON Lines, XML and YAML load into tables an agent can query?
**Date:** 2026-11-11
**Status:** Reviewed
**Built on:** tables-container-2026-11-02.md (the CON-E6 table store this extends). The Connapse corpus was unreachable, so no other prior corpus material was checked.

## Executive summary
Connapse should split nested structured files into real, normalized parent and child tables. Ingestion tools already do this (dlt, legacy Airbyte, AWS Glue), and it is also the only shape the text-to-SQL literature suggests agents handle well. Nested objects flatten into the parent row as path-named columns. Each array becomes a child table that has a row id, a parent row id and the position within the array. Anything below a depth cap, or anything that cannot be tabulated, is stored as `jsonb` so it stays queryable. Agents score much worse when answers sit inside nested columns: 10% against 27% on Spider 2.0, and 27–37% on DevRev's nested benchmark until the nested fields were described. No study compares the two storage shapes directly, so the case for normalized tables is an inference. CON-335's own test set has to confirm it.

## Research brief
**Question:** What table shape, key scheme and fallback should Connapse use to load nested JSON, JSON Lines, XML and YAML into PostgreSQL tables that an LLM agent can filter, count and join? The shape must also let a later GraphQL layer (CON-336) sit on top.

**Sub-questions:**
1. How do existing ingestion tools flatten nested JSON: naming, child tables, keys, depth caps, type conflicts?
2. How should XML, YAML and JSON Lines map onto a JSON-like tree, safely?
3. Which PostgreSQL storage suits data that can't be flattened, and what do GraphQL generators need from the tables?
4. What does the evidence say about LLM accuracy on nested schemas compared with flat ones, and how should a pass bar be set?

**Out of scope:** building GraphQL (CON-336); semi-structured documents that are prose rather than records (they stay text); structured-query datatypes outside the table store.

**Success criteria:** a concrete loading design with sources, plus a defensible eval design.

## Findings by sub-question

### 1. Flattening nested JSON in ingestion tools
Ingestion tools take one of two approaches to nested JSON. dlt, legacy Airbyte and AWS Glue split it into child tables. Airbyte Destinations V2, Singer target-postgres, DuckDB, Snowflake, BigQuery, ClickHouse and Spark keep the nesting in a native or JSON column and unnest it at query time. Every tool keeps what it cannot flatten as JSON rather than dropping it.

**dlt** (dlthub.com docs, relational.py source):
- **Nested objects:** flattened into the parent row; the column name joins the key path with `__`.
- **Arrays of objects:** each becomes a child table named `parent__field`.
- **Arrays of scalars:** each becomes a child table with a single `value` column.
- **Keys:** every row gets `_dlt_id`. Child rows add `_dlt_parent_id` and `_dlt_list_idx`. Child ids are a deterministic hash of the parent id, path and position.
- **Depth cap:** `max_table_nesting`. Anything deeper is stored as JSON.
- **Type conflicts:** dlt first tries to coerce the value, then falls back to a variant column such as `col__v_text`.
- **Long names:** shortened with a deterministic hash.

**Legacy Airbyte normalization:**
- Child tables keyed by hash ids, with a foreign key back to the parent.
- Arrays of scalars collapsed into a comma-separated string.
- The raw JSON blob always kept.

**Airbyte V2:**
- No child tables at all.
- A value that fails to cast becomes NULL, and the change is logged in `_airbyte_meta` instead of failing the sync.

**Glue Relationalize:**
- Each array is pivoted into a table named `<root>_<field>`, with columns `id` (a foreign key to the root) and `index`.

**pandas `json_normalize`:**
- Joins the key path with a `.` separator and has a `max_level` cap.

**DuckDB `read_json`:**
- Objects become STRUCT columns and arrays become LIST columns.
- An object switches to a MAP when it has more than 200 keys, or when its keys are sparse across records.

**The consensus algorithm across these tools:**
1. Infer a schema across all records; a missing key is NULL.
2. Inline objects using a path separator.
3. Move each array into a child table that carries a row id, a parent id and the position.
4. Make the ids deterministic.
5. Give arrays of scalars a `value` column.
6. Cap the depth, with a JSON fallback below it.
7. Coerce conflicting types, or fall back without failing the load.

Confidence is high: every tool claim above comes from primary docs or source code. The Glue scalar-array layout is the exception; it rests on one example and memory.

### 2. Mapping XML, YAML and JSON Lines to a JSON-like tree
All three formats map onto the same JSON-like tree the flattener reads, given a few conventions per format.

**XML:**
- **Attributes** become `@name` keys. Newtonsoft and xmltodict both do this; Spark uses `_` instead.
- **Element text** is the value when the element has no attributes or children. Otherwise it goes under `#text`.
- **Comments and processing instructions** are dropped. CDATA counts as text.
- **Single versus repeated elements:** an element seen once and an element seen several times are ambiguous. The fix is to decide array-or-not per path across the whole document (xmltodict's `force_list`).
- **Row element:** no tool detects it automatically. Spark requires a `rowTag`, and pandas `read_xml` uses the root's children.
- **Safety** (OWASP XXE cheat sheet): prohibit DTDs, null the resolver, and cap entity expansion.

**YAML:**
- **Scalars** resolve by YAML 1.2 core-schema rules, so `NO` and `on` stay strings (yaml.org/spec/1.2.2).
- **A multi-document stream** becomes a list of records.
- **`<<` merge keys** expand, with the map's own keys winning.
- **Aliases** expand within a budget, and cycles are rejected.
- **YamlDotNet:** `YamlStream` reads multi-document files, and `MergingParser` handles `<<`. YamlDotNet is a YAML 1.1 library, so its default scalar typing must not be trusted; scalars have to be typed by Connapse's own rules.

**JSON Lines** (jsonlines.org): one value per `\n`-separated line, with `\r` stripped. Blank lines are tolerated. Bad lines are rejected with their line numbers instead of failing the file. DuckDB unions keys across records.

Confidence is high for JSON Lines and YAML, which rest on specs. For XML it is medium: the conventions are consistent across tools, but no standard defines them.

### 3. PostgreSQL storage for the leftovers, and what a GraphQL layer needs
Data that can't be flattened should be stored as `jsonb`, and the generated tables need stable key columns.

**PostgreSQL storage** (postgresql.org/docs/17):
- **`jsonb` over `json`:** `jsonb` is pre-parsed, indexable and faster to query. The trade-offs are that it drops key order and duplicate keys, normalizes numbers and rejects `\u0000`.
- **GIN indexes:** a `jsonb_path_ops` GIN index serves `@>` (containment) and JSON path queries.
- **PostgreSQL 16 compatibility:** `JSON_TABLE` and `JSON_VALUE` exist only in PostgreSQL 17, so portable SQL should use `jsonb_array_elements` and similar functions.
- **Size limits:** a table holds at most 1,600 columns, and identifiers are capped at 63 bytes. Connapse already stores tables under generated `c_<n>` column names and keeps headers in its catalog, so neither limit touches header names.

**GraphQL generators:**
- **pg_graphql:** won't expose a table without a primary key, derives relationships from foreign keys, and can't filter JSON columns (supabase.github.io/pg_graphql).
- **Hasura:** filters `jsonb` columns by containment only.
- **PostGraphile:** reads keys and relationships declared in comment tags on views.

Every field an agent or a GraphQL client filters on should therefore be a real column. Every generated table should have a single-column row key and a parent key.

Confidence is high; these are primary docs.

### 4. LLM accuracy on nested data, and how to set a pass bar
No published study loads the same nested data both normalized and as JSON and compares agent accuracy on the same questions. The indirect evidence points one way.
- **Spider 2.0** (arXiv 2411.07763): the agent scored 10.34% on databases with nested columns, against 27.38% without them. The authors blame schema linking, meaning the model picks the wrong tables or columns. Sampled cell values were needed in the prompt.
- **DevRev** (arXiv 2609.04641): three leading systems scored 27–37% on array-of-struct columns. Describing the nested fields, their allowed values, sample values and join hints was worth 15 points overall.
- **TEND** (arXiv 2502.11201), a MongoDB benchmark: describing only top-level fields cut accuracy from 41.8% to 6.4%.
- **Schema presentation** (arXiv 2510.14296): distinct sample values gave better schema recall than sample rows, 90.6% against 82.3%.
- **Gold-answer quality** (CIDR 2026, Jin et al.): public benchmarks have 50–60% error rates in their gold answers, so gold answers must be computed, not hand-written.

The eval advice that follows:
- 50–100 questions spread across single-table, parent-child join, multi-level and null or absent-field questions.
- Result-set grading.
- A pass bar fixed before the run.
- A paired McNemar test when two designs are compared.

Confidence is medium. The evidence is indirect, and DevRev is a single, recent, partly LLM-judged source.

## Conflicts and uncertainties
- **Real foreign key constraints or not.** The GraphQL research recommends real primary key and foreign key constraints, since generators read them. Connapse reloads and deletes tables one by one, and constraints between them would force an ordering on every reload and delete. CON-335 keeps explicit key columns and describes the join in each column's description. CON-336 can declare relationships through view comment directives, which pg_graphql and PostGraphile both support, and that needs no constraints. This is a judgement call, not something the sources settle.
- **Path separator.** dlt and Airbyte use `__`; pandas uses `.`. Connapse headers are display names, not SQL identifiers, and the CON-305 join compiler already rejects a dotted header that is ambiguous with a join alias. `.` therefore reads better and is safe.
- **Arrays of scalars.** dlt makes a `value` child table, Airbyte made a comma-joined string, and Singer stores JSON. Connapse follows dlt, because only a child table lets an agent filter and count the elements.
- **Unverified:** DuckDB's fallback when types conflict; the 256 MB `jsonb` limit that is often quoted; YamlDotNet's alias-expansion budget.

## Gaps — what we did not find
- No head-to-head study of normalized tables against `jsonb` for LLM agents.
- No standard XML-to-tree mapping. All of them are conventions.
- No tool that detects XML row elements automatically.

## Source quality assessment
Most of sub-questions 1–3 rests on primary sources: official docs, specs and source code. Sub-question 4 rests on peer-reviewed or preprint benchmarks; DevRev is a 2026 preprint whose questions were written with LLM help. The tertiary claims, the Glue scalar arrays and the DuckDB conflict fallback, carry no weight in the design.

## Sources
**Primary.** Tools:
- dlthub.com/docs (destination-tables, naming-convention, schema, source)
- github.com/dlt-hub/dlt `relational.py`
- airbyte basic-normalization v0.44.1
- docs.airbyte.com typing-deduping
- github.com/transferwise/pipelinewise-target-postgres
- docs.aws.amazon.com Glue Relationalize
- pandas `json_normalize`
- duckdb.org loading_json
- docs.snowflake.com FLATTEN
- cloud.google.com BigQuery nested-repeated
- spark.apache.org generator functions and sql-data-sources-xml
- sqlite-utils CLI
- clickhouse.com newjson

**Primary.** Format specs and conversion conventions:
- newtonsoft.com ConvertingJSONandXML
- github.com/martinblech/xmltodict
- wiki.open311.org JSON_and_XML_Conversion
- pandas `read_xml`
- docs.databricks.com XML
- OWASP XXE Prevention Cheat Sheet
- yaml.org/spec/1.2.2, yaml.org/type/bool.html, yaml.org/type/merge.html
- YamlDotNet wiki and releases
- jsonlines.org

**Primary.** PostgreSQL and GraphQL generators:
- postgresql.org/docs/17 (datatype-json, functions-json, storage-toast, limits, sql-syntax-lexical)
- spec.graphql.org Names
- supabase.github.io/pg_graphql
- hasura.io/docs/2.0
- postgraphile.org/postgraphile/5/smart-tags

**Papers:**
- Spider 2.0 (arXiv 2411.07763)
- DevRev NL2SQL (arXiv 2609.04641)
- TEND (arXiv 2502.11201)
- BIRD (arXiv 2305.03111)
- column descriptions (arXiv 2408.04691)
- schema presentation (arXiv 2510.14296)
- CHESS (arXiv 2405.16755)
- Jin et al., CIDR 2026 (vldb.org/cidrdb/papers/2026/p5-jin.pdf)

**Secondary:**
- Aalborg ClinicalQA thesis (2026)
- Snowflake Cortex Analyst engineering blog
