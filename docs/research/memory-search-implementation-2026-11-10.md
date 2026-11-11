# How to build CON-322's memory search changes
**Date:** 2026-11-10
**Status:** Reviewed
**Built on:** memory-search-shortfalls-2026-11-10.md, memory-findability-plan-2026-11-10.md

## Executive summary
The CON-322 changes to `memory_recall` can all be built in PostgreSQL with no new extension except pg_trgm, which Azure
Flexible Server allows. Rare words are ranked higher by working out, at query time, how many facts in the space contain
each query word. Once those counts are materialized, a 2,000-fact space ranks in about 9 ms. Partial words such as
"Postgres" go through pg_trgm, but only as a fallback for query words that matched nothing: at its default threshold the
trigram score cannot tell a prefix ("time" in "timezone") from a typo, and fusing it into the main rank brings back the
CON-321 false hits. Short handles should be permanent per-user numbers stored in Postgres and never reused, as
Playwright MCP's refs are. A handle the caller was not shown is refused with the one call that fixes it. Results should
be one plain-text line per fact, grouped by subject, with defaults left out. Anthropic's own example cut a response to
about a third that way. The main uncertainty is that no source measures sibling-fact caps, "did you mean" hints or handle
spellings on agents, so those choices are judgment calls that the fresh sealed set tests as a whole.

