# CON-142 results — file search plus RAG for agents

Run on 2026-10-28 against Connapse at commit `950c61fa`. Pass bar: `pass-bar.md`, committed before any scored run.

## Recommendation: drop the combined feature, keep raw code files searchable

The combined setup ("both") failed the pre-set bar, so it is **dropped** as a combined feature. Files-only answered the
most questions, so "both" had no room to beat it by 10 points. The code check (CON-28) points the other way: agents
answered code questions 17 points better from the original files than from RAG, so raw code should stay searchable
next to the GraphRAG graph.

## Scores (185-file corpus, 48 questions, one attempt each)

| Setup | Correct | Exact | Concept | Code | Docs | Cost per question | Tool calls per question |
|---|---|---|---|---|---|---|---|
| rag | 90% (43/48) | 100% (24/24) | 79% (19/24) | 79% (19/24) | 100% (24/24) | $0.0493 | 1.8 |
| files | **96% (46/48)** | 100% (24/24) | 92% (22/24) | **96% (23/24)** | 96% (23/24) | **$0.0238** | 2.4 |
| both | 94% (45/48) | 100% (24/24) | 88% (21/24) | 88% (21/24) | 100% (24/24) | $0.0332 | 2.1 |

Agent: Sonnet 5.5 through `claude -p`; judge: a separate Sonnet 5.5 call that does not see the setup. Per-question
answers and verdicts are in `results/small-corpus/judged-*.jsonl`.

## (a) The pre-set verdict

| Bar | Needed | Got | Result |
|---|---|---|---|
| 1. Accuracy | both ≥ best single + 10 points (files, 96%) | 94%, 2 points **below** files | fail |
| 2. Cost | both ≤ files' cost + 20% ($0.0286) | $0.0332, 39% above | fail |
| 3. RAG on concept questions | `search_knowledge` on 13+ of 24 | 24 of 24 | pass |

Bar 1 fails, so the outcome is **drop**. With files-only at 94–96%, "both" could not clear a 10-point lead on these
questions whatever it did. Between "both" and files the disagreements were 3 against 2 questions (exact McNemar p = 1.0),
so the two are indistinguishable here. No single setup beat the other by 10 points overall (files 96%, rag 90%).

## (b) These questions are too easy to show what RAG adds

Files-only scored 94% on the full 773-file corpus and 96% on the 185-file one. When plain text search already answers
almost everything, no setup can show a large gain over it. That is a limit of this test, not proof that RAG is useless.
Two things made the questions easy for file search:

- The question writers had read the files, so even the "concept" questions tend to share words with the source, and a
  search for those words finds it.
- The corpus is small. An agent can grep 185 or 773 files and read the hits in two or three calls.

A test that would separate the setups needs a large corpus (tens of thousands of files, where reading every hit is too
slow) and concept questions whose wording does not appear in the files — synonyms, a user's description of a symptom,
or a question that spans several documents. That is where search by meaning should beat search by words.

## (c) CON-28: should raw code stay searchable next to the graph?

**Yes.** On the 24 code questions files-only scored 96% and RAG-only 79%, a 17-point gap, above the 10-point rule in
`pass-bar.md`. All five RAG misses were code concept questions; RAG got every exact code question right.

Part of the reason is that Connapse's stored chunks are not a faithful copy of code (see "Chunks against files" below):
a third of the source lines the code questions rely on (26 of 78) are not in the chunks as written, against none of the
48 for docs. That does not map one to one onto the misses — RAG still answered some questions whose evidence lines were
changed — so it is a contributing cause, not the whole story. The four questions files got right and RAG missed are a
small sample (exact McNemar p = 0.125 on code), but the direction matches the chunk damage.

## (d) How often the agent chose RAG when it had both tools

| Question type | Used `search_knowledge` at all | Called it first | Also used file tools |
|---|---|---|---|
| Concept (24) | 24 (100%) | 22 (92%) | 21 (88%) |
| Exact (24) | 8 (33%) | 7 (29%) | 24 (100%) |

The worry that the agent would ignore RAG once it had grep did not come true. It followed the instructions: meaning
search first for questions in plain words, text search first for exact names. But on concept questions it nearly
always went on to check the files too, which is why "both" cost 39% more than files-only without answering more.

## Chunks against files

Measured on the same 185 files after ingestion (`chunks_vs_files.py`, `chunk_coverage.py`; data in `results/`).

- **Speed:** searching chunk text in Postgres (`strpos` over 2,861 chunks) took a median 5.8 ms per pattern; ripgrep
  over the original files took 38.5 ms, including starting the process. Both are fast enough not to matter to an agent.
- **Completeness:** for 5 of 20 literal patterns the chunks missed files that contain the text; they never found a
  file the originals did not. All five misses were in code.
- **Why:** only 63% of code lines (20+ characters) appear word for word in the chunks, and 71% even when all whitespace
  is ignored, so about 29% of code lines are dropped or rewritten beyond whitespace. Docs: 95%. What was observed in
  the stored text: gaps between consecutive chunks (VectorColumnManager.cs lost the `EnsureIndexesAsync` signature
  between chunk 0 and chunk 1), a space inserted after every `.` (`searchSettings. CurrentValue`), and text inside angle
  brackets removed (`List<Group> groups` stored as `List groups`). The root cause in the parser or chunker was not
  traced; filed as CON-143. RAG's 79% on code was measured with only 63% of code lines stored faithfully, so fixing
  CON-143 may close part of the gap to files.

So for exact search, chunk text cannot stand in for the original files today, at least for code.

## How the run was set up, and what changed on the way

- **Chunker: Semantic (the default), kept on purpose.** Ingesting was slow (35–96 s per file with Ollama capped at
  2 CPUs). A probe on 20 unseen files measured Recursive at 3,398 ms per KB against Semantic's 2,890 — 0.85x, not the 3x
  that would have justified switching — because embedding, not chunking, is the bottleneck
  (`results/chunker_probe_fresh.json`).
- **Three files re-chunked with Recursive.** `PgVectorStore.cs`, `IngestionPipeline.cs` and
  `Storage/Extensions/ServiceCollectionExtensions.cs` failed four times: the Semantic chunker produced a chunk longer
  than nomic-embed-text's context, and Ollama answered 400 "the input length exceeds the context length". Connapse
  reports this as "Failed to connect to Ollama", which is misleading. These files hold evidence for six questions, so
  they were re-uploaded with Recursive (which caps chunk size). Filed as CON-144.
- **Corpus cut from 773 to 185 files.** Ingesting all 773 under the CPU cap would have taken about 3 hours. The cut keeps
  all 46 evidence files plus 139 already-ingested distractors (`shrink_corpus.py`, list in
  `results/corpus-small-files.txt`). Files-only was re-run on the same 185 files so all three setups saw the same set.
- **Side result:** files-only on the full 773 files scored 94% (45/48) at $0.0324 per question
  (`results/full-corpus/judged-files.jsonl`), close to its 96% on the cut corpus, so the cut did not change the
  picture for file search.
- **Not run:** a reranker, repeated attempts per question, and a harder question set — see (b).
