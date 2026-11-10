# How should agents find and save Connapse memories when their wording differs from the stored fact?
**Date:** 2026-11-09
**Status:** Reviewed
**Built on:** no prior corpus material (the Connapse MCP server was not available in this session)

## Executive summary

Connapse memory search finds a fact only when the question shares a whole English word stem with it. That is the
documented weakness of plain keyword search on short facts. The evidence favours **hybrid matching inside
`memory_recall`**: keep the keyword search, add an embedding of each fact written as a short sentence, and fuse the
two. Add trigram matching for partial words, plus clearer tool text and error hints.

Production memory systems converged on the same answer. Graphiti/Zep and Mem0's 2026 algorithm both fuse
semantic, BM25 keyword and entity matching. None of the systems surveyed has an LLM write graph queries at read
time by default. Zero-shot text-to-Cypher is about 60% accurate, and it fails on the same wording mismatch it would
be meant to fix.

The main uncertainty is size. No source measures hybrid search on a store as small and terse as one agent's memory
space (tens to hundreds of facts). The CON-313 test set exists to measure exactly that.

## Research brief

**Question:** which change makes Connapse memories easiest for an agent to find and save when the question is
worded differently from the stored fact: better matching (partial words, meaning), simpler memory tools, or
LLM-written graph queries such as Cypher?

**Sub-questions:**
1. How does today's memory search work, and where does it miss?
2. Lexical, dense and hybrid retrieval for short subject–attribute–value facts: what works?
3. LLM-written graph queries (text-to-Cypher): accuracy, safety, and fit on PostgreSQL.
4. Tool design: does simplifying the memory tools help agents find and save?
5. What do production agent-memory systems use at read time, and what did their ablations show?
6. How should agent memory be scored, and how should a pass bar be set?

**Out of scope:** changing document search, cloud deployment, UI.

**Success criteria:** a recommended option with evidence, and a scoring method and pass bar for the test set.

## Findings by sub-question

### 1. Today's memory search path in Connapse

Connapse memory recall is English full-text search on one weighted column; it has no partial-word or meaning match.

- `PostgresMemoryStore.QueryAsync` searches a `search_vector` weighted subject A, attribute B, value C, reason D.
  The question's words are OR-ed and ranked by `ts_rank`, which has no inverse document frequency (Postgres docs).
- `DistinctiveWordsAsync` drops any query word that more than half of a container's facts match, once the
  container holds at least 8 facts. This stands in for the missing IDF.
- The `subject` and `attribute` filters are exact, case-insensitive `ILIKE` matches.
- MCP tools (`src/Connapse.Web/Mcp/McpTools.Memory.cs`): `memory_spaces`, `memory_space_create`,
  `memory_remember`, `memory_correct`, `memory_forget`, `memory_recall` and `memory_timeline`. `memory_recall`
  takes `query`, `subject`, `attribute`, `asOf`, `includeHistory`, `limit` (default 10) and paging.
- **Consequence:** "who reviews pull requests" cannot find a fact whose attribute is `code review owner` unless a
  stem is shared ("review" is). "k8s" cannot find "Kubernetes", and "Postgres" cannot find "PostgreSQL". A question
  that names neither the subject nor any stem of the value returns nothing.
- **Reusable:** document search already runs vector, keyword and hybrid search with reranking (`Connapse.Search`),
  through per-model embedding columns and partial IVFFlat indexes (`VectorColumnManager`). Memory facts are not
  embedded today.

### 2. Lexical, dense and hybrid retrieval for short facts

For short entity-centred facts, keyword search is strong and dense search weak. The two are complementary, and
hybrid wins.

- **Keyword search beats dense on entity facts.** On EntityQuestions, which is built from triples, BM25 reaches
  72.0% top-20 and DPR 49.7% (Sciavolino et al. 2021, arXiv 2109.08535, P). BEIR finds BM25 a strong zero-shot
  baseline (Thakur et al. 2021, P).
- **They are complementary.** Removing BM25 from a hybrid hurt more than removing the dense retriever (ACL 2023
  Findings 208, P).
- **Write facts as sentences before embedding.** Dense X Retrieval found that indexing ~11-word propositions
  improved Recall@20 by +10 points for unsupervised encoders and +2.7 for supervised ones, most for rare entities
  (Chen et al. 2023, arXiv 2312.06648, P). Turning a subject–attribute–value triple into a sentence is our inference
  from that result, not something the paper tested.
- **Index-key expansion helps.** LongMemEval found that appending extracted facts to the index key raised recall@k
  by 9.4% and accuracy by 5.4% on average. Replacing the key with the condensed form alone did not help (Wu et al.
  2024, arXiv 2410.10813, P).
- **Dense pitfalls.** Embeddings favour length and literal overlap (arXiv 2503.05037, P) and are near chance on
  numbers, 0.54 (arXiv 2509.05691, P). Version numbers and dates must stay on the keyword side.
- **Partial words.** `pg_trgm` `word_similarity` / `<%` with a GIN or GiST index fixes spelling variants and
  prefixes such as Postgres → PostgreSQL. It cannot fix abbreviations that share no letters, such as k8s →
  Kubernetes (PostgreSQL docs, P).
