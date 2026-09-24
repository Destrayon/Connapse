# How should Connapse chunk GitHub markdown docs and GitHub issues/PRs, and does it already?
**Date:** 2026-09-23
**Status:** Reviewed
**Built on:** github-connector-implementation-2026-09-09.md, next-connectors-rag-shape-graphrag-2026-09-09.md

## Executive summary
Connapse's two GitHub chunkers use the right units: the DocumentAware chunker splits markdown docs at headings and prepends a heading breadcrumb, and the Record chunker keeps an issue or pull request whole, splitting at comment boundaries with the record header repeated on every piece. The research confirms both units; neither needs redesigning. The gaps are in what goes into the chunks. Measured on the Destrayon/Connapse repository (87 docs, 506 issues and PRs): 22% of issue chunks contain CodeRabbit bot comments, about 10% of doc chunks cut a code block in half, 8% of doc chunks are under 50 tokens with a one-word breadcrumb such as "How", the repository name never appears in a chunk, and roughly 18% of chunks exceed the 512-token limit. Separately, Connapse sends nomic-embed-text no task prefixes, which the model's authors say are required; the cost of omitting them has never been published. The main uncertainty is magnitude: no study ablates issue chunking, and the breadcrumb gain (+24% MRR) rests on one 2026 paper.

## Research brief
**Question:** What is 2025–2026 best practice for chunking (a) markdown documentation from GitHub repositories and (b) GitHub issues and pull requests for hybrid retrieval with a small local embedding model, and how does Connapse compare?

**Sub-questions:**
1. How should markdown documentation be chunked (heading splits, breadcrumbs, size, code blocks and tables, small sections, parent-child and late chunking)?
2. How should issues and pull requests be chunked (unit, header, noise, review comments, long threads)?
3. What constraints do the embedding model and hybrid retrieval impose (nomic prefixes, size vs quality, metadata placement, Postgres full-text specifics, reranking)?
4. What does Connapse do today, measured on real data?

**Out of scope:** repository code chunking (excluded on purpose), GraphRAG edges, reranker selection.

**Success criteria:** a gap list against Connapse's current chunkers, ranked, with concrete settings.

## Findings by sub-question

### Markdown documentation chunking
Best practice for markdown docs is heading-based sections, merged when tiny and split only when too large, with a document-title-plus-heading breadcrumb written into the chunk text.
- **Heading splits are universal.** LlamaIndex MarkdownNodeParser, LangChain MarkdownHeaderTextSplitter, Docling HybridChunker, Unstructured chunk_by_title and Supabase's docs search all cut at headings and treat code fences as opaque (primary, source code; high).
- **Tiny sections are merged** with same-parent neighbours by Docling (`merge_peers`), Unstructured (`combine_text_under_n_chars`) and Kapa.ai (primary; high).
- **Breadcrumbs:** arXiv 2608.00824 (2026) prepended `[doc > h1 > h2]` without any LLM on a 196k-chunk markdown knowledge base and raised MRR@5 from 0.374 to 0.463 (+23.8%) (primary, single source; medium). Snowflake found most of markdown splitting's 5–10 point gain disappears once document-level context is prepended (secondary; medium). The breadcrumb belongs in the stored text so both vector and keyword search see it.
- **Size and overlap:** 256–512 tokens for fact lookup, 512–1,024 for analytical questions (NVIDIA; arXiv 2505.21700). Chroma found overlap lowers precision and 800/400 is the worst configuration (primary; high). Use zero overlap at heading or paragraph boundaries.
- **Code and tables:** never split inside a fence, keep the sentence introducing a block with it, and split an oversized table by rows with its header repeated (Docling, Unstructured; high).
- **Skip for now:** LLM-generated per-chunk context (Snowflake measured it hurting with Llama 3), late chunking (small and model-dependent), and parent-child retrieval (no controlled measurement found).

