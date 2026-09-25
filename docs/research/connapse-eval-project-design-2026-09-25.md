# How should Connapse build an in-repo evaluation project?
**Date:** 2026-09-25
**Status:** Reviewed
**Built on:** retrieval-eval-github-sources-2026-09-24.md, github-docs-issues-chunking-2026-09-23.md, issue #523, the existing `tests/Connapse.Eval` scaffold

## Executive summary
Connapse should turn `tests/Connapse.Eval` into an xUnit project that drives the real `IKnowledgeSearch` through the existing `SharedWebAppFixture`. It should compute ranking metrics in its own C# code, checked against trec_eval (the reference scorer from NIST's Text REtrieval Conference), and use Microsoft.Extensions.AI.Evaluation 10.10 only for reporting, caching and any later LLM-judged checks. No .NET library computes ranking metrics such as MRR or nDCG, and MEAI.Eval has none. The datasets should be a Connapse-built golden set (hardened against the "model-written questions are too easy" problem), a synthetic permission set, and a small tier of redistributable public sets: NanoBEIR, CQADupStack, MultiHop-RAG, a ViDoRe v3 slice and olmOCR-bench. The prior eval used bootstrap p-values to decide whether a change wins. Decisions should instead use a paired t-test or permutation test with Holm correction, and it had too few queries: 40 held-out queries can only detect MRR differences of about 0.13. The largest open question is what can run on every PR, because the embedding models need a GPU and CI has none. Load and performance testing belongs alongside the quality eval as separate suites: BenchmarkDotNet for micro-benchmarks, k6 for HTTP/REST/MCP load, and an xUnit vector-index harness that measures recall and latency together. The research also found that Connapse never sets `ivfflat.probes`, so filtered vector search may be losing recall today.

## Research brief
**Question:** How should an evaluation project for Connapse be designed — .NET engineering, RAG-retrieval methodology, the metrics and algorithms to implement, and the datasets to use?

**Sub-questions:**
1. What is current .NET practice for AI/search evaluation harnesses (packages, project type, reproducibility, CI)?
2. What methodology should a retrieval-only enterprise RAG platform follow (test-collection building, judges, statistics, permission and agent-facing concerns)?
3. Which metrics and statistical algorithms are needed, with implementable definitions and edge-case conventions?
4. Which datasets (public and self-built) fit, with sizes and licenses?

5. (Added after the first pass) How should Connapse do load and performance testing — tools, scenarios, statistics, CI placement?

**Out of scope:** building a chatbot or answer-generation eval (Connapse has no answer UI); choosing embedding or reranker models (settled in the 2026-09-24 report).

**Success criteria:** a design-ready recommendation for project shape, metric set, statistical procedure and a tiered dataset plan, with sources and flagged uncertainty.

## Findings by sub-question

