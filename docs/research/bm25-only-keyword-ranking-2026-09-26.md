# Should Connapse drop ts_rank and make pg_textsearch BM25 its only keyword ranker?
**Date:** 2026-09-26
**Status:** Reviewed
**Built on:** keyword-query-matching-2026-09-26.md; PR #547 eval results (BM25 vs ts_rank on the 7-dataset v1 subset). Connapse knowledge-base tools were not available in this session.

## Executive summary
BM25 should become Connapse's default keyword ranker. Containers without a BM25 index should be treated as stale and indexed automatically; that "keyword-only reindex" is just a `CREATE INDEX CONCURRENTLY` over chunk text that is already stored, with no re-chunking or re-embedding. But ts_rank should not be deleted yet, for three reasons:
- **Hosted databases.** pg_textsearch is unavailable on AWS RDS, Aurora, Azure Flexible Server, Supabase, Neon and DigitalOcean, and Connapse accepts any Postgres connection string.
- **Crash risk.** pg_textsearch 1.4.0 has open bugs that can crash or wedge the entire Postgres server.
- **Untested at scale.** Connapse's one-partial-index-per-container design has not been tested at hundreds or thousands of containers.

The fallback does not need today's full cost, though. The stored `simple`+`english` tsvector and its GIN index can shrink, because BM25 no longer needs either one.

## Research brief
**Question:** Is there any remaining reason to keep ts_rank once BM25 exists, or can unindexed containers simply be treated as stale and reindexed for keyword search only?
**Sub-questions:**
1. What in Connapse still depends on ts_rank or the stored tsvector?
2. How risky is pg_textsearch as a sole, hard dependency?
3. Where can pg_textsearch run outside the bundled image?
4. How do phrases, exclusions and stop-word-only queries work without the tsvector column, and what does that column cost?

**Out of scope:** ranking quality, which PR #547 already measured.
**Success criteria:** a keep, drop or slim decision, with the conditions for revisiting it.

## Findings by sub-question

### 1. What in Connapse still depends on ts_rank or the stored tsvector
Only `KeywordSearchService` uses ts_rank or `chunks.search_vector`; nothing else in `src/` reads them (codebase audit, primary). Every search surface passes exactly one container: the REST search endpoints, the MCP `search_knowledge` tool and the UI. So the per-container BM25 index always applies, and the "search not scoped to one container" fallback is currently unreachable. No cross-container search issue is filed.

The deployment docs make `ConnectionStrings__DefaultConnection` a required setting and show a hardened connection string (`docs/deployment.md`). An external or hosted Postgres is therefore possible, but no guide covers it.

The existing `ReindexService` re-chunks and re-embeds documents. A BM25 "keyword-only reindex" does not need it, because `chunks.content` is already stored; building the index is pure DDL. In PR #547 an owner with up to 20k chunks is built on its first search and larger owners build in the background. That is the "stale container" flow the question proposes, done lazily.

### 2. pg_textsearch as a sole dependency
pg_textsearch is durable but young, and its failures take down the whole database server (medium risk overall). All sources below are primary: releases, issues and ARCHITECTURE.md at https://github.com/timescale/pg_textsearch.

