# How should Connapse store agent memory: facts over time, with sources and permissions?
**Date:** 2026-10-27
**Status:** Reviewed
**Built on:** no prior corpus material (Connapse MCP was not available in the session that wrote this).

## Executive summary
Connapse's agent memory should be one append-only PostgreSQL table with two time axes per fact (when it was true,
and when memory believed it), corrections that close the old row instead of changing it, and a true delete that
leaves only a content-free audit entry. Among the surveyed products only Zep/Graphiti keeps both axes, and every one
of them except the official MCP memory server calls an LLM when it writes; none hides a fact by the permissions of
its sources. The permission rule should be stricter than "any readable source": a fact citing several sources is
shown only when the caller can read all of them, because an "any" rule lets a writer widen a private fact's audience
by citing one public document. The main uncertainty is benchmark evidence: published LongMemEval numbers for these
products are vendor-reported and disputed.

## Research brief
**Question:** how should a self-hosted RAG platform design agent memory of facts over time, with sources, history,
deletes and permission trimming, and with no server-side LLM calls?

**Sub-questions:** (1) how prior memory systems model changing facts; (2) a bitemporal schema in PostgreSQL;
(3) how to return memory safely (prompt injection) and which permission rule leaks.

**Out of scope:** embedding models, ranking research, UI.

**Success criteria:** a schema, tool shapes, a permission rule and pitfalls, each with sources.

## Findings: how prior memory systems model changing facts
Prior agent-memory products mostly overwrite or append facts, and only Zep/Graphiti keeps a full history on two time
axes.
- **Zep/Graphiti** stores four times on each fact edge: when it became true, when it stopped, when the system learned
  it and when the system retired it. On a contradiction an LLM sets the old edge's end to the new edge's start; the
  old edge is kept. Every fact traces back to the input "episode" it came from. Primary: Rasmussen et al. 2025,
  arXiv 2501.13956; github.com/getzep/graphiti.
- **Mem0**'s paper (arXiv 2504.19413) has an LLM choose ADD, UPDATE, DELETE or NOOP against the ten nearest memories,
  which overwrites. Its current docs describe an "ADD-only" extraction that never overwrites
  (docs.mem0.ai/core-concepts/memory-evaluation). The docs and the update API page disagree; unresolved.
- **Letta/MemGPT** keeps editable prompt blocks plus vector-searched archival passages; no dates, no provenance
  (docs.letta.com). **LangMem** updates a profile document in place and reconciles with an LLM
  (langchain-ai.github.io/langmem). **A-MEM** (arXiv 2502.12110) rewrites neighbouring notes with an LLM, replacing
  the originals. **Cognee** models dated events, not superseded facts (docs.cognee.ai/guides/time-awareness).
- **The official MCP memory server** (github.com/modelcontextprotocol/servers, `src/memory`) is the only one with no
  server LLM: entities, relations and observation strings in a JSONL file, hard deletes, no dates, no provenance.
- **Benchmarks.** On LongMemEval_s, Zep with gpt-4o reports knowledge-update 83.3 against 78.2 for full context, and
  temporal reasoning 62.4 against 45.1; with gpt-4o-mini it loses knowledge-update (74.4 against 76.9) (arXiv
  2501.13956). Mem0 reports 93.6 and 97.0 on the same two kinds at a 200-memory budget without naming the answering
  model (vendor, single source). LoCoMo numbers between Mem0 and Zep are disputed by Zep
  (blog.getzep.com, "Lies, damn lies, statistics"). These are answer accuracies with an LLM reader, so they do not
  compare with Connapse's retrieval-only scoring.

**Takeaway for Connapse:** take Graphiti's edge model (two axes, invalidate instead of delete, source per fact), drop
the LLM: the agent decides what changed and says so with an explicit correct call.

## Findings: a bitemporal schema in PostgreSQL
A single table with both time axes and close-and-insert corrections covers current, as-of and "as we believed then"
queries without a history table.
- Valid time (SQL:2011 "application time") is when a fact held; transaction time ("system time") is when the
  database held it (Kulkarni & Michels, SIGMOD Record 2012; Snodgrass, *Developing Time-Oriented Database
  Applications in SQL*).
- PostgreSQL 18 added `WITHOUT OVERLAPS` keys for one period only; `FOR PORTION OF` is not in 18
  (postgresql.org/docs/18/sql-createtable.html). Connapse runs PostgreSQL 17 (`pgvector/pgvector:pg17`), so neither
  is available, and a two-range exclusion constraint would need `btree_gist`.