### 1. .NET engineering for the Connapse eval harness
The Connapse eval should be an xUnit project that calls the shipped search code in-process, with MEAI.Eval used for reporting only.
- **Microsoft.Extensions.AI.Evaluation (MEAI.Eval) is stable, but it can't score ranking.** The package family is at 10.10.0 (2026-09-09) [primary]. Its Quality package has LLM-judged evaluators, and the one closest to ranking, `RetrievalEvaluator`, is an LLM giving a 1–5 score. Microsoft's own docs warn it "can be especially poor when a smaller / local model is used" [primary]. It accepts any `IChatClient`, so Ollama works mechanically. What Connapse should take from it: disk-based reporting with response caching (`DiskBasedReportingConfiguration`, `executionName` = git SHA + config label), the `dotnet aieval report` HTML/JSON output, and custom `IEvaluator`s that return `NumericMetric`, so C#-computed MRR/nDCG appear in the same report [primary].
- **Microsoft's samples use a test project, not a console app.** The dotnet/ai-samples evaluation samples are unit-test projects, and eShopSupport moved from a console "Evaluator" to an xUnit EvaluationTests project [primary]. Connapse's existing console scaffold holds only `SummaryJudge`, so converting it costs little.
- **Drive the product code path.** The 2026-09-24 eval's biggest methodological flaw was an approximated Python search. `WebApplicationFactory` gives the harness the real DI container, and `ConfigureTestServices` can flip fusion, reranker or chunking options per configuration [primary]. The seam is `IKnowledgeSearch.SearchAsync(query, SearchOptions)`, and each `SearchHit` already carries `path` metadata, so relevance labels can key on `container + path` rather than database GUIDs that change on re-ingest (verified in `src/Connapse.Search/Keyword/KeywordSearchService.cs`). Chunks carry `StartOffset`/`EndOffset` (`ChunkInfo`), so span-level chunking metrics need no product change.
- **Reproducibility has to be built by hand.** No .NET guidance exists for dataset pinning, run manifests or TREC-format files [gap]. The run manifest should record the git SHA, a config hash, embedding and reranker model IDs, and dataset SHA-256s. Each run should also emit a per-query JSONL file and a TREC run file. Embeddings can be cached with `DistributedCachingEmbeddingGenerator` over a disk cache [primary].
- **CI.** Microsoft advises against failing builds on individual scores; the advice is to fail only on a significant regression across many cases [primary]. Testcontainers `WithReuse` is experimental and gives the container a new host port each run, so use it only locally [primary].

### 2. Retrieval-evaluation methodology for Connapse
A retrieval-only product like Connapse should measure each layer through its own code, and treat the permission check as a separate pass/fail gate.
- **Model-written questions are easier than real ones, and this is measured.** The CIKM '26 "curse of knowledge" paper found that questions an LLM writes after reading the answer leak concepts only a reader would know: 7.4% of concepts, appearing in 97 of 100 topics [primary]. This is the documented cause of the 2026-09-24 eval's inflated absolute scores. The mitigations are to write questions from a short backstory rather than the whole document, to add human-written questions, and to add real issue titles whose relevance labels come from linked duplicates or fixes. Fully synthetic collections rank systems similarly to human ones, but can favour systems built with the same model family [primary].
- **LLM judges are usable if calibrated.** At TREC RAG 2024, UMBRELA (an open-source LLM relevance judge) produced system rankings that correlated highly with manual judgments across 77 runs [primary]. LLM judges also over-label passages as relevant and can be fooled by keyword stuffing and by instructions planted in the text [primary]. That matters here because GitHub issues are user-authored. Use them to judge pooled results (the combined top-k from every configuration), check about 10% against human labels, and never judge with the same model family that wrote the questions.
- **Incomplete judgments.** Score after removing unjudged documents from each ranked list ("condensed lists"). Sakai showed this is at least as robust as bpref, a metric designed for incomplete labels [primary]. Report judged@k for every run.
- **Sample size.** Even 50 topics can yield significant results that point the wrong way [primary]. Sakai's topic-set-size design typically lands near 100 queries per comparison [primary]. The 2026-09-24 eval's 40 held-out queries per source were underpowered for small effects. Its reported wins (+13.9, +19.9 MRR) exceed the roughly 13-point detectable difference, so they stand, but the 1–3-point per-source content effects could never have been resolved.
- **Evaluating each layer.** Anthropic's contextual-retrieval post reports the top-20 retrieval failure rate after adding each layer (5.7% → 3.7% → 2.9% → 1.9%), a good template for Connapse's keyword/vector/fusion/rerank stack [secondary, vendor data]. Relevance labels correlate only weakly with how well an LLM answers from the retrieved text. eRAG, which scores the LLM's output per retrieved document, correlates better with end-to-end quality [primary], so a small downstream check is worth adding later.
- **Permission-aware retrieval.** No academic benchmark exists [gap]. One tertiary, single-source demo shows removing the permission check moved leakage from 0% to 81.8% while recall stayed at 100% [tertiary]. The point holds regardless of the source: quality metrics cannot detect a permission bug, so leakage needs its own suite.
- **Agent-facing and online signals.** BrowseComp-Plus fixes the corpus so the retriever's effect on an agent can be isolated [primary]. When there are no clicks, useful proxies to log are re-query rate within a session, which ranks the agent actually fetches, zero-result rate, and searches per task [primary, but no validation study].

