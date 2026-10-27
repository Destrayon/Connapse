# How should Connapse parse code with tree-sitter into function passages and confidence-scored links?
**Date:** 2026-10-27
**Status:** Reviewed
**Built on:** no prior corpus material (the Connapse knowledge-base tools were not available in this session)

## Executive summary
Connapse should parse C#, TypeScript/TSX and Python with the TreeSitter.DotNet 1.3.0 NuGet package inside the existing parser host process, which already isolates native parsers under Landlock and can be killed on a hang or crash. Each function or method becomes one passage with a short header naming its file and enclosing type, each type gets a skeleton passage with member bodies elided, and over-long members split at line boundaries with the header repeated. Links are extracted by our own syntax walk, because the tags queries shipped with the grammars are incomplete (the TypeScript one has no calls or class declarations). No published tool assigns numeric confidence to name-resolved calls, so Connapse's confidence ladder is a stated design choice, recorded beside a resolution kind so it can be retuned. The main open risk is a heap-corruption report against TreeSitter.DotNet 1.3.0 on Windows, which the child-process sandbox contains but which a stress test should check.

## Research brief
**Question:** How should a .NET 10 server parse source code with tree-sitter (no compiling, no running code, no AI) into one passage per function or class plus defines/imports/calls/inherits links with a confidence, re-parsing only changed files, under 1 s per file and 2 minutes for a 2,000-file repo?

**Sub-questions:** (1) .NET bindings and native packaging; (2) how existing tools build symbols and score name-based links; (3) code chunking practice; (4) safety and speed of tree-sitter on untrusted input.

**Out of scope:** compiler-grade resolution (Roslyn, SCIP indexers, language servers), embeddings, search ranking.

**Success criteria:** a build recommendation with named sources.

## Findings by sub-question

### 1. .NET tree-sitter bindings
TreeSitter.DotNet 1.3.0 (Marius Greuel, MIT, released 2026-01-22, about 330k downloads) is the only maintained binding with queries, node traversal and prebuilt natives for win-x64, linux-x64 and linux-arm64 among others. It bundles grammars as submodules of the official repos: tree-sitter core 0.26.3, C# from a January 2026 commit, TypeScript and TSX from tree-sitter-typescript 0.23.2, Python 0.25.0. Natives are built in GitHub Actions on native runners, with no signing step found. Issue #11 reports heap corruption after about 45,000 parse cycles on Windows; it was closed without a visible fix. XbergIo.TreeSitterLanguagePack wraps the same binding. TreeSitter (profmagija), TreeSitter.Bindings, TreeSitterSharp and CycoDevTreeSitter are abandoned, low-activity, missing our grammars, or forks. Writing our own P/Invoke layer would mean building natives for three platforms ourselves, worth it only if the stress test fails. Confidence: high for package facts (nuget.org and the repo), low for the cause of issue #11.

### 2. Symbols and links in existing tools
The tags.scm convention (`@definition.*`, `@reference.call`) carries no scope or resolution data, and the upstream query files are incomplete: Python captures only classes, functions and calls; C# misses bare calls, constructors, properties, records and enums; TypeScript misses function and class declarations and calls. Aider's repo map links files by shared identifier and weights them (×0.1 for names defined more than 5 times, ×10 for long distinctive names), with no per-link confidence. GitHub's stack-graphs did precise per-file incremental resolution but was archived in September 2025. Sourcegraph's search-based navigation uses symbol search with imports as a filter and publishes no scores. code-graph-rag is the only project found with resolution tiers (`exact`, `overload`, `heuristic`, as labels, not numbers); its resolver tries scope, then the import map, then receiver types, then a name-suffix fallback, and on a file change it re-parses that file and its dependents. Conclusion for Connapse: write our own per-language walk, resolve in the order scope → imports → declared receiver types, and store a numeric confidence plus a resolution kind. Every number in the ladder is our own choice.

