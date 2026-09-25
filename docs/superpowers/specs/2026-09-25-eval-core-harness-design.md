# Eval core harness — design

**Date:** 2026-09-25
**Issue:** #523
**Status:** Approved in brainstorming, awaiting spec review
**Research:** `docs/research/connapse-eval-project-design-2026-09-25.md`

## Purpose

Connapse needs a repeatable way to tell whether a change to ingestion, chunking or search makes retrieval better or worse, across the kinds of content it is meant to serve. Connapse has no single target customer, so the harness measures breadth: one score per dataset, rolled up per domain and across the portfolio. It must also accept search systems that do not exist yet (graph RAG, image RAG, query rewriting, an agent driving Connapse over MCP) and score them on the same datasets.

The harness is built from scratch. No code, questions, corpora or results from earlier eval work are reused, and numbers from the 2026-09-24 eval are hypotheses to re-measure, not baselines.

This spec covers sub-project 1 of 7. The others each get their own spec:

| # | Sub-project | Depends on |
|---|---|---|
| 1 | **Core harness** (this spec) | none |
| 2 | Dataset tooling: `claude -p` / `codex exec` adapter, grading of pooled results, hand-built sets | 1 |
| 3 | Permission leak suite: synthetic users and grants, zero-leak gate | 1 |
| 4 | Vector index benchmark: recall vs exact search over probes, lists, iterative scan | 1 |
| 5 | More public dataset adapters (MultiHop-RAG, ViDoRe, BRIGHT, …) | 1 |
| 6 | Load tests (k6) and micro-benchmarks (BenchmarkDotNet) | none |
| 7 | Parser eval (olmOCR-bench), CI scheduling, self-hosted runner | 1–6 |

## Architecture

Two projects plus a data folder. The existing `tests/Connapse.Eval` scaffold (`SummaryJudge`, `Corpora/`, README) is deleted and replaced.

```
tests/Connapse.Eval/             console runner (net10.0 Exe)
  Datasets/     IDatasetAdapter implementations → EvalDataset
  Systems/      ISystemUnderTest implementations (ConnapseSearchSystem)
  Metrics/      ranking metrics, pure functions
  Statistics/   paired tests, bootstrap, multiple-comparison correction, MDE
  Runs/         runner, run folder writer/reader, manifest
  Reports/      HTML + JSON report, comparison report
  Cli/          command parsing (run, compare, report, pool, datasets)
tests/Connapse.Eval.Tests/       xUnit, [Trait("Category", "Unit")]
  metric fixtures checked against trec_eval output, statistics tests, adapter tests
eval/                            committed
  MANIFEST.json                  suites, datasets, pinned versions, checksums
  datasets/<name>/card.md        dataset card per dataset
  sets/                          Connapse's own hand-built sets (empty in v1)
  .cache/                        gitignored: downloads, embedding cache
  runs/                          gitignored: run folders
```

Dependencies go one way. `Metrics/` and `Statistics/` depend on nothing but the BCL. `Reports/` depends on them. `Runs/` depends on `Datasets/` and `Systems/`. Only `Systems/` references Connapse projects (Web, for the in-process host).

### Contracts

```csharp
public interface IDatasetAdapter
{
    string Name { get; }                                  // "nanobeir-scifact"
    Task<EvalDataset> LoadAsync(DatasetEntry entry, CacheDir cache, CancellationToken ct);
}

public sealed record EvalDataset(
    string Name, string Version, IReadOnlyList<string> Tags,        // e.g. "domain:engineering"
    IReadOnlyList<EvalDocument> Corpus,
    IReadOnlyList<EvalQuery> Queries,
    Qrels Qrels);

public sealed record EvalDocument(string Id, DocumentKind Kind, string? Title,
    string? Text, string? ImagePath, IReadOnlyDictionary<string, string> Metadata);
public enum DocumentKind { Text, Image }

public sealed record EvalQuery(string Id, string Text, Split Split, IReadOnlyList<string> Tags);
public enum Split { Dev, Test }

// One instance per (system, config): settings are applied when the system starts.
public interface ISystemUnderTest : IAsyncDisposable
{
    string Name { get; }                                  // "connapse"
    IReadOnlyDictionary<string, string> Describe();       // effective model IDs and settings, for the manifest
    Task<IndexReport> IndexAsync(EvalDataset dataset, CancellationToken ct);
    Task<SearchOutcome> SearchAsync(string dataset, EvalQuery query, int k, CancellationToken ct);
}

public sealed record IndexReport(int Documents, int Failed, IReadOnlyList<string> FailedDocumentIds);
public sealed record SearchOutcome(IReadOnlyList<RankedDoc> Ranked, Trace Trace, string? Error);
public sealed record RankedDoc(string DocId, double Score);
public sealed record Trace(TimeSpan Total, IReadOnlyDictionary<string, TimeSpan> Stages,
    int? ToolCalls, int? InputTokens, int? OutputTokens);
```