### 3. Metrics and statistical algorithms for Connapse's eval
Every metric Connapse needs is a few lines of C#, but the edge-case conventions must be chosen deliberately and checked against trec_eval.

| Metric | Definition | LLM? | Use in Connapse |
|---|---|---|---|
| MRR@10 | 1 / rank of first relevant doc (0 if none) | No | Lead for single-answer questions |
| hit@1, hit@3 | any relevant in top k | No | Sanity check; drop hit@10 (saturated in 2026-09 evals) |
| nDCG@10 | Σ g(rel)/log₂(i+1), IDCG from the **qrels**, linear gain | No | Lead for graded, multi-relevant queries |
| Recall@5, @10 | relevant in top k / all relevant | No | What an agent reading top-k actually gets |
| Judged@10 | share of top 10 with any judgment | No | Always reported alongside quality metrics |
| AP′ / nDCG′ | metric after removing unjudged documents | No | v2, when pooling leaves gaps |
| Span recall / precision / IoU | character-interval overlap of gold excerpt vs retrieved chunks (Chroma method) | No | v2; the only fair way to compare chunkers |
| Leak count | unauthorized results per query | No | Must be exactly 0; a test assertion |
| Claim recall / nugget Vital-Strict | gold claims covered by retrieved chunks | Yes | v2; container summaries and downstream checks |

Sources: trec_eval source, ranx docs, Chroma "Evaluating Chunking", RAGChecker, TREC RAG 2024 nugget paper [all primary].