### GitHub issue and pull request chunking
Best practice for issues and pull requests is one document per record, split at comment boundaries when oversized, with a short identity header on every chunk and noise removed before embedding; no study compares chunking units.
- **Production connectors mostly do less than Connapse.** The Onyx GitHub connector indexes the body only and never fetches comments; LlamaIndex and LangChain loaders index title and body only. Qodo pr-agent embeds the issue and each comment as separate records and drops comments under 10 words (primary, source code; high).
- **Headers help.** Onyx prefixes every chunk with the title and appends metadata, capped at 25% of the chunk. arXiv 2601.11863 raised hit@5 from 33% to 55% with a short metadata prefix on near-identical documents (primary, single source; medium).
- **Size:** a duplicate-bug-report thesis found a 500-token input cap beat 350 and 1,000, because long logs add noise (secondary, 2023; medium).
- **Noise:** pr-agent drops short comments; Onyx's Jira connector filters by author. No connector read filters bot comments, so the recommendation to drop them rests on reasoning and on Connapse's own measurement below.
- **Review comments** are understood through their file path plus a few lines of the diff hunk; code-review retrieval papers pair the two (primary; medium). Full diffs are not needed.

### Embedding model and hybrid retrieval constraints
The embedding model and Postgres keyword ranking favour 256–512-token chunks, task prefixes for nomic, and short identity text in the chunk.
- **nomic-embed-text requires `search_document: ` and `search_query: ` prefixes** (model card and paper; primary; high). The size of the loss when they are omitted has not been published.
- **nomic was trained at 2,048 tokens;** its long-input needle retrieval is weak (LongEmbed: 39.5% needle), so its 8,192 window is not a reason for larger chunks (primary; high).
- **Metadata placement:** put identity (repository, path, title, heading trail) in the embedded text; keep labels, authors and dates in filters or the keyword index (inference from Onyx and Anthropic; medium).
- **Postgres `ts_rank_cd` has no IDF or length normalisation,** so boilerplate and longer chunks inflate scores. Weighting titles and headings with `setweight` A is the documented practice (ParadeDB; secondary). Strip markdown noise and HTML comments before indexing.
- **Duplicates** crowd out the top results; collapse chunks with identical content hashes (secondary; medium).

### What Connapse does today, measured
Connapse's units are right; its content hygiene is not. Figures are from the connapse-3b test instance, Destrayon/Connapse, 512-token maximum, 50 overlap, breadcrumbs on.

| Practice | Docs (DocumentAware) | Issues and PRs (Record) |
|---|---|---|
| Right unit (sections / whole record split at comments) | Yes | Yes |
| Identity header in every chunk | Heading trail only; no repository, path or file title (a PR-template section chunks as just "How") | Number, title, facts; no repository name |
| Chunks within the 512-token limit | 197 of 1,887 exceed it (13 by more than 50) | 534 of 2,895 exceed it (23 by more than 50) |
| Code blocks kept whole | No: ~191 of 1,887 chunks contain an odd number of fences | Not checked |
| Tiny chunks merged | No: 150 under 50 tokens | n/a (1 under 50) |
| Noise removed | HTML comments from templates are indexed | Minimized comments dropped; bot comments kept (630 chunks, 22%, almost all CodeRabbit); short comments and long logs kept |
| Review-comment context | n/a | File path in the delimiter; no diff hunk |
| nomic task prefixes | Not sent | Not sent |
| Keyword index weighting | `to_tsvector('english', content)`, unweighted | Same |

Issue records average 5.7 chunks at 512 tokens, not the "most records fit one chunk" the 2026-09-09 report assumed; bot comments are a large part of that.

## Conflicts and uncertainties
- Document-level context: Snowflake found cheap document metadata raised accuracy sharply while LLM per-chunk context hurt on Llama 3; Anthropic reports large gains from LLM context and "very limited" gains from generic summaries. Different corpora and context types; the cheap breadcrumb is supported by both.
- The +23.8% breadcrumb result is a single source whose own relevance judges disagreed when prefixes were stripped (kappa 0.45 to 0.04).
- Issue chunking has no ablation at all; the recommendations come from connector practice plus Connapse's own measurements.
- Stack traces helped in one bug-localisation case and hurt in another (Jahan thesis).
- The over-limit chunks are an inference about cause: the breadcrumb and header appear to be added after the recursive fallback has already used the full budget, with overlap on top. Not yet traced in code.

## Gaps — what we did not find
- Any measurement of the cost of omitting nomic's task prefixes.
- Any study comparing whole-ticket, per-comment and windowed chunking.
- How Glean, Dosu, Atlassian Rovo or GitHub's own semantic issue search chunk records.
- Evidence on front matter or admonitions in docs.
- Whether a reranker changes the optimal chunk size.