- **Query rewriting.** HyDE (arXiv 2212.10496) and query2doc (arXiv 2303.07678; BM25 +3–15%) help, but each costs
  an LLM call per query. The calling agent can rewrite its own query instead.
- **Fusion.** A tuned convex combination of scores beats reciprocal-rank fusion (Bruch et al. 2022, arXiv
  2210.11934, P).
- **Saying "nothing found" is hard.** A cosine cutoff is a weak no-match signal: GuidedRAG still retrieved for
  19–28% of unanswerable questions (arXiv 2607.26071, P), and top-1 cosine separates answerable from unanswerable at
  about 0.75 AUC (tertiary). The cutoff must be set per embedding model, which is what CON-278 did for document
  search. A reranker score is a better signal.

### 3. LLM-written graph queries (text-to-Cypher)

Text-to-Cypher is too inaccurate and too risky for a permission-trimmed store. It also fails on the wording mismatch
that CON-313 needs fixed.

- **Accuracy.** CypherBench zero-shot execution accuracy is about 60% for Claude 3.5 Sonnet and GPT-4o (P).
  Text-to-SQL is 60–80% one-shot and reaches the 90s only with agent loops that retry (S).
- **Failure shapes.** Entity-linking mismatch, where the question's word is not the stored node's name, is the
  CON-313 problem itself. Others are reversed relationship direction and invented relationships (CypherBench, P).
- **On PostgreSQL.** Apache AGE is generally available on Azure Database for PostgreSQL, and 1.7.0 adds PG18
  support. Project activity is low, row-level security arrived only in 1.7.0, and CVE-2022-45786 affected its
  drivers (P).
- **Safety.** An agent-written query can be trusted only if the database enforces per-user row-level security on
  every node and edge. This is our inference. Connapse filters memory by space and owner in application code, so
  arbitrary queries would bypass that filter.
- **Production use.** None of the surveyed memory systems uses it by default (sub-question 5).

### 4. Tool design

Clearer tool text and more helpful errors help agents more than adding tools, and overlap between tools is the
main risk at 7 tools.

- **Anthropic, "Writing effective tools for agents" (P).** Consolidate tools, use shared prefixes and unambiguous
  parameter names, offer concise vs detailed output and paging, and return errors that say how to fix the call.
- **OpenAI function-calling guide (P).** Fewer than 20 functions, enums and strict schemas.
- **Tool count.** Adaptive tool shortlists scored 93.1% vs 87.1% (arXiv 2605.24660, P). At 7 tools, tools that
  overlap in purpose (`memory_recall` vs `memory_timeline`) matter more than the count.
- **Anthropic's memory tool (P).** View first, errors state the fix, and update rather than create duplicates.
  LangMem has two tools, `manage_memory` and `search_memory` (P), and Letta consolidated its tool set (P).
- **"Did you mean" hints** on empty results: recommended practice, but no measured evidence was found.

### 5. What production agent-memory systems use at read time

The production systems run hybrid search. Graph structure adds little on single-hop lookups, and LLM-written
queries are off by default where they exist at all.

- **Graphiti/Zep:** cosine similarity, Okapi BM25 and breadth-first graph search, merged by RRF, MMR or a
  cross-encoder. The default is semantic plus BM25 merged by RRF. The paper's only Cypher is predefined and used for
  graph construction (arXiv 2501.13956; getzep/graphiti `search_config.py`; Zep docs, P). The paper has no ablation
  of these components.
- **Mem0:** the 2025 paper retrieves by vector similarity, and its graph variant adds entity matching (arXiv
  2504.19413, P). The April 2026 algorithm fuses semantic, BM25 and entity matching in one pass, and the graph-store
  integration was removed from the open-source SDK (Mem0 README and migration docs, P/SR).
- **Graph vs vector in Mem0** (2504.19413, P/SR): the graph variant scores +1.6 overall (68.44 vs 66.88). It wins
  on temporal questions but loses single-hop (65.7 vs 67.1) and multi-hop (47.2 vs 51.2), and is about 3× slower at
  p95 (0.657 s vs 0.200 s).
- **Letta/MemGPT:** archival search is embedding-based with time and tag filters (P). LangMem: semantic or exact
  match over the LangGraph store (P). A-MEM: cosine top-10 over note embeddings (arXiv 2502.12110, P). MemOS: tag,
  time and Boolean filters, then embedding ranking (arXiv 2507.03724, P).
- **Cognee:** the only system where an LLM writes Cypher (`NATURAL_LANGUAGE` mode). The mode is disabled by default
  behind `ALLOW_CYPHER_QUERY` (Cognee docs, P).
- **Letta filesystem benchmark** (P/SR, single source): GPT-4o-mini with grep, semantic search and file open scored
  74.0 on LoCoMo. Letta concludes that the agent's own repeated queries matter more than the retrieval mechanism.
  This supports giving the agent better tool text and hints as well as better matching.

### 6. Scoring agent memory and setting a pass bar