### 3. Chunking code for retrieval
The cAST paper (arXiv 2506.15655) is the one controlled study: tree-sitter chunks that merge small siblings and split oversized nodes beat fixed-size line chunks by 1.8–4.3 points of Recall@5 on RepoEval and lifted downstream pass@1 by 2.7–6 points. Splitting without merging small siblings was much worse (nDCG 53.5 vs 71.1). The best size was about 2,000–2,500 non-whitespace characters. cAST adds no path or class header and lists ancestor context as future work. Sweep and LlamaIndex's CodeSplitter cap chunks at 1,500 characters. No controlled study isolates the value of a path or qualified-name header for code, so the header is a hypothesis to test on the code-questions eval set. Recommended policy: one passage per function or method with its doc comment and attributes; a skeleton passage per type; one module passage for imports and top-level statements; a size cap near the configured chunk size, splitting oversized members at line boundaries with the header repeated.

### 4. Safety and speed on untrusted input
tree-sitter has no memory cap or file-size limit. Known failure modes are stack overflows on deeply nested input (fixed in core in October 2026, issue #5807), error-recovery blowups (issue #3538), and grammar-specific hangs in older TypeScript and Python grammars. The old timeout API is gone; cancellation now goes through a progress callback, which bindings may not expose. A stack overflow cannot be caught in-process, so the defence is the parser host process: the parent's existing deadline kills it, plus a file-size cap and skipping minified or generated files. Trees, queries and parsers must be disposed explicitly. Walk trees with a cursor and an explicit stack, never recursion. The only published throughput figure is about 10 MB/s for the Rust grammar; at even 1 MB/s a 2,000-file repo fits the 2-minute budget, so the risk is the slow tail, not the average.

## Conflicts and uncertainties
- Whether TreeSitter.DotNet 1.3.0 includes the core stack-overflow fix (#5807, October 2026) is unknown. Its core is 0.26.3 from January 2026, so it probably does not. The parser host's kill-on-deadline and a nesting cap in our own walk cover this.
- Predicate support in TreeSitter.DotNet queries rests on its README. Our design avoids depending on query predicates.
- Every confidence number is invented. The ladder is recorded with a resolution kind so the numbers can be retuned against the code-questions set.

## Gaps — what we did not find
- No throughput numbers for the C#, TypeScript or Python grammars, and no query-cost numbers.
- No controlled ablation of contextual headers on code retrieval.
- No source for how GitHub code navigation guards against pathological inputs.
- Sweep's and Continue.dev's chunking posts could not be fetched; claims about them come from search snippets.

## Source quality assessment
Most findings rest on primary sources: package repos, nuget.org, tree-sitter's api.h and issue tracker, the grammars' query files, Aider's and code-graph-rag's source, and the cAST paper. Cursor's chunking and other tools' safety guards come from secondary or remembered sources and are not relied on.

## Sources
**Primary**
- TreeSitter.DotNet: https://github.com/mariusgreuel/tree-sitter-dotnet-bindings, https://www.nuget.org/packages/TreeSitter.DotNet
- tree-sitter API header: https://github.com/tree-sitter/tree-sitter/blob/master/lib/include/tree_sitter/api.h
- tree-sitter issues: https://github.com/tree-sitter/tree-sitter/issues/5807, /3538, /4999, /6009
- Grammar tags queries: https://github.com/tree-sitter/tree-sitter-c-sharp/blob/master/queries/tags.scm, https://github.com/tree-sitter/tree-sitter-typescript/blob/master/queries/tags.scm, https://github.com/tree-sitter/tree-sitter-python/blob/master/queries/tags.scm
- Grammar issues: https://github.com/tree-sitter/tree-sitter-python/issues/207, https://github.com/tree-sitter/tree-sitter-typescript/issues/265
- cAST: https://arxiv.org/abs/2506.15655, https://github.com/yilinjz/astchunk
- Aider repo map: https://github.com/Aider-AI/aider/blob/main/aider/repomap.py
- code-graph-rag: https://github.com/vitali87/code-graph-rag
- stack-graphs: https://github.com/github/stack-graphs
- Sourcegraph search-based navigation: https://sourcegraph.com/docs/code-navigation/search-based-code-navigation

**Secondary**
- LlamaIndex CodeSplitter source; Supermemory code-chunk blog: https://supermemory.ai/blog/building-code-chunk-ast-aware-code-chunking/