**Edge-case conventions (the tools disagree):**
- **Ties.** trec_eval breaks ties by docid in descending order and ignores the rank column. Replicate this, because min-max score blending produces ties.
- **Duplicate docids.** trec_eval errors. The harness should fail too, which also catches bugs in collapsing chunks to documents.
- **Queries with no relevant documents.** trec_eval skips them; ranx scores them 0. Exclude them from quality metrics and score them separately as "should return nothing" queries.
- **Empty runs.** Score them 0 (trec_eval's `-c` behaviour).
- **Chunks to documents.** Take each document's best-ranked chunk, then deduplicate.
- **Chunk-level labels.** Never key labels by chunk ID. IDs change whenever the chunking strategy changes, so key by character spans instead.

**Statistics:**
- **Decision test.** Urbano et al. analysed more than 500 million p-values [primary]. They recommend the paired t-test or permutation test and found the bootstrap-shift test biased toward small p-values when there are few queries. Use a paired t-test plus a sign-flip permutation test (B ≥ 10,000; p = (count + 1)/(B + 1)).
- **Confidence intervals.** Use a paired bootstrap on per-query differences, BCa when n < 100.
- **Multiple comparisons.** Across configuration grids, use Holm correction for "X beats baseline" claims and Benjamini–Hochberg for exploratory sweeps.
- **Detectable effect.** Print the minimum detectable effect in every report: MDE ≈ (z₀.₉₇₅ + z₀.₈)·σ_d/√n. With σ_d ≈ 0.3, n = 40 gives about 0.13 MRR and n = 200 about 0.06.
- **Judge agreement.** Weighted Cohen's κ against human labels, and Kendall's τ for agreement on system rankings (τ ≥ 0.9 is the conventional "equivalent" bar, cited from memory).
- **Latency.** p50/p95 per stage (embed, keyword, vector, rerank), with warm-up runs discarded.

**Validation:** no .NET IR-metrics library was found (a search-based check, not a full NuGet crawl). Commit fixture runs covering ties, duplicates, empty runs, queries with no relevant documents, graded labels and k greater than the run length. Assert that the C# output matches `trec_eval -c` or pytrec_eval to 1e-9. Python stays a test oracle, not a runtime dependency.

### 4. Datasets for Connapse's eval
Connapse's own golden set should carry most of the weight. Public sets add breadth, and the permission set must be synthesized.

| Dataset | Tests | Size | License | Tier |
|---|---|---|---|---|
| Connapse golden set (docs + issues, hardened) | Real product content | 87 docs, 506 issues; target ≥ 200 queries | Repo's own | CI slice + nightly |
| Synthetic permission set over the Connapse corpus | Leak rate, permission-trimmed recall | Same corpus, synthetic users and grants | Repo's own | CI |
| NanoBEIR (SciFact, FiQA, ArguAna; all 13 nightly) | General retrieval regression | ≤ 10K docs, 50 queries each | Card says CC-BY-4.0; upstream varies — **unclear** | CI / nightly |
| NFCorpus | Graded relevance (0–2) | 3.6K docs, 323 queries | Check upstream | Nightly |
| CQADupStack (programmers) | Duplicate-question retrieval, closest to issue dedup | 32K docs, 876 queries | StackExchange CC-BY-SA upstream (mirror says Apache-2.0) — **flag** | Nightly |
| MultiHop-RAG | Multi-document evidence | 609 articles, 2,556 queries | ODC-BY | Nightly |
| RAGBench TechQA + EManual | Technical-manual retrieval, grounding keys | ~1.3K+ queries | CC-BY-4.0 | Nightly |
| ViDoRe v3 (computer science) | PDF page retrieval through Connapse's parser | ≤ 16.7K pages | CC-BY-4.0 | Nightly |
| BRIGHT (StackExchange domains) | Reasoning-heavy retrieval | Small per domain | CC-BY-4.0 | Nightly |
| olmOCR-bench | PDF parsing unit tests (tables, reading order) | 1,403 PDFs, 7,010 tests | ODC-BY-1.0 | 20-PDF CI slice; full nightly |
| TREC-COVID, TREC DL 19/20, LoTTE, CoIR, SWE-bench Lite retrieval, FRAMES | Deep graded and code/issue checks | Large | Mixed; MS MARCO non-commercial | Pre-release |
| CRAG, FinanceBench, OmniDocBench | Web RAG, PDF finance QA, layout | — | **Non-commercial** | Pre-release, download only, never commit |

Sources: Hugging Face dataset cards and official repos [primary where marked verified by the researcher; BEIR sizes from the papers].

- **Building the permission set.** Assign synthetic users and groups plus per-document grants, including deny rules, nested groups and documents readable by nobody. Each user's relevance labels are the original relevant documents minus the ones they can't read. Include queries whose only relevant document is forbidden; the correct answer for those is empty. Commit the ACL files and the seed used to generate them.
- **Hardening the golden set.**
  - Keep the answer-quote check, the fixed-seed dev/held-out split and document-level scoring.
  - Add about 50 human-written or human-verified questions.
  - Add real issue titles, with linked duplicates or fixes as relevance labels.
  - Write synthetic questions from a backstory, not the whole document.
  - Grade labels 0–2 with an LLM judge from a different model family, spot-checked by a person.
  - Balance question types. "Know Your RAG" shows generators skew toward simple lookups.
- **Versioning.** Store queries as JSONL, relevance labels in TREC format (`qid 0 docid grade`), and a corpus manifest with SHA-256s and the Connapse commit it was exported from. Commit small sets. Download large or non-commercial sets on demand into a gitignored cache, pinned to a Hugging Face revision.

### 5. Load and performance testing for Connapse
Connapse's load and performance testing needs three tools for three jobs, and its vector-index benchmark must measure recall alongside latency, because pgvector's speed settings trade directly against result quality.
- **Possible recall gap found in the code.** Nothing in `src` sets `ivfflat.probes` (pgvector's default is 1 probe, meaning the query scans only one index partition) or iterative scans. `VectorColumnManager.cs:98` caps IVFFlat lists at 100, and `PgVectorStore.cs:295-306` applies the container, source and permission filters in the WHERE clause, with `ORDER BY distance LIMIT topK` [verified in code]. pgvector's README says filters are applied after the index scan: with 10% of rows matching, default settings return about 4 of 10 rows. pgvector 0.8 added `ivfflat.iterative_scan` and `max_probes` to fix this, and recommends probes ≈ √lists [primary]. **Unverified:** whether the planner uses the partial index at all for this query. If it doesn't, search is exact (recall fine, latency poor). The first benchmark scenario settles this.
- **Tools.**
  - **NBomber is unsuitable.** Its free tier is personal-use only, and organizations need a paid licence [primary].
  - **k6** is AGPL-3.0. It is fine as an external tool that is never linked into Connapse [primary]. Its arrival-rate executors produce an *open* workload (requests sent at a fixed rate whether or not the server keeps up), which avoids coordinated omission [primary]. Coordinated omission is the bias where a slow server throttles a closed-loop load generator and hides its own tail latency.
  - **k6 against the MCP endpoint.** Use a plain k6 script that sends `initialize` and keeps the `Mcp-Session-Id` header [secondary]. The xk6-mcp extension is experimental and unsupported [primary].
  - **BenchmarkDotNet** (MIT) for micro-benchmarks of fusion scoring, chunkers, tokenizers and the metric code [primary].
  - **Crank**, the ASP.NET team's tool, is too heavy for a one-maintainer repo [primary].
- **Vector benchmark method.** ANN-Benchmarks plots recall@k against queries/sec. VectorDBBench adds p99 latency, load time and filtered cases [primary]. Measure recall against exact search forced with `SET LOCAL enable_indexscan = off` [primary]. Sweep lists, probes and iterative scan at 10K, 100K and 1M chunks, with and without a 10%-selective filter.
- **Ingestion.** No authoritative benchmarking method was found [gap]. Proposed metrics: docs/sec, chunks/sec, embedding batch-size sweep, queue depth over time, time-to-searchable (upload to first search hit), and CPU, GPU, RAM and database size.
- **Regression detection.** Shared GitHub runners vary ±10–20% [primary], so only allocation counts and large relative timing changes are reliable there. Latency and throughput need a dedicated machine; the RTX 5070 Ti dev box can be a self-hosted runner. MongoDB found fixed-threshold perf alerts noisy and switched to change-point detection with the E-Divisive algorithm [primary]. Use Mann-Whitney U over at least 5 runs until nightly history exists, then change-point detection.
- **Blazor Server** circuits cost about 250 KB each, and Microsoft measured 5,000+ users on 1 vCPU [primary, single old measurement]. Skip UI load testing in v1.

**Proposed v1 scenarios (all targets are proposals, not sourced):**

| # | Scenario | Key metrics | Proposed target | Runs |
|---|---|---|---|---|
| 1 | Filtered vector search at 10K / 100K / 1M chunks | recall@10 vs exact; rows returned; latency | recall ≥ 0.95, exactly k rows | 10K per PR; 100K nightly; 1M pre-release |
| 2 | Hybrid search, no reranker, 20 req/s open load, 100K chunks | p50/p95/p99, error rate, CPU | p95 < 300 ms | Nightly |
| 3 | Hybrid + GPU reranker | p95; reranker-timeout rate | p95 < 800 ms; timeouts < 0.1% | Nightly |
| 4 | MCP search tool, 50 concurrent sessions (k6 sends scripted `tools/call` requests from a fixed query list; no LLM or agent involved, so no API cost) | p95, session errors | p95 < 500 ms, 0 errors | Nightly |
| 5 | Ingest 1,000 mixed documents | docs/sec, chunks/sec, p95 time-to-searchable | p95 < 60 s on GPU box | Nightly |
| 6 | 2-hour soak at 50% capacity | memory, DB connections | drift < 10% | Pre-release |
| 7 | 5× spike for 60 s | failures, p95 recovery | 0 failures; recovers in 2 min | Pre-release |
| 8 | Micro-benchmarks | allocations, median time | no allocation regression; < 25% slowdown | Per PR |

Sources: pgvector README https://github.com/pgvector/pgvector; NBomber licence https://nbomber.com/docs/getting-started/license/; k6 https://github.com/grafana/k6 and https://grafana.com/docs/k6/latest/using-k6/scenarios/concepts/open-vs-closed/; xk6-mcp https://github.com/grafana/xk6-mcp; Infobip MCP k6 post https://www.infobip.com/developers/blog/implementing-mcp-load-tests-with-grafana-k6; Crank https://github.com/dotnet/crank; BenchmarkDotNet https://github.com/dotnet/BenchmarkDotNet; ANN-Benchmarks https://arxiv.org/pdf/1807.05614; VectorDBBench https://github.com/zilliztech/vectordbbench; wrk2 https://github.com/giltene/wrk2; MongoDB change-point https://www.mongodb.com/blog/post/using-change-point-detection-find-performance-regressions; Hunter https://arxiv.org/pdf/2301.03034; github-action-benchmark https://github.com/benchmark-action/github-action-benchmark; Bencher https://bencher.dev/docs/explanation/thresholds/; Blazor hosting https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/server.

### 6. How teams organise evaluation datasets, and what Connapse should copy
The common industry pattern is a small, human-checked golden set built from real queries, versioned in git like code, grown by adding a case for every bug found, and pinned by version in every run manifest.
- **Set types.** Regression sets should stay near 100% and catch breakage. Capability sets start with low scores and measure improvement; cases that saturate "graduate" into regression [primary, Anthropic, single source]. Anthropic also says to test both where a behaviour should occur and where it shouldn't, because one-sided sets reward over-triggering [primary]; for retrieval that means a *no-answer* set of queries whose right result is empty (UAEval4RAG) [primary]. Holdout sets are never tuned against: Kaggle's gap between public and private leaderboards shows how quickly visible sets get overfit [secondary]. Connapse adds a fifth type: *permission* cases ("user X must not see document Y").
- **Sources.** Real usage first. Anthropic: "20–50 simple tasks drawn from real failures is a great start" [primary]. Vespa drew 386 queries from a month of site traffic [primary]. OpenAI warns against sets that don't match production traffic [primary]. Connapse has no production logs, since users self-host, but the maintainer's own Claude Code sessions query Connapse over MCP daily; logging those on the maintainer's instance is the closest real source.
- **Labelling.** Write a grading guide on a 0–3 scale (Quepid: 3 = "This is what I am looking for!") [primary]. Hand-label a sample, check an LLM's grades against it, then let the LLM grade the rest. Vespa hand-labelled 90 pairs in a few hours before handing about 10k to GPT-4 [primary]. Judge the pooled top 10 from several configurations, and re-judge when a new configuration surfaces unjudged documents [primary, NIST].
- **Versioning.** A dataset card per set (Gebru et al., "Datasheets for Datasets") [primary]; published versions are immutable; a manifest records a checksum for every file; every run records the dataset version, as MTEB, LangSmith, Braintrust and Phoenix all do [primary]. Semver for datasets is borrowed from MTEB practice, not a documented industry norm [gap].
- **Workflow for adding a case.** Observe a miss; write the query and a note of what the user wanted; pool the top 10 from each search mode; the LLM pre-grades and the maintainer confirms; add it to the capability or regression set with its origin (issue number); bump the version and commit with the fix. About 20% of new cases go to the holdout.

Sources: https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents; https://arxiv.org/pdf/2412.12300; https://www.kaggle.com/code/caseyftw/overfitting-the-leaderboard; https://developers.openai.com/api/docs/guides/evaluation-best-practices; https://blog.vespa.ai/improving-retrieval-with-llm-as-a-judge/; https://shopify.engineering/evaluating-search-algorithms; https://hamel.dev/blog/posts/evals-faq/; https://arxiv.org/abs/1803.09010; https://arxiv.org/html/2506.21182v1; https://docs.smith.langchain.com/evaluation/how_to_guides/version_datasets; https://www.braintrust.dev/docs/platform/datasets; https://www.elastic.co/search-labs/blog/judgment-lists; https://github.com/o19s/quepid/wiki/Judgement-Rating-Best-Practices; https://docs.opensearch.org/latest/search-plugins/search-relevance/using-search-relevance-workbench/; https://tsapps.nist.gov/publication/get_pdf.cfm?pub_id=51236

## Conflicts and uncertainties
- **What runs per PR.** The dataset researcher proposed a five-minute per-PR tier. The .NET researcher and Microsoft's guidance favour nightly or manual runs with regression-only gating. There are two further constraints: Connapse CI only runs on PRs to main, and it has no GPU for Ollama embeddings. My reconciliation: per PR, run only deterministic checks (the metric fixtures against trec_eval, the zero-leak permission suite, and keyword-only retrieval on the golden slice). Leave embedding-dependent quality to nightly runs on the dev box or a self-hosted runner. **This is the decision most worth your input.**
- **Bootstrap vs t-test.** The prior eval decided on bootstrap confidence intervals. Urbano et al. 2019 favour the t-test or permutation test, while Smucker et al. 2007 found little practical difference. Adopting t-test/permutation for decisions costs nothing and follows the larger study.
- **Whether LLM judges can replace humans.** TREC RAG 2024 found high correlation; Clarke & Dietz argue circularity and gaming remain. A reported "5th vs 28th" ranking inversion was seen only in a search snippet and is unverified.
- **BrowseComp-Plus at web scale.** A single, non-peer-reviewed August 2026 preprint reports evidence recall collapsing from 84% to 21% on a 553M-document corpus. It may partly reflect missing judgments.
- **Kernel Memory's C# RAGAS port.** Possibly archived; its status is unconfirmed.
- **Mirror licenses.** mteb/NanoBEIR mirrors relabel licenses (CQADupStack says Apache-2.0 on the mirror, but its StackExchange source is CC-BY-SA). Check upstream before committing any public data.

## Gaps — what we did not find
- No .NET IR-metrics library and no .NET port of trec_eval, ranx or pytrec_eval.
- No public permission-aware retrieval benchmark, and no peer-reviewed methodology for per-user recall.
- No quantitative method for evaluating parsing or ingestion alone beyond olmOCR-bench-style unit tests.
- No credible public intranet benchmark in the Glean or SharePoint style. "EnterpriseEM" and "ConfluenceQA" could not be confirmed to exist.
- No validated link between agent-behaviour proxies (re-query rate and similar) and retrieval quality.
- No GitHub Actions integration for the `dotnet aieval` report.
- No freshness/staleness standard. Proposed metrics (stale retrieval rate, catch-up latency) come from non-peer-reviewed sources.

## Source quality assessment
- **Methodology and statistics** rest mostly on primary peer-reviewed IR work: TREC overviews, SIGIR, CIKM, TOIS, and Urbano et al.
- **The .NET tooling section** rests on primary Microsoft docs and source code.
- **Dataset facts** come from Hugging Face cards and official repos. Some BEIR sizes are from the papers and were not re-fetched.
- **Weaker areas:** the permission-leakage evidence is tertiary and single-source, the freshness metrics are secondary or tertiary, and the Anthropic layer numbers are vendor data.
- **Carried-forward findings:** those from the 2026-09-24 report are Connapse's own measurements, with the limitations stated there.

## Sources
**Primary.**
- *Evaluation tooling:*
  - MEAI.Eval: https://learn.microsoft.com/en-us/dotnet/ai/evaluation/libraries, https://learn.microsoft.com/en-us/dotnet/ai/evaluation/evaluate-with-reporting, https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.evaluation.quality.retrievalevaluator, https://www.nuget.org/packages/Microsoft.Extensions.AI.Evaluation
  - dotnet/extensions `aieval` source: https://github.com/dotnet/extensions
  - Samples: https://github.com/dotnet/ai-samples, https://github.com/dotnet/eShopSupport
  - Testing infrastructure: https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests, https://dotnet.testcontainers.org/api/resource_reuse/
- *Metric references:* trec_eval source (m_ndcg.c, m_bpref.c, form_res_rels.c): https://github.com/usnistgov/trec_eval; ranx: https://amenra.github.io/ranx/metrics/; ir_measures: https://ir-measur.es
- *Statistics and test collections:*
  - Urbano et al. 2019: https://arxiv.org/abs/1905.11096
  - Smucker et al. 2007: https://dl.acm.org/doi/10.1145/1321440.1321528
  - Sakai 2007 (condensed lists): https://dl.acm.org/doi/abs/10.1145/1277741.1277756
  - Sakai 2016: https://dl.acm.org/doi/10.1145/2911451.2911492
  - Voorhees & Buckley 2002: https://dl.acm.org/doi/10.1145/564376.564432
  - Carterette 2012 (multiple testing); Carterette 2025: https://arxiv.org/abs/2501.03930
- *LLM judges and synthetic questions:*
  - Thomas et al.: https://arxiv.org/abs/2309.10621
  - UMBRELA / TREC RAG 2024: https://arxiv.org/abs/2411.08275
  - Clarke & Dietz: https://arxiv.org/abs/2412.17156
  - LLM-judge biases: https://arxiv.org/abs/2501.17969
  - Synthetic test collections: https://arxiv.org/abs/2405.07767
  - Curse of knowledge: https://arxiv.org/abs/2608.25245
- *RAG and agent evaluation:*
  - eRAG: https://arxiv.org/abs/2404.13781
  - RAGChecker: https://arxiv.org/abs/2408.08067
  - ARES: https://arxiv.org/abs/2311.09476
  - TREC RAG nuggets: https://arxiv.org/abs/2411.09607
  - Chroma chunking: https://www.trychroma.com/research/evaluating-chunking
  - BrowseComp-Plus: https://github.com/texttron/BrowseComp-Plus
  - Web-scale preprint: https://arxiv.org/abs/2608.20317
  - Agent trajectories: https://arxiv.org/html/2604.00356
  - RAG security survey: https://arxiv.org/abs/2606.25533
- *Datasets:* NanoBEIR https://huggingface.co/datasets/zeta-alpha-ai/NanoSciFact, BEIR https://github.com/beir-cellar/beir, CQADupStack https://huggingface.co/datasets/mteb/cqadupstack-programmers, BRIGHT https://huggingface.co/datasets/xlangai/BRIGHT, MultiHop-RAG https://huggingface.co/datasets/yixuantt/MultiHopRAG, RAGBench https://huggingface.co/datasets/rungalileo/ragbench, ViDoRe v3 https://huggingface.co/datasets/vidore/vidore_v3_computer_science, olmOCR-bench https://huggingface.co/datasets/allenai/olmOCR-bench, FRAMES https://huggingface.co/datasets/google/frames-benchmark, CRAG https://github.com/facebookresearch/CRAG, FinanceBench https://huggingface.co/datasets/PatronusAI/financebench, OmniDocBench https://huggingface.co/datasets/opendatalab/OmniDocBench, LoTTE https://huggingface.co/datasets/colbertv2/lotte, CoIR https://huggingface.co/datasets/CoIR-Retrieval/cosqa, CodeRAG-Bench https://huggingface.co/datasets/code-rag-bench/github-repos

**Secondary.**
- Anthropic, Contextual Retrieval: https://www.anthropic.com/engineering/contextual-retrieval
- eShopSupport eval write-up: https://jasonhaley.com/2024/12/11/eshopsupport-evaluation-tests/
- devleader .NET RAG metrics: https://www.devleader.ca/2026/09/05/rag-evaluation-metrics-in-net-measure-retrieval-and-grounding
- Freshness metrics: https://arxiv.org/html/2603.10765v1

**Tertiary.** VaultRAG leak demo: https://github.com/Rudra-G-23/VaultRAG; freshness: https://zenodo.org/records/20710012