- **Correction pattern:** close the old row (its believed-until time is set), insert the new row, in one transaction;
  never update content in place.
- **Open ends** should be one representation everywhere (null or infinity), and date-only input normalised to UTC
  midnight.
- **Concurrency:** lock the fact being corrected (`SELECT … FOR UPDATE`) so two corrections cannot both close it.
- **Erasure** is a real `DELETE` of every version; closing a row keeps its content, so it is not erasure.

**Decision for Connapse:** nullable `timestamptz` pairs instead of ranges (plain B-tree indexes, no extension, simple
EF mapping; the volume is small), no overlap constraint, because agents may record several current values for one
attribute (a team's members) and one subject's facts are few enough to read in one query.

## Findings: returning memory safely, and which permission rule leaks
Memory returned to an agent is attacker-reachable text, so it must come back as labelled data with its author and
sources, and the permission rule must not let a writer choose a wider audience.
- **Attacks:** MINJA plants memory records through ordinary queries (98.2% injection success, arXiv 2503.03704);
  AgentPoison triggers poisoned entries at under 0.1% poison rate (arXiv 2407.12784). OWASP's agentic Top 10 names
  memory and context poisoning (ASI06; seen only through secondary sources).
- **Defenses:** spotlighting marks untrusted text so the model can tell it from instructions; datamarking cut attack
  success from over 50% to under 2% on GPT models (Hines et al., arXiv 2403.14720). The MCP spec says servers must
  sanitise tool output and clients should validate it (modelcontextprotocol.io/specification/2025-06-18/server/tools).
- **Response shape:** JSON facts with id, subject, attribute, value, dates, author user, writing agent, and only the
  sources the caller can read, under a fixed notice that these are claims written by agents, not instructions. Length
  caps on every field at write time.
- **Permission rule.** Palantir Foundry propagates markings from every input to derived data (an "all inputs" rule;
  palantir.com/docs/foundry/security/access-control-propagation). No standard covers "any source" against "all
  sources" for derived facts; the comparison below is reasoning, not a cited result.
  - *Any readable source* leaks by **padding**: a fact citing private B and public A is shown to all of A's readers.
  - *All readable sources* never widens the audience; its cost is that a fact disappears for a caller missing one
    source.
  - Both need write-time checks: the writer must be able to read every cited source (no fabricated citations), and
    access is re-checked live on every read (revocation applies at once). Neither stops a writer from omitting the
    private document it actually read: with no server LLM, nothing can check that text is supported by its sources.
  - A fact citing nothing has no source to check; show it to its author only.

**Decision for Connapse:** "all cited sources readable", which also satisfies the ticket's "hidden unless the caller
can read one of its sources".

## Conflicts and uncertainties
- Mem0's docs contradict themselves on overwriting; its benchmark numbers are vendor-only and disputed by Zep.
- The OWASP agentic Top 10 item was seen only through secondary sources.
- The any/all permission comparison has no primary source.

## Gaps — what we did not find
- No surveyed product filters facts by the permissions of their sources.
- No independent LongMemEval per-kind numbers for Mem0, Letta or LangMem.
- Graphiti's, Letta's and LangMem's storage schemas were not read in code.

## Source quality assessment
Schema and spotlighting findings rest on primary sources (PostgreSQL docs, SQL:2011 paper, arXiv papers, the MCP
spec). Product comparisons rest on primary repos and docs, but the benchmark numbers are vendor-reported. The
permission rule rests on one primary analogue (Foundry) plus reasoning.

## Sources
- Primary: arXiv 2501.13956 (Zep); arXiv 2504.19413 (Mem0); arXiv 2410.10813 (LongMemEval); arXiv 2502.12110
  (A-MEM); arXiv 2503.03704 (MINJA); arXiv 2407.12784 (AgentPoison); arXiv 2403.14720 (spotlighting);
  github.com/getzep/graphiti; github.com/modelcontextprotocol/servers (`src/memory`);
  modelcontextprotocol.io/specification/2025-06-18/server/tools; postgresql.org/docs/18/sql-createtable.html;
  postgresql.org/docs/18/ddl-constraints.html; npgsql.org/efcore/mapping/range.html; SIGMOD Record 2012 SQL:2011
  overview; palantir.com/docs/foundry/security/access-control-propagation; docs.mem0.ai; docs.letta.com;
  langchain-ai.github.io/langmem; docs.cognee.ai.
- Secondary: blog.getzep.com (benchmark dispute); OWASP ASI06 summaries (zealynx.io).
