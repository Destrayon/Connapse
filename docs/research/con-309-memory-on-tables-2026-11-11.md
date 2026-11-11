# CON-309: can memory facts be read through the table layer with a view, safely and without copying?
**Date:** 2026-11-11
**Status:** Reviewed
**Built on:** no prior corpus material (the Connapse MCP containers were not reachable from this session)

## Executive summary
Memory facts can be exposed to the restricted table-reader login through one view, `tables_memory.facts`, owned by
Connapse's main login and marked `security_barrier`, that joins `memory_facts` to the per-request token in
`tables_auth.requests` and applies memory's permission rules itself. PostgreSQL checks a non-invoker view's base
tables against the view owner but evaluates `current_setting()` in the caller's session, so the token decides what
the caller sees. A view guard is stronger here than a row-level security policy, because the 2024–2026 row-security
CVEs all involve role changes plus plan reuse, which is the reader's exact pattern. The main uncertainty is whether
every hosted server (Azure Flexible Server) runs the August 2026 PostgreSQL minors.

## Research brief
**Question:** How should memory facts become a built-in table of the table layer (CON-309) without copying data,
keeping memory's per-source and per-author permission rules and its time rules?

**Sub-questions:** (1) view security on PostgreSQL 16–18; (2) whether memory's own tools should also move onto the
view through a second EF Core context; (3) how the memory gate is run CPU-only.

**Out of scope:** writes to memory through the table layer; raw SQL over memory.

## Findings: view security on PostgreSQL 16–18
A view that is not `security_invoker` reads its tables with the view owner's privileges, while functions in it run
as if called by the querying session, so `current_setting('tables_auth.token')` and `statement_timestamp()` read the
caller's transaction-local values (postgresql.org, rules-privileges and CREATE VIEW notes; primary). No change in 16,
17 or 18 was found.

With `security_barrier`, a caller's condition is pushed into the view only when it passes no view column to a
non-leakproof function and contains no subquery (PG16 `allpaths.c`, `qual_is_pushdown_safe`; primary source code).
In PG16's catalog `texteq`, `uuid_eq`, `timestamptz_gt` and `starts_with` are leakproof; `texticlike` (ILIKE),
`textlike`, regex match, `ts_match_vq` and `array_eq` are not. Leaky conditions therefore run only on rows the view
already allowed, at the cost of not using an index for them.

Unreferenced output columns of the view, including a scalar sub-select, are replaced by NULL constants and not
evaluated, unless the outer query makes a whole-row reference (PG16 `remove_unused_subquery_outputs`; single source).

Nested non-invoker views each check their tables against their own owner; an inner view must itself be
`security_barrier` or leaky caller functions could see its rows early (inferred from the general rule).

## Findings: view guard against row-level security
Relevant CVEs (postgresql.org security page; primary):
- CVE-2026-14666, row-security caching ignores role changes on plan reuse; fixed 18.6, 17.11, 16.15 (2026-08-13).
- CVE-2026-14668, selectivity-estimator ctid type confusion, needs an object creator; same fixes.
- CVE-2025-8713, optimizer statistics leak sampled view data through a crafted leaky operator; fixed 17.6, 16.10.
- CVE-2024-10976 and CVE-2024-10978, row security and SET ROLE under plan reuse; fixed 17.1, 16.5.

No CVE specific to `security_barrier` views was found. Most remaining attacks need the attacker to create a function,
operator or statistics object, which the reader role cannot: it has no CREATE on any schema, and the table tools only
run compiled SQL. The token is an ordinary settable setting, so its 128-bit secrecy and expiry are the guard.

## Findings: memory's own tools on a second EF Core context
EF Core supports mapping the same CLR entity to views in a second context (`ToView`, keys kept, out of migrations)
over an external open connection and transaction, with no statements a read-only transaction would reject
(learn.microsoft.com EF Core docs, npgsql.org; primary). It is feasible but **not adopted**: it would move memory
recall, which the gate holds to p95 + 150 ms, onto a token round-trip and a second login, and touch 21 tests that
build the store directly. Memory's own tools keep their current store; only the table tools read the view.

## Findings: the memory gate
`eval/tools/run_light.sh memory-v1` runs `Connapse.Eval memory --config hybrid --split heldout` with CPU-only Ollama
on port 11435 and containers capped at 2 CPUs; no LLM is called. The baseline
`eval/baselines/memory-v1-heldout-2026-10-27.json` holds aggregates only (recall p95 6.2 ms); the Holm step in
`MemoryEval.cs` compares notes against memory within one run. No code compares a run against the baseline with Holm.

## Conflicts and uncertainties
- The epic plan places a memory table in documents containers too; the code keeps memory in memory containers only.
- Unused-column pruning and nested-view behaviour rest on PG16 source reading, not docs.

## Gaps — what we did not find
- Whether Azure Flexible Server has rolled out 16.15/17.11/18.6.
- A baseline-vs-run Holm comparison tool for memory-v1.

## Source quality assessment
Almost entirely primary: PostgreSQL docs, security page and source, EF Core and Npgsql docs, and the repo itself.

## Sources
- Primary: postgresql.org docs (rules-privileges, sql-createview), postgresql.org/support/security, PostgreSQL
  REL_16_STABLE `allpaths.c` and `pg_proc.dat`, learn.microsoft.com EF Core (entity types, keyless types,
  transactions, performance), npgsql.org (EF provider API, full-text search, datetime), this repo's `eval/` and
  `tests/Connapse.Eval/`.