## Source quality assessment
Mostly primary: splitter and connector source code (LlamaIndex, LangChain, Docling, Unstructured, Supabase, Onyx, pr-agent), model documentation (Nomic), vendor studies with numbers (Chroma, NVIDIA, Anthropic), and arXiv papers. Three load-bearing claims are single-source: the breadcrumb MRR gain (arXiv 2608.00824), the metadata-prefix hit-rate gain (arXiv 2601.11863), and the 500-token cap for bug reports (a 2023 thesis). Every Connapse finding is measured directly on the test database or read from the code. The prior reports were primary-sourced; only their conclusions about units were carried forward.

## Sources
**Primary:** [LlamaIndex MarkdownNodeParser](https://raw.githubusercontent.com/run-llama/llama_index/main/llama-index-core/llama_index/core/node_parser/file/markdown.py) · [LangChain markdown splitter](https://raw.githubusercontent.com/langchain-ai/langchain/master/libs/text-splitters/langchain_text_splitters/markdown.py) · [Docling chunking](https://docling-project.github.io/docling/concepts/chunking/) · [Unstructured chunking](https://docs.unstructured.io/open-source/core-functionality/chunking) · [Supabase docs search](https://raw.githubusercontent.com/supabase/supabase/master/apps/docs/scripts/search/sources/markdown.ts) · [Kapa writing guide](https://docs.kapa.ai/improving/writing-best-practices) · [arXiv 2608.00824](https://arxiv.org/html/2608.00824) · [Chroma chunking](https://www.trychroma.com/research/evaluating-chunking) · [arXiv 2505.21700](https://arxiv.org/abs/2505.21700) · [NVIDIA chunking](https://developer.nvidia.com/blog/finding-the-best-chunking-strategy-for-accurate-ai-responses/) · [arXiv 2606.00881](https://arxiv.org/html/2606.00881v1) · [arXiv 2504.19754](https://arxiv.org/html/2504.19754) · [arXiv 2603.24556](https://arxiv.org/abs/2603.24556) · [Anthropic Contextual Retrieval](https://www.anthropic.com/engineering/contextual-retrieval) · [nomic-embed-text card](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5) · [Nomic paper](https://ar5iv.labs.arxiv.org/html/2402.01613) · [LongEmbed](https://arxiv.org/html/2404.12096v2) · [Postgres parsers](https://www.postgresql.org/docs/current/textsearch-parsers.html) · [Onyx GitHub connector](https://github.com/onyx-dot-app/onyx/blob/main/backend/onyx/connectors/github/connector.py) · [pr-agent similar issues](https://github.com/qodo-ai/pr-agent/blob/main/pr_agent/tools/pr_similar_issue.py) · [arXiv 2601.11863](https://arxiv.org/html/2601.11863) · [arXiv 2506.11591](https://arxiv.org/abs/2506.11591) · [arXiv 2512.01356](https://arxiv.org/abs/2512.01356) · [CodeRabbit learnings](https://docs.coderabbit.ai/guides/learnings)

**Secondary:** [Snowflake chunking in finance RAG](https://www.snowflake.com/en/blog/engineering/impact-retrieval-chunking-finance-rag/) · [ParadeDB hybrid search](https://www.paradedb.com/blog/hybrid-search-in-postgresql-the-missing-manual) · [Tiger Data pg_textsearch](https://www.tigerdata.com/blog/introducing-pg_textsearch-true-bm25-ranking-hybrid-retrieval-postgres) · [Supabase hybrid search](https://supabase.com/docs/guides/ai/hybrid-search) · [Jahan thesis](https://raise.cs.dal.ca/thesis/Sigma-RAD.pdf) · [RAG4Tickets](https://arxiv.org/abs/2510.08667) · [Greptile](https://www.greptile.com/blog/make-llms-shut-up)

**Tertiary (not relied on):** [GitHub improved issue search](https://github.blog/changelog/2026-01-29-improved-search-for-github-issues-in-public-preview/) · [FloTorch via premai.io](https://www.premai.io/blog/rag-chunking-strategies-the-2026-benchmark-guide/) · [breadcrumb headers write-up](https://dev.to/kartikeyraj/free-contextual-chunk-headers-heading-aware-chunking-for-hybrid-retrieval-560)