Score what the agent achieves and what it leaves in the store, not only retrieval rank, and fix the bar before
running.

- **Answer scores overstate memory work.** MemoryArena found QA scores overstate task completion (P). FEB-30
  found scoring each memory operation differs from QA scoring by about 16 points (arXiv 2610.22209, P). BFCL v4's
  memory track lists premature deletes, key lookups that break on rephrasing, and generic answers; the best model
  scores about 64% (P).
- **Updates and abstention need their own cases.** LongMemEval covers knowledge updates and abstention, judged by
  GPT-4o with over 97% agreement with humans (P). MemoryAgentBench multi-hop consolidation is at or below 28% (P).
- **Save tasks are scored on the store's state.** WritePolicyBench scores writes deterministically (P).
- **Statistics.** Use paired differences with clustered standard errors and K repetitions per task (Miller 2024,
  arXiv 2411.00640, P). With 30–50 tasks, only effects of about 10 points or more are detectable.
- **Task set.** Anthropic's "Demystifying evals" recommends 20–50 tasks drawn from real failures, graded on
  outcomes, with pass^k (P). Fixing a non-inferiority margin in advance is our inference.

## Conflicts and uncertainties

- **Zep vs Mem0 on LoCoMo.** Mem0's paper scored Zep 65.99. Zep says Mem0 misconfigured it and reports 75.14. A
  secondary report says Mem0 replied with 58.44, and MemOS reports Zep 59.22 and Mem0 64.57. Every number is
  vendor-run, and a third-party audit says much of LoCoMo's answer key is wrong. Differences of a few points on
  LoCoMo are noise.
- **Mem0's 92.5 on LoCoMo** is self-reported on its managed platform, which has optimisations the SDK lacks.
- **Small stores.** Every retrieval result above comes from corpora of thousands to millions of passages. Whether
  hybrid search helps a store of about 100 facts, where keyword search already sees every fact, is unmeasured.
- **Cognee's docs** disagree with themselves on the default search mode.
- **Letta's** tool rules forced semantic search first, so its "grep is enough" result is not a pure grep test.

## Gaps — what we did not find

- Embedding latency on CPU for nomic-style models. One tertiary source gives about 10 ms on GPU.
- Any measurement of "did you mean" hints on empty results.
- A neutral rerun of any agent-memory benchmark.
- An isolated ablation of graph links in A-MEM, or of any retrieval component in Graphiti or MemOS.
- Letta's source for archival search: the expected file paths returned 404.

## Source quality assessment

Most load-bearing claims rest on primary sources: peer-reviewed or arXiv papers, official docs, and source code.
The vendor comparisons (Zep, Mem0, MemOS, Letta) are primary but self-reported, and they conflict. The "no-match
cutoff" AUC and GPU latency are tertiary and are not relied on. The recommendation does not depend on any single
vendor number. It rests on the BM25-vs-dense, complementarity and proposition results, and on the convergence of
production systems.

## Sources

**Primary**
- Sciavolino et al., Simple Entity-Centric Questions Challenge Dense Retrievers, arXiv 2109.08535
- Thakur et al., BEIR, arXiv 2104.08663
- ACL 2023 Findings paper 208 (hybrid ablation)
- Chen et al., Dense X Retrieval, arXiv 2312.06648
- Wu et al., LongMemEval, arXiv 2410.10813
- arXiv 2503.05037 (dense retriever biases); arXiv 2509.05691 (numbers in embeddings)
- Gao et al., HyDE, arXiv 2212.10496; Wang et al., query2doc, arXiv 2303.07678
- Bruch et al., An Analysis of Fusion Functions for Hybrid Retrieval, arXiv 2210.11934
- GuidedRAG, arXiv 2607.26071
- CypherBench; Apache AGE release notes; CVE-2022-45786
- PostgreSQL docs: text search ranking, pg_trgm
- Anthropic, Writing effective tools for agents; Anthropic memory tool docs; Anthropic, Demystifying evals
- OpenAI function calling guide
- arXiv 2605.24660 (adaptive tool shortlists)
- LangMem tool reference; Letta passages search API; Cognee search docs
- Rasmussen et al., Zep/Graphiti, arXiv 2501.13956; github.com/getzep/graphiti; help.getzep.com
- Chhikara et al., Mem0, arXiv 2504.19413; Mem0 README and OSS v2→v3 migration notes
- Xu et al., A-MEM, arXiv 2502.12110; MemOS, arXiv 2507.03724
- MemoryArena; MemoryAgentBench; BFCL v4 memory; FEB-30, arXiv 2610.22209; WritePolicyBench
- Miller, Adding Error Bars to Evals, arXiv 2411.00640

**Primary, self-reported by a vendor**
- getzep.com blog, "Lies, damn lies, statistics: is Mem0 really SOTA"
- letta.com blog, "Benchmarking AI agent memory"

**Secondary and tertiary**
- Reports of Mem0's GitHub reply to Zep (58.44)
- LoCoMo audit (dev.to/penfieldlabs; github.com/dial481/locomo-audit)
- Ollama embedding latency and cosine-AUC blog figures