`Trace` carries tool calls and tokens from day one so an agent system can report its cost without a contract change. `DocumentKind.Image` exists from day one; v1 adapters emit only `Text`, and `ConnapseSearchSystem` fails fast on `Image` documents.

`SystemConfig` is a named set of Connapse settings overrides (search mode, fusion alpha, reranker on/off, chunking strategy, embedding model), loaded from `eval/systems/<system>/<config>.json`. Its hash goes into the run manifest.

## Data flow

1. **Resolve.** Read `eval/MANIFEST.json`, select the suite, download missing files into `eval/.cache/`, verify SHA-256 for every file. Any mismatch stops the run with the file named.
2. **Adapt.** The adapter produces an `EvalDataset`. Splits come from the source. Sources that ship only a test split (NanoBEIR, CQADupStack) are entirely `Test`: they measure, and nothing is tuned against them. RAGBench's validation questions are `Dev` and its test questions `Test`. Tuning uses dev splits only. A stable hash-based dev/test assigner arrives with the first hand-built set (sub-project 2), since no v1 source needs one.
3. **Index.** `ConnapseSearchSystem` starts Connapse in-process with `WebApplicationFactory` on a Testcontainers PostgreSQL (pgvector) and MinIO, the same way `SharedWebAppFixture` does, applies the config's settings overrides, creates one container per dataset, and uploads every document through `IUploadService.BulkUploadAsync` as a numbered `.txt` file, keeping a map from the Connapse document ID in each `UploadResult` to the dataset document ID. It waits until every document is Ready or Failed (`ContainerStats`). Embedding calls go through a disk cache keyed by (provider, model, SHA-256 of the input text).
4. **Search.** For each query, call `IKnowledgeSearch.SearchAsync` scoped to the dataset's container with `TopK` large enough to return k distinct documents after collapsing (start at 3 × k chunks; widen once to 10 × k if fewer than k distinct documents come back). Collapse chunk hits to documents by best rank and map each hit's `DocumentId` back to the dataset document ID. Record total latency; per-stage latency is recorded only when a system reports stages, and `ConnapseSearchSystem` reports total only in v1 because Connapse does not expose stage timings.
5. **Record.** Write `eval/runs/{utc-timestamp}-{gitsha7}-{suite}-{system}-{config}/`:
   - `manifest.json`: git SHA and dirty flag, suite, system, config name and hash, the system's effective settings (embedding and reranker model IDs among them), dataset names, versions and checksums, machine name, OS, processor count, start and end times, failed-document counts.
   - `results/{dataset}.jsonl`: one line per query: dataset, query ID, query text, split, ranked documents with scores, trace, error. A `results/{dataset}.done` marker is written last, so resume skips only datasets that finished.
   - `qrels/{dataset}.tsv` and `titles/{dataset}.json`: copies that make the run folder self-contained for `compare` and `report`.
   - `trec/{dataset}.trec`: TREC run file per dataset, for cross-checking with external tools.
6. **Score and report.** Compute metrics per query, aggregate per dataset (test split for headline numbers), per domain tag, and across the portfolio. Write `report.html` and `report.json` into the run folder.

## Metrics

Per query, document level, k = 10 unless stated: MRR@10, nDCG@10, Recall@5, Recall@10, P@5, hit@1, hit@3, judged@10. Latency p50 and p95 per stage and total, per dataset.