## Research brief
**Question:** How should Connapse implement the CON-322 memory search changes (handles, rare-word ranking, partial
words, compact results, a subject's other facts, empty-result hints, month names) in PostgreSQL behind the MCP tools?

**Sub-questions:** (1) short fact handles that survive restarts and many server instances; (2) rare-word ranking and
partial-word matching in SQL; (3) the compact result format, attaching a subject's other facts, empty-result hints and
dates.

**Out of scope:** embeddings (stage 2 of the CON-313 plan), BM25 extensions (not on Azure's allow-list), and the eval
design, which CON-321 settled.

**Success criteria:** a concrete design for each change, with its evidence and its weak spots named.

## Short fact handles
A short handle for a memory fact should be a permanent number per user, shown as `f<n>`, stored in PostgreSQL and never
reused. The strongest prior art is Playwright MCP (primary, source): it builds refs as `'e' + (++lastRef)` from a counter
that only goes up, keeps an element's ref while the element is unchanged, and answers an unknown ref with `Ref e3 not found
in the current page snapshot. Try capturing new snapshot.` Its state lives in the browser page, which Connapse can't copy:
the MCP spec makes session ids optional and lets a server end a session at any time (primary), so a "shown" record must
live where every instance reads it. Mem0 renumbers UUIDs as 0, 1, 2 per call inside its own update step, with a comment
calling it "anti-hallucination" (primary, source), but a per-call number means a different fact on the next call. LangMem
writes a wrong but well-formed UUID as a new memory, and Graphiti deletes whichever real fact a lookalike UUID names
(both primary, source). That is the CON-321 failure: checking only that an id exists doesn't catch it.

**Design:** a table `memory_fact_handles (user_id, handle, fact_id)`, unique on both (user_id, handle) and (user_id,
fact_id). A fact gets the user's next number the first time any memory tool shows it to that user. `memory_correct`
and `memory_forget` take a handle or a full id, and refuse either unless the caller was shown it. That rule includes full
ids, which other clients still send. The refusal names the bad handle and says to recall the fact first. Listing
recently shown handles in the refusal is untested, so it is left out.

## Rare-word ranking and partial words
Rare-word (IDF) ranking costs little in plain SQL when the per-word fact counts are materialized once per query.
Measured on a throwaway `postgres:17` container with 1 CPU and 2,000 facts in a space (primary, own measurement):
- **Materialized CTE counting each query word's facts:** 9.3 ms.
- **The same count as a correlated subquery:** 3,001 ms, because Postgres recounted it for every matching row. This is
  the trap to avoid.
- **Formula:** IDF is ln(1 + (N − df + 0.5)/(df + 0.5)), where N is the number of facts in the space and df is how many
  contain the word. This is the Lucene and rank_bm25 form (tertiary, widely copied).
- **k1 and b don't matter for facts of 5–20 words.** A word almost never repeats in one fact, so BM25 reduces to the sum
  of IDF × field weight over the matched words. That is an inference; no study covers texts this short.
- **Field weight:** `ts_rank` with one word gives the weight of the field it appears in (subject A to reason D), per the
  PostgreSQL docs (primary).
- **Several-word OR queries:** multiplying by the share of query words matched (Lucene's old "coord" factor) is a
  suggestion, not sourced.

**Partial words with pg_trgm** (PostgreSQL docs, primary; scores measured):
- `word_similarity` scores Postgres→PostgreSQL 0.89, but also time→timezone 0.80 and design→designer 0.86.
- Abbreviations (cfg, k8s, db) score at most 0.33, so trigrams never catch them.
- Use trigrams only for query words that matched no fact through full-text search, with `strict_word_similarity >= 0.5`
  against subject and value, and rank those hits below the full-text hits. Supabase fuses full-text and vector lists
  with reciprocal rank fusion at k = 50 (vendor docs). No primary source fuses full-text with trigram.

## Compact results, a subject's other facts, hints and dates
**Compact results:** one plain-text line per fact, grouped under its subject, cuts the tokens agents pay for without
losing what they cite.
- Anthropic's tool-writing guide (primary) offers concise and detailed response modes. Its concise example was 72 tokens
  against 206 for the detailed one, and it says to replace low-level ids with meaningful words to cut hallucination.
- Studies of table formats disagree. A one-model test ranked Markdown key-value first (secondary). TQA-Bench put JSON
  last (primary). TOON reports about 40% fewer tokens than JSON at equal accuracy (vendor-run).
- Zep renders facts as `FACT (Date range: from - to)` lines (primary), the closest prior art for facts with dates.
- **Design:** the default output is one line per fact under its subject, like
  `f12 employer: Acme (since 2025-03-01)` or `f7 employer: Initech (2023-01-10 to 2026-10-02, ended; now f12)`.
  Default fields are left out, and a `detailed` option keeps the full JSON.

**A subject's other facts:** add them to a hit, rather than putting every describing fact into every fact's search text.
- LongMemEval (primary) appended extracted facts to each index key and raised recall@k by 9.4%.
- Zep (primary) returns the matched entities' summaries beside the facts. A-MEM (primary) returns a hit's linked notes
  with it.
- Graphiti (primary docs) favours facts near the top hit's entity.
- No source gives a cap. **Design:** whichever subjects own the top hits, list their other current facts in a block,
  capped per subject, and also score a subject by its best-matching describing fact, so "SSO gateway" reaches Wicket.
  Connapse's `search_vector` is computed from the row alone, so a subject's describing facts are matched at query time,
  not copied into its other facts' text.

**Empty results:** the MCP spec (SEP-1303, primary) treats an empty match as a normal result, not an error. Anthropic's
guide asks for "specific and actionable" messages. No ablation shows "did you mean" hints cut retries.
- **Design:** say nothing matched, name up to 3 nearest subjects by trigram similarity, and name the next call.

**Dates:** month names have to be written into the searched text, since a raw `2027-01-19` never matches "January".
LongMemEval's date-range step raised temporal recall by 6.8–11.3% with GPT-4o (primary). **Design:** write each date
value's month name and year ("January 2027") into the indexed text.

## Conflicts and uncertainties
- **Result format:** the ranking of formats conflicts across sources (Markdown key-value first in one test, JSON last in
  another, XML best on Claude Haiku in TOON's run). The choice of plain lines rests on token cost, not on proven
  accuracy.
- **Untested choices:** the sibling cap, the trigram fallback threshold (0.5) and the hint wording are untested
  heuristics.
- **Handles versus full ids:** the handles research suggested refusing raw UUIDs outright. This design accepts a full id
  the caller was shown, since the ticket refuses only unshown ids and other clients send full ids.
- **Latency:** measured on synthetic data with 1 CPU, not on Azure.

## Gaps — what we did not find
- No measurement compares handle spellings (`f12`, `12`, short hashes).
- No ablation shows that empty-result hints or listing valid handles helps an agent recover.
- No study of BM25 parameters on texts of 5–20 words.

## Source quality assessment
- **Primary:** most load-bearing claims rest on primary sources: project source code (Playwright, Mem0, LangMem,
  Graphiti), the PostgreSQL and MCP specs, peer-reviewed or arXiv papers (LongMemEval, Zep, A-MEM, HippoRAG 2,
  TQA-Bench), Anthropic's guide, and our own latency measurements.
- **Vendor or weaker:** the format-ranking numbers (Gigazine's report of the Improving Agents test, TOON's own
  benchmark), the IDF formula's provenance, and the RRF examples are secondary, vendor or tertiary.

## Sources
**Primary:**
- Anthropic, *Writing tools for agents*: https://www.anthropic.com/engineering/writing-tools-for-agents
- Anthropic memory tool: https://platform.claude.com/docs/en/agents-and-tools/tool-use/memory-tool
- Playwright: https://github.com/microsoft/playwright/blob/main/packages/injected/src/ariaSnapshot.ts and `packages/playwright-core/src/tools/backend/tab.ts`
- Mem0: https://github.com/mem0ai/mem0/blob/main/mem0/memory/main.py and https://docs.mem0.ai/platform/features/graph-memory
- LangMem: https://github.com/langchain-ai/langmem/blob/main/src/langmem/knowledge/tools.py
- Graphiti: https://github.com/getzep/graphiti/blob/main/mcp_server/src/graphiti_mcp_server.py and https://help.getzep.com/graphiti/working-with-data/searching
- MCP spec: https://modelcontextprotocol.io/specification/2025-06-18/basic/transports#session-management and https://modelcontextprotocol.io/seps/1303-input-validation-errors-as-tool-execution-errors.md
- PostgreSQL pg_trgm: https://www.postgresql.org/docs/17/pgtrgm.html
- Papers:
  - LongMemEval: https://arxiv.org/html/2410.10813v2
  - Zep: https://arxiv.org/html/2501.13956
  - A-MEM: https://arxiv.org/html/2502.12110
  - HippoRAG 2: https://arxiv.org/html/2502.14802
  - TQA-Bench: https://arxiv.org/pdf/2411.19504

**Secondary or vendor:**
- Supabase hybrid search: https://supabase.com/docs/guides/ai/hybrid-search
- ParadeDB on BM25 in Postgres: https://www.paradedb.com/learn/search-in-postgresql/bm25
- Elasticsearch `combined_fields`: https://www.elastic.co/guide/en/elasticsearch/reference/current/query-dsl-combined-fields-query.html
- TOON benchmarks: https://toonformat.dev/guide/benchmarks
- Gigazine on table formats: https://gigazine.net/gsc_news/en/20251007-ai-table-format

**Tertiary:**
- plpgsql_bm25: https://github.com/jankovicsandras/plpgsql_bm25
- agentpatterns.ai on retrieval refusals: https://agentpatterns.ai/patterns/agent-design/retrieval-tool-refusal/
