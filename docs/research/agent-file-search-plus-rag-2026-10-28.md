# Should agents get file search next to Connapse's RAG search?
**Date:** 2026-10-28
**Status:** Reviewed
**Built on:** retrieval-eval-github-sources-2026-09-24.md, keyword-query-matching-2026-09-26.md (Connapse MCP was not available in this session, so no corpus pre-check ran)
**Ticket:** CON-142. Plan: `docs/plans/con-142-file-search-plus-rag.md`. Measured run: `eval/agent-search/results.md`.

## Executive summary
Every product that offers agents meaning search (embedding RAG) pairs it with exact-text search; none offers meaning search alone, and agents left to choose default to grep. The published evidence on which is better conflicts and tracks who is publishing it: index vendors (Cursor, Augment, Zilliz) report gains from adding semantic search, grep-first teams (Claude Code, Cognition, Augment's own SWE-bench agent) report grep was enough, and the one independent 2026 paper found a prebuilt vector index beat agentic grep on read-only repository questions. No independent test of "both, with routing instructions" against either alone exists, which is the gap CON-142's run fills. For the file-search tool itself, ripgrep is the pick: agents already know its syntax, it needs no index, and on Connapse's own source it answered in 0.3–0.4 s at 108 MB of text. Zoekt is the scale-up path once a container passes several GB.

## Research brief
**Question:** How do established agents combine exact-text and meaning search, which file-search tool should Connapse give an agent, and should that tool search the stored chunks or the original files?
**Sub-questions:** (1) how products and papers combine and route the two kinds of search; (2) grep vs ripgrep vs Zoekt vs ast-grep on speed, accuracy and agent fit; (3) chunk text in Postgres vs original files in object storage on speed, chunk-edge misses and cost.
**Out of scope:** building a product feature; GraphRAG itself (CON-28).
**Success criteria:** a tool to prototype, a storage choice, and a sourced picture of when agents use each search.

## How agents combine exact-text and meaning search
Products split into three camps; the agent picks grep by default in all of them.

| Product | Exact-text | Meaning search | What they report |
|---|---|---|---|
| Claude Code | Grep tool on bundled ripgrep | none (dropped a local vector DB) | "agentic search generally works better"; evidence "internal benchmarks… but mostly vibes" (Boris Cherny, secondhand quotes) |
| Cursor | grep | codebase_search over an embedding index | +12.5% average accuracy on codebase questions (6.5–23.5% by model); +2.6% code retention on 1,000+ file repos (vendor, 2025-11-06) |
| Sourcegraph Cody | Zoekt / keyword with adapted BM25 | dropped embeddings in v5.3 (Feb 2024) | embeddings did not scale to many large repos and sent code to third-party APIs |
| GitHub Copilot | code search | code embedding model | +37.6% retrieval quality from a new embedding model (InfoQ summary) |
| Windsurf / Cognition | SWE-grep: RL-trained grep/glob/read subagent | none | 20x faster than Haiku 4.5 at matching retrieval quality; embeddings "can even be counterproductive" |
| Augment | grep in its SWE-bench agent | Context Engine (MCP) | +30–80% on 300 Elasticsearch PRs (vendor, no baseline published); its own engineer: "grep and find were sufficient" for SWE-bench |
| Codex CLI | `rg` per system prompt | none | — |
| Continue | agent file/search tools | `@Codebase` embeddings, deprecated | — |
| Zilliz claude-context | grep | vector MCP tool | tokens −39%, tool calls 8.3→5.3, answer F1 unchanged at 0.40 (vendor) |

Papers:
- **Deep Agentic Search (arXiv 2608.01507, Aug 2026):** on SWE-QA, a prebuilt vector index answered 65.2% correctly at under half the cost per correct answer; a planner with a grep subagent answered 46.2%, and 41.8% of its failures happened at the planner-to-subagent hand-off.
- **"Is Grep All You Need?" (arXiv 2605.15184):** on conversation memory (LongMemEval, 116 questions) grep generally beat vector retrieval, but the harness (Claude Code vs Codex vs Gemini CLI) moved scores by as much as the retrieval method.
- **Anthropic Contextual Retrieval:** top-20 retrieval failures fell 35% with contextual embeddings, 49% adding BM25, 67% adding a reranker — the argument for hybrid search inside RAG, which Connapse already does.
- **Lexical vs dense (arXiv 2605.14503, 2411.19463):** BM25 wins on code queries with hard lexical constraints; dense encoders win on natural-language-to-code queries.
- **Anchoring (arXiv 2606.26979):** agents navigate by keyword search and miss call-graph structure; injecting static-analysis tags gave +2.2 pp localization for ~10% more tokens. Relevant to whether code should stay grep-searchable next to the CON-28 graph.

Routing instructions in the wild are short: Cursor's docs say semantic search for natural-language questions, grep for exact symbols; Claude Code's Grep description says "ALWAYS use Grep for search tasks"; users report agents ignore even that and shell out to `grep`.

## Which file-search tool to give an agent
**Ripgrep, measured on Connapse's own source.** Corpus: 773 `.cs`, `.razor` and `.md` files from `src/` and `docs/` at commit 950c61fa, 3.6 MB of text, plus a 30x copy (108 MB, 23,190 files). Median of 5 warm runs on this Windows PC; every time includes process start (~80 ms) or the HTTP round trip.

| Query | grep 3.6 MB | rg 3.6 MB | Zoekt 3.6 MB | grep 108 MB | rg 108 MB | Zoekt 108 MB |
|---|---|---|---|---|---|---|
| identifier `VectorColumnManager` | 109 ms | 86 ms | 73 ms | 1,956 ms | 345 ms | 89 ms |
| error message | 126 ms | 98 ms | 91 ms | 1,951 ms | 298 ms | 72 ms |
| regex `class \w+Connector\b` | 119 ms | 87 ms | 74 ms | 2,064 ms | 344 ms | 87 ms |
| case-insensitive `rate limit` | 121 ms | 98 ms | 92 ms | 2,225 ms | 395 ms | 104 ms |
| common `async Task` (40,500 lines at 108 MB) | 114 ms | 130 ms | 99 ms | 2,134 ms | 1,615 ms | 113 ms |

- **Zoekt** (trigram index, run in a container capped at 2 CPUs and 4 GB): building the index took 2.4 s for 3.6 MB and 69 s for 108 MB, and the 108 MB index was 635 MB on disk (about 6x the text; its builder logs "overhead 2.8" against its own size count). It is the fastest at scale but adds an index to build, store and keep in sync, a sidecar service with its own query language (`/regex/` returned 0 files; the bare regex returned 12), and no .NET library.
- **ast-grep** (structural search over syntax trees) only works on code, and patterns are easy to get wrong: `class $N : IDocumentParser` found 0 of the 5 parser classes, `public class $N : IDocumentParser` found 3 (it misses classes with primary constructors and `partial`), while one ripgrep regex found all 5. ast-grep's own docs say models need several iterations and a test loop.
- **grep** is 5–7x slower than rg at 108 MB with the same matches.
- **Agent fit:** Claude Code and Codex both run rg, and LLM-written regex is accurate (o3-mini produced >95%-accurate regexes on 95.3% of full-match tasks, arXiv 2503.20579). Any rg tool must refuse `--pre`, `-z` and `--search-zip`, which run external programs (Codex CVE-2025-54558).

Decision: prototype a ripgrep-semantics tool. Revisit Zoekt when a container's text passes a few GB, where rg's ~0.6 GB/s per core (GitHub's measurement) starts to cost seconds per query.

## Search stored chunks or original files
Connapse already holds every file's text twice: the original in object storage and the parsed chunks in Postgres. Measured on this corpus after ingestion (see `eval/agent-search/results.md`, "Chunks vs files"); the research says:
- **Chunks in Postgres:** a pg_trgm GIN index (every 3-character substring) accelerates `ILIKE` and `~` regex with a recheck, so results are exact; patterns with no extractable trigrams degrade to a full index scan. Full-text `tsvector` is unusable for exact search: it splits `light_bulb`, drops punctuation, stems and removes stop words. Chunk text is not a faithful copy: `PrependHeaderPath` (default on) adds a heading breadcrumb that is not in the file, overlap duplicates text across neighbours, offsets are into parsed text rather than file lines, and PDF/DOCX text is reconstructed.
- **Original files:** exact text with real line numbers, but each search means listing and fetching objects (~100–200 ms first-byte per S3 GET; ~$0.004 per 10,000 GETs, so time not money is the cost) or keeping a local text mirror. GitHub and Sourcegraph both keep their own local copy rather than scanning storage per query.
- **Chunk-edge misses:** no study measures how often an exact string straddles a chunk boundary; overlap (Connapse default 50 tokens) catches strings shorter than the overlap.

## Conflicts and uncertainties
- Cursor, Augment and Zilliz (who sell indexes) report gains from meaning search; Claude Code, Cognition and Augment's SWE-bench team report grep is enough. The independent 2608.01507 paper sides with the index on read-only questions. Each vendor number is single-source.
- Cherny's statements are secondhand; the original post could not be opened.
- Zoekt's published index overhead (3–3.5x) is below what this run measured (~6x of raw text); the design doc counts differently.

## Gaps
- No published data on how often agents call an optional semantic MCP tool, or which tool-description wording changes that.
- No independent benchmark of both tools with routing instructions against either alone, on a mixed code-and-docs corpus.
- No measured rate of exact strings straddling chunk boundaries in the literature.
- No Azure Blob or MinIO per-GET latency numbers.

## Source quality
Roughly half the product claims are vendor primary sources with no replication; the papers are primary but four of five are 2026 preprints. The speed table and ast-grep counts are this session's own measurements, reproducible with the scripts listed in `eval/agent-search/README.md`.

## Sources
Primary: cursor.com/blog/semsearch; cursor.com/docs/context/codebase-indexing; sourcegraph.com/blog/sourcegraph-5-3-changelog; sourcegraph.com/docs/cody/faq; cognition.com/blog/swe-grep; augmentcode.com/product/context-engine-mcp; raw.githubusercontent.com/openai/codex/main/codex-rs/core/gpt_5_codex_prompt.md; docs.continue.dev/reference/deprecated-codebase; anthropic.com/news/contextual-retrieval; github.blog (technology behind GitHub's new code search; Copilot embedding model); arXiv 2608.01507, 2605.15184, 2605.14503, 2411.19463, 2606.26979, 2503.09089, 2503.20579; burntsushi.net/ripgrep; swtch.com/~rsc/regexp/regexp4.html; github.com/sourcegraph/zoekt doc/design.md; ast-grep.github.io/advanced/prompting.html; postgresql.org/docs/current/pgtrgm.html; postgresql.org/docs/current/textsearch-controls.html; docs.aws.amazon.com/AmazonS3/latest/userguide/optimizing-performance.html; osv.dev CVE-2025-54558.
Secondary: newsletter.pragmaticengineer.com (Boris Cherny); jxnl.co (Augment SWE-bench); infoq.com (Copilot embeddings); Zilliz claude-context evaluation README; github.com/codemod/benchmark (ast-grep sponsor).
Tertiary: claudeissues.com (agents shelling out to grep); S3 pricing summaries.