- **nDCG** uses linear gain (g = grade) and log2(rank + 1) discount; the ideal DCG sorts the qrels, not the run, truncated at k.
- **Relevant** means grade ≥ 1.
- **Ties** in score are broken by document ID descending (trec_eval's rule).
- **Duplicate document IDs** in one query's ranked list are an error; the run fails.
- **Queries with no relevant document** in the qrels are excluded from quality averages and reported separately as a no-answer count.
- **Empty or errored results** score 0 on every quality metric.
- **judged@10** is the share of the top 10 that has any qrel for that query, reported next to every quality number.

**Validation.** `Connapse.Eval.Tests` holds committed fixture qrels and runs covering ties, duplicate IDs, empty runs, no-relevant queries, graded labels (0–3) and k larger than the run length. The expected values were produced once by `trec_eval -c -m all_trec` and are committed next to the fixtures along with the command that generated them. Tests assert the C# values match to 1e-9. Python and trec_eval are needed only to regenerate the expected values.

## Statistics

`compare <runA> <runB>` pairs queries by (dataset, query ID) on the test split and, per dataset and metric, reports:

- mean difference B − A with a 95% BCa bootstrap interval (10,000 resamples, fixed seed);
- p-values from a paired t-test and a paired sign-flip permutation test (10,000 resamples; p = (count + 1) / (B + 1));
- Holm-corrected p-values across all (dataset × metric) pairs in the comparison;
- effect size d_z = mean difference / SD of differences;
- minimum detectable effect at α = 0.05 and 80% power for that query count.

A dataset's difference is shown as significant only when its Holm-corrected permutation p < 0.05. `compare` refuses runs with different dataset versions or checksums unless `--allow-dataset-mismatch` is passed, and then prints a warning banner in the report.

**Aggregation.** The portfolio score for a metric is the unweighted mean over datasets. Domain scores are the unweighted mean over datasets tagged with that domain. In a comparison, the verdict line reads "improves" only if the portfolio nDCG@10 rises and no dataset shows a significant drop on any quality metric; otherwise it names the datasets that dropped.

**Tests.** Statistics code is tested against known cases: a zero-difference sample gives p ≈ 1 and an interval containing 0; a constant positive shift gives p < 0.001; the t-test matches a hand-computed value; BCa matches a reference value computed once with SciPy and committed.

## Report

One self-contained HTML file (inline CSS and SVG, no external requests) plus `report.json` with the same numbers.

- **Header:** run metadata from the manifest; invalid datasets and error counts in a banner.
- **Portfolio table:** dataset rows × metric columns, test split, with judged@10; domain rollups and the portfolio row at the bottom.
- **Comparison view** (from `compare`): differences with intervals; cells coloured only when significant after correction; MDE per dataset; the verdict line.
- **Failure browser:** per dataset, the 20 worst queries by nDCG@10, each with the query text, the relevant documents and their ranks, the returned top 10 with titles, and the trace.

## CLI

```
run      --suite <name> --system <name> --config <name> [--datasets a,b] [--resume <runDir>] [--limit-queries N]
compare  <runDirA> <runDirB> [--allow-dataset-mismatch]
report   <runDir>
pool     <runDir> [<runDir> …] --out <file>     lists unjudged top-10 documents per query (grading is sub-project 2)
datasets list | verify | fetch | pin --suite <name>   pin records SHA-256 for files the manifest has not pinned yet
```

`run` prints the run folder path and a one-screen summary. Exit code is non-zero on any hard failure (checksum, duplicate IDs, invalid dataset).

## Failure handling

- **Checksum mismatch or missing file:** stop before indexing, name the file.
- **Document ingestion failures:** counted and listed in the manifest. More than 1% failed in a dataset marks that dataset invalid in the report; it is not scored.
- **Query error or timeout** (default 30 s per query): recorded with the error text, scored as empty. Error counts appear in the report header.
- **Crash or cancel:** each (dataset × system × config) unit writes its results on completion; `--resume` skips completed units. The embedding cache avoids re-embedding.
- **Container startup failure** (Docker missing): stop with a message saying Docker is required.

## Version 1 suite

Defined in `eval/MANIFEST.json` as suite `v1`. Public data is downloaded at runtime from pinned Hugging Face revisions and never committed; only manifest entries and dataset cards are.

| Domain tag | Dataset | Size | Labels |
|---|---|---|---|
| `domain:general` (plus `science`, `finance`, `argument` sub-tags) | NanoBEIR, all 13 subsets | ≤ 10K docs, 50 queries each | Human, mostly binary |
| `domain:engineering` | CQADupStack programmers | ~32K posts, 876 queries | Human, binary |
| `domain:business-docs` | RAGBench TechQA, RAGBench EManual | ~1.3K+ queries | **LLM-annotated** relevant-sentence keys → document qrels |

The RAGBench adapter derives document-level qrels from its relevant-sentence keys (key `3b` means sentence b of document 3). Its corpus is the union of documents across train, validation and test, so train documents act as distractors. RAGBench's relevance keys were produced by an LLM annotator, not people; its dataset card says so, and its scores should be read with that in mind. Licences are checked at the upstream source and recorded in each card before the dataset is added; the mteb/NanoBEIR mirrors' licence labels are not trusted on their own.

## Out of scope for version 1

Permission suite, LLM grading and generation tooling, vector-index benchmark, load tests and micro-benchmarks, parser evaluation, CI scheduling, graph/image/agent systems, Microsoft.Extensions.AI.Evaluation. Only the xUnit project (`Connapse.Eval.Tests`, Category Unit) runs in per-PR CI.

## Done when

1. `dotnet test --filter "Category=Unit"` passes, including the trec_eval fixture tests and statistics tests.
2. `dotnet run --project tests/Connapse.Eval -- run --suite v1 --system connapse --config hybrid` completes on the reference machine (the maintainer's RTX 5070 Ti box with Ollama) and writes a run folder with a report.
3. A `keyword` config run completes, and `compare` between the two produces a comparison report with intervals, corrected p-values, MDE and a verdict line.
4. Every dataset in `v1` has a dataset card with source, upstream licence, pinned revision, size, label type, split rule and known limitations.