**Durability**
- The index is WAL-logged through Postgres's standard `GenericXLog`, so crash recovery and physical replicas are covered and tested.
- Logical replication subscribers build their own index (PR #263).
- Hot standbys need `hot_standby_feedback = on`.

**Maturity:** releases come roughly monthly (1.0 in March 2026 through 1.4.0 in August 2026), and every one fixed corruption or replication bugs. Version 1.2 notes that physical replication was "essentially broken" before it.

**Open bugs against 1.4.0/main**
- #512: writers spin forever and can't be cancelled, even with `pg_terminate_backend`.
- #506: a rolled-back `DROP INDEX` crashes other backends with SIGSEGV, sending the cluster into crash recovery.
- #508: databases created from a template share index state and crash.
- #470: each REINDEX leaks shared-memory accounting.

Because pgvector data and every Connapse table live in that same server, a keyword-search bug is a full outage, not a degraded search.

**Upgrades**
- `ALTER EXTENSION UPDATE` is tested in CI from every release.
- The 1.3 format change once silently dropped documents still in the memtable (PR #452, now a warning with a REINDEX hint).
- pg_upgrade across major versions has no documentation or tests; dump/restore or REINDEX afterwards is the safe path.

**Scale:** the project has no guidance or tests for hundreds or thousands of partial bm25 indexes; the largest tested is 200 partition indexes. The Postgres docs warn that every insert checks every partial index predicate, and advise against large sets of non-overlapping partial indexes (https://www.postgresql.org/docs/current/indexes-partial.html).

### 3. Where pg_textsearch runs
As of 2026-09-26, pg_textsearch is available on:
- Tiger Cloud (GA)
- Crunchy Bridge
- Google Cloud SQL and AlloyDB (public preview since 2026-09-21; AlloyDB ships 1.3)
- Azure HorizonDB (preview; a different product from Flexible Server)

It is absent from AWS RDS, Aurora, Azure Flexible Server ("not on roadmap", Microsoft Q&A, Feb 2026), Supabase, Neon and DigitalOcean. pgvector, which Connapse already requires, is available everywhere (vendor extension lists, primary).

No portable BM25 exists for the other hosts:
- ParadeDB pg_search and VectorChord-bm25 are on none of these services.
- Neon's `lakebase_text` is proprietary.
- AWS's own sample pairs Aurora with OpenSearch.
- A pure-SQL BM25 over `ts_stat` or a term-statistics table has no published latency numbers, and ParadeDB (secondary) calls it "very convoluted and very slow".

### 4. Phrases, exclusions and stop words without the stored tsvector
Once BM25 ranks, the stored tsvector and GIN index are not needed for matching. The remaining checks run on the ~100–500 BM25 candidates, where computing `to_tsvector` costs about 60 µs per 1,000-character chunk. This was verified on pg_textsearch 1.4.0 by the researcher.

- **Phrases:** the bm25 index stores no positions (issue #314 is open), so "database system" and "system of a database" score the same. Use `to_tsvector(content) @@ phraseto_tsquery(...)` on candidates as a boost, which matches Elasticsearch's optional-phrase signal ([Elastic guide](https://www.elastic.co/guide/en/elasticsearch/guide/current/proximity-relevance.html)).
- **Exclusions:** `NOT (to_tsvector('english', content) @@ q)` stays a single ordered index scan with a row filter. Its cost grows with how many top-ranked rows contain the excluded term, up to pg_textsearch's 100,000-row scan cap.
- **Stop-word-only queries:** Elasticsearch's default is to return nothing (`zero_terms_query: none`). If Connapse wants "the who" to work, a second bm25 index with `text_config = 'simple'` does it. That doubles the partial-index count, which conflicts with the scaling risk in sub-question 2.
- **Cost of the stored column:** on 34k chunks of code-heavy text from this repo, the `simple`+`english` stored tsvector and GIN index took the table from 38 MB to 86 MB (+16 MB GIN) and bulk insert from 0.17 s to 6.2 s. This was measured by the researcher; prose may differ.
  - Since PR #546, ranking already ignores the `simple` copy (weights `{0,0,1,0}`). That copy only serves the stop-word fallback.

## Recommendation
1. **Make BM25 the default** once the image is published, and make indexing proactive rather than first-search. Build at startup for owners that lack an index, and at container or source creation, when the index is empty and instant. An unindexed container is "stale" and gets a keyword-only background build. Only the build window uses the fallback.
2. **Keep ts_rank as the fallback, as a kill switch.** It covers hosts without the extension, BM25 errors at runtime (fall back automatically instead of failing the search), and admins who turn BM25 off while a pg_textsearch bug is open.
3. **Slim the fallback.** Replace the stored `simple`+`english` column with an `english`-only representation, or an expression GIN index, and drop the `simple` stop-word path in favour of Elasticsearch's "return nothing" default. Measure write and storage savings before committing to it.
4. **Scale-test before defaulting.** Run 1,000 containers with per-owner partial indexes and measure insert latency and planning time. If it degrades, consider list-partitioning `chunks` by owner, since pg_textsearch keeps statistics per partition, before adding more indexes.
5. **Revisit dropping ts_rank** when both hold:
   - (a) Connapse declares a pg_textsearch-capable Postgres a requirement, or the major hosts ship it.
   - (b) pg_textsearch's open server-crash bugs (#506, #512) are fixed and a release has soaked.

## Conflicts and uncertainties
- **Drop the tsvector entirely vs keep a fallback.** The feature-mechanics research says nothing needs the tsvector once BM25 ranks everything. The ops and hosting research says a fallback is still needed for hosts without the extension and as a kill switch. Both are right about different things. The resolution is a slim fallback, not the full stored column.
- **Hosted availability.** Cloud SQL and AlloyDB docs list pg_textsearch without a preview label, but the 2026-09-21 press release says preview.
- **Unverified mechanics.** `REINDEX CONCURRENTLY` support is referenced but has no dedicated test. The pg_dump/restore behaviour is inferred from standard Postgres, not documented by the project.
- **Single-measurement numbers.** The storage and insert-cost figures are one measurement on code-heavy text.

## Gaps — what we did not find
- Any test or guidance for thousands of partial bm25 indexes.
- pg_upgrade (PG17 → PG18) behaviour with the extension.
- Published latency for pure-SQL BM25.
- How many Connapse users run an external Postgres; no telemetry exists.

## Source quality assessment
Most claims are primary: pg_textsearch releases, issues, README and ARCHITECTURE.md; vendor extension lists; Postgres and Elastic docs; and the Connapse codebase. Researcher-run container checks back the phrase, exclusion, stop-word and storage measurements. Secondary sources are the ParadeDB view on pure-SQL BM25 and a blog's ts_rank scaling figure, which was not opened.

## Sources
**Primary:** https://github.com/timescale/pg_textsearch (README, ARCHITECTURE.md, releases, issues #314 #470 #506 #508 #512, PRs #263 #452 #480) · https://www.postgresql.org/docs/current/indexes-partial.html · https://www.postgresql.org/docs/current/textsearch-tables.html · https://www.postgresql.org/docs/current/gin.html · https://www.elastic.co/docs/reference/query-languages/query-dsl/query-dsl-match-query · https://www.elastic.co/guide/en/elasticsearch/guide/current/proximity-relevance.html · https://docs.aws.amazon.com/AmazonRDS/latest/PostgreSQLReleaseNotes/postgresql-extensions.html · https://docs.aws.amazon.com/AmazonRDS/latest/AuroraPostgreSQLReleaseNotes/AuroraPostgreSQL.Extensions.html · https://learn.microsoft.com/en-us/azure/postgresql/extensions/concepts-extensions-versions · https://learn.microsoft.com/en-us/answers/questions/5760513/pg-textsearch-extension-support-on-azure-database · https://learn.microsoft.com/en-us/azure/horizondb/ai/full-text-search · https://docs.cloud.google.com/sql/docs/postgres/extensions · https://docs.cloud.google.com/alloydb/docs/reference/extensions · https://docs.crunchybridge.com/extensions-and-languages · https://neon.com/docs/extensions/pg-extensions · https://docs.digitalocean.com/products/databases/postgresql/details/supported-extensions/ · https://www.tigerdata.com/docs/deploy/tiger-cloud/tiger-cloud-aws/tiger-cloud-extensions/pg-textsearch
**Secondary:** https://www.paradedb.com/learn/search-in-postgresql/bm25 · https://github.com/supabase/postgres · https://www.globenewswire.com/news-release/2026/09/21/3365524/0/en/google-cloud-brings-native-bm25-full-text-search-to-alloydb-and-cloud-sql-via-tiger-data-s-pg_textsearch.html
