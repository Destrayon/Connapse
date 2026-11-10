# Where memory search falls short of grep, and how to close it (CON-321)
**Date:** 2026-11-10
**Status:** Reviewed
**Built on:** `memory-findability-2026-11-09.md`, `memory-findability-plan-2026-11-10.md` (the CON-322 scope),
`eval/baselines/memory-agent-sealed-1-memory-2026-11-10.json` and `-grep-2026-11-10.json`

## Executive summary

On the sealed set, memory search and grep are tied on accuracy (0.930 against 0.934), but memory costs 84% more per
find task ($0.0138 against $0.0075). Almost all of the extra cost is the shape of `memory_recall`'s result: each call
returns about 2,880 characters against grep's 772, and 65% of those characters are fields agents never use. That same
verbose shape, with three or four lookalike UUIDv7 IDs per fact, caused 7 of memory's failed runs, where the agent
copied the wrong ID. Search quality explains the rest: 3 lost questions and most of the extra calls trace to missing
partial-word and synonym matching, ranking that ignores how rare a word is, and no easy way to see every fact about a
subject. CON-322 as scoped covers ranking, partial words, subject describing facts and empty-result hints. It does not
touch the result shape. So it cannot meet the approved bar, which allows memory to cost at most 5% more than grep.
**Recommendation: keep memory search, and add a compact result shape with short fact handles to CON-322.**

## Which questions were lost, and why

Of the 100 sealed tasks, 18 qualify: memory scored lower on 5 and made more calls on 13. Scores are means over 3
repetitions. "Calls" counts read calls per run. Memory beat grep on four other questions (r01, i01, i10, a08), all by
not citing a superseded or unrelated fact.

| Task | Question | Memory / grep score | Calls | Main cause |
|---|---|---|---|---|
| find-r10 | oldest phone OS Corvid runs on | 0.33 / 1.00 | 1.00 / 1.00 | wrong ID copied (gold was rank 1) |
| find-p02 | min OS the drivers' app supports | 0.33 / 1.00 | 1.00 / 2.00 | wrong ID copied (gold was rank 1) |
| find-r20 | final say on Plinth's design choices | 0.00 / 1.00 | 1.00 / 2.00 | ranking: "design" outranked the subject; gold 4th, agent declined |
| find-i07 | scheduler for the hourly BigQuery load | 0.00 / 1.00 | 1.00 / 2.00 | synonym ("scheduler" vs "orchestrator"); subject's other facts never listed |
| find-i02 | who runs pallet tracking these days | 0.00 / 0.33 | 2.00 / 2.00 | cited the ended owner beside the current one |
| find-p11 | cfg folder helpers stay out of | 1.00 / 1.00 | 4.67 / 2.00 | abbreviation and synonym ("cfg", "helpers" vs "config", "workers"); 2 zero-hit calls, then a 100-fact dump |
| find-i11 | who runs the January ship | 1.00 / 1.00 | 3.67 / 2.67 | "January" vs stored "2027-01-19"; zero hits, then a 100-fact dump |
| find-i18 | who works out VAT | 1.00 / 1.00 | 3.00 / 2.00 | synonym ("VAT" vs "tax engine") |
| find-i08 | where BigQuery job failures are reported | 1.00 / 1.00 | 3.00 / 2.00 | extra probes with gold already at rank 1 |
| find-i04 | what sits in front of the parcel lookup API | 1.00 / 1.00 | 2.67 / 2.00 | ranking ("API" pulled the vendor mapping fact up) |
| find-i14 | when the principal engineer is away | 1.00 / 1.00 | 2.00 / 1.33 | role is a value, vacation is under the person's name |
| find-i15 | what hardware drivers carry | 1.00 / 1.00 | 2.00 / 1.00 | synonym ("hardware" vs "device fleet") |
| find-i17 | response time the parcel API must hit | 1.00 / 1.00 | 2.00 / 1.67 | ranking ("time" pulled time-zone facts up) |
| save-u01 | Tessera fixes target release/7.3 | 0.67 / 1.00 | 2.67 / 2.00 | corrected the neighbouring wrong fact |
| save-u04 | Mallow deploy window moved | 0.67 / 1.00 | 2.33 / 2.00 | corrected the neighbouring wrong fact |
| save-u07 | crash reports go to Sentry | 1.00 / 1.00 | 2.67 / 2.00 | one run passed an invented ID (`…-0000-000000000000`) |
| save-n06, save-n08 | new facts | 1.00 / 1.00 | 3.00, 2.33 / 2.67, 2.00 | one extra read before saving; not classified further |

How the two arms failed overall, across 228 find runs each:

- **Memory** failed 16 runs: 6 cited a superseded fact, 4 cited an ID that appeared in no tool output, and 6 failed
  for other reasons.
- **Grep** failed 15 runs: 12 cited a superseded fact and 3 failed for other reasons. Grep never cited an ID it hadn't
  been shown.

Grep agents won the extra-call questions with two habits. They put every variant into one regex
(`holiday|away|leave|vacation`), and their second call was usually `grep <Subject>`, which lists every fact about
that subject.

## The shortfalls, ranked by cost

### 1. The result shape: verbose JSON and lookalike IDs

**Cost:** 4 failed find runs (r10, p02), 3 failed or broken save runs (u01, u04, u07), and most of the 84% cost gap.

`memory_recall` returns each fact as a JSON object with:

- the fact's own ID;
- a nested space object;
- the scope;
- `recordedAt` and `supersededAt`;
- a `replaces` ID;
- a nested author with two more IDs;
- the sources.

That is 3 or 4 UUIDs per fact (`src/Connapse.Web/Mcp/McpTools.Memory.cs:348-366`, with `replaces` at :361 and the
author at :363). Results also carry a notice and a base64 cursor (:331-346).

Across 389 recall calls, 65% of the output characters were fields other than id, subject, attribute, value, the
valid-from and valid-to dates, status and reason.

The ID errors have one shape. UUIDv7 IDs begin with a timestamp, so facts seeded together share long prefixes. The
agent cited `…7e44-7757…` when the fact was `…7e41-7757…`, and twice ran `memory_correct` on the fact next to the
right one (the `factId` parameter is at :230).

A find run's tool output averages 4,496 characters for memory against 1,156 for grep. That output is re-read on every
later turn, so it dominates the cost difference. The find tools' descriptions add only about 1,250 characters more
than grep's.

What others do:

- Anthropic's tool-writing guide says that resolving UUIDs "to more semantically meaningful … (or even a 0-indexed
  ID scheme) significantly improves Claude's precision in retrieval tasks by reducing hallucinations". It also says
  to drop low-level identifiers and to offer a concise response format.
- BoundaryML measured about 48 ID errors with raw UUIDs against about 6 with integers on the same task.
- SWE-agent's ablation found concise search output resolved 18.0% of issues, against 12.0% for verbose output.

**Fix:**

- Default to one line per fact: `f3 | subject | attribute | value | from → to | current` (or `ended 2026-10-02,
  replaced by f2`).
- Give each fact a short per-response handle (`f1`, `f2` …) that `memory_correct` and `memory_forget` accept,
  resolved on the server for that caller.
- Reject any full ID that was never shown, with an error that lists the valid handles.
- Put space, author, recordedAt and sources behind a `detailed` option.
- Shorten the cursor to a short token on a last line.

**CON-322: not covered.** Without this, the bar's cost test (no more than 5% above grep) is out of reach whatever
happens to ranking.

### 2. Words the agent and the fact don't share: synonyms and abbreviations

**Cost:** 1 lost question (i07) and extra calls on p11, i18, i15 and i14.

The query is matched only by `english` stems of the exact words
(`src/Connapse.Storage/Memory/PostgresMemoryStore.cs:241-260`, tokenizer at :296 and :437). So "scheduler" never
reaches "orchestrator", "VAT" never reaches "tax engine", and "cfg" never reaches "config".

Query words are already OR-ed together (:292-294), so an agent could pass `scheduler orchestrator cron` in one call. But
the tool description (`McpTools.Memory.cs:272`, parameter at :277) never says so. Agents send one phrasing per call,
where grep agents send an alternation.

What others do:

- Every memory system surveyed fuses embeddings with keyword search. Graphiti uses BM25 plus cosine similarity with
  Reciprocal Rank Fusion (RRF). Mem0 uses vector plus BM25 plus entity matching. Letta uses vector plus full text with
  RRF.
- A-MEM and LongMemEval add generated keywords to each entry when it is written. LongMemEval measured +9.4% recall
  from extending the index key this way.
- On Azure Flexible Server, synonym dictionaries (`dict_xsyn`) need files the managed service likely won't accept.
  This is unverified.

**Fix, in order of cost:**

1. Tell agents in the `query` description to put every synonym and abbreviation in one query, since words are
   OR-ed.
2. Store a few agent- or model-written keywords per fact in the weight-D part of `search_vector`, as A-MEM does.
3. Add embeddings fused by RRF.

**CON-322: partly covered.**

- A1 (each subject's describing facts are added to its other facts' search text) helps i14 and i07 only where the
  set has a describing fact.
- Stage 2 (embeddings) is held back unless stage 1 misses.
- The query-description fix and write-time keywords are not in scope.

### 3. Ranking that ignores how rare a word is

**Cost:** 1 lost question (r20) and extra calls on i04 and i17.

`ts_rank` has no inverse document frequency, so a rare word scores the same as a common one
(`PostgresMemoryStore.cs:260`). The only correction drops words that match more than half the facts (:304-332).

In r20, "design" lifted the "design system" facts above Plinth's tech lead. In i17, "time" lifted the time-zone
facts.

The PostgreSQL docs confirm the ranking functions "do not use any global information". None of the BM25 extensions
(ParadeDB `pg_search`, VectorChord-bm25, `pg_textsearch`) is on the Azure Flexible Server allow-list.
`pg_textsearch` is offered only on Azure HorizonDB, which is in preview.

**Fix:** compute IDF in SQL from per-space word counts, taken from `ts_stat` or a small table kept up to date on
write. Score each fact as the sum of IDF × field weight over the words it matches. At hundreds to low thousands of
facts per space, the speed concern ParadeDB raises does not apply.

**CON-322: covered by A2.**

### 4. No easy way to see a subject's other facts

**Cost:** part of i07 and extra calls on i14, i04 and i08.

Grep agents list a subject with `grep Gantry`. Memory can do the same with `subject`, but that filter is an exact,
case-insensitive match (`PostgresMemoryStore.cs:213-214`, parameter at `McpTools.Memory.cs:278`). The tool
description never suggests it as the follow-up, and a hit never brings its subject's other facts along.

In i07 the first call returned two Gantry facts. The agent answered "Gantry" and stopped; it never saw the
orchestrator fact.

Mem0's graph mode and Graphiti both walk from an entity found in the query to every fact attached to it. Graphiti
also takes filters for "every fact about this entity" (`edge_source_node_uuids`).

**Fix:**

- When hits concentrate on one or two subjects, append those subjects' other current facts below the hits as
  compact lines.
- Say in the description that `subject` lists everything about a subject.

**CON-322: partly covered.**

- A1 (describing facts in the search text) and A4 (description says a subject filter lists everything) cover the
  description half.
- Appending a hit subject's other facts is not in scope.

### 5. Fragments and dates

**Cost:** extra calls on i11 and p11 (14 zero-hit calls in all, 4 of them followed by a 100-fact dump), and the
"BigQuery" probe in i07.

"January" cannot match a stored `2027-01-19`. `pg_trgm` and prefix queries do not fix that, and no source covers it.
`pg_trgm` (on Azure's allow-list) does fix spelling and partial words such as "Postgres" for PostgreSQL. It does not
fix abbreviations such as "cfg", which share almost no three-letter sequences with "config".

**Fix:**

- Add `pg_trgm` `word_similarity` over subject and value.
- When a value parses as a date, write the month name and year into the weight-D text.

**CON-322: trigram covered by A3; dates not covered.** The empty-result hint (A4) should stop the 100-fact dumps.

### 6. Citing ended facts

**Cost:** 1 lost question (i02, where grep also lost 2 of 3).

The tool description invites `includeHistory` (`McpTools.Memory.cs:272`, :281). History rows look just like current
ones apart from a `status` field, and the find prompt asks for an answer with cited facts. Agents then cited both the
current and the ended owner.

Memory is already better than grep here: 6 such runs against 12. Graphiti returns ended facts by default, with date
ranges, and Mem0's 2026 docs admit "prior facts can still surface alongside newer ones". Keeping old facts as a
chain, as Connapse does, matches where those systems ended up.

**Fix:**

- Write history rows as `ended <date>, replaced by f2`.
- Add one line to the description: "cite only current facts unless the question asks about the past".

**CON-322: not covered.**

## What CON-322 should add

1. A compact default result with short handles and validated IDs (shortfall 1). This is the largest item, and the only
   route to the cost bar.
2. Query-description guidance to OR every synonym and abbreviation in one call (shortfall 2). This is one line of
   text.
3. Appending a hit subject's other current facts (shortfall 4).
4. Month names for date values, and history rows that name their replacement, with a cite-current line
   (shortfalls 5 and 6).

A2 (rare-word weighting) and A3 (trigram) stay as planned. A1 and A4 stay too, but they only partly close shortfalls
2 and 4.

## Should memory search stay?

Yes. Grep ties on accuracy only because the sealed set is small (252 facts), and every gap above is in our
implementation, not in the idea.

The evidence on grep against an index points the same way:

- Claude Code's grep-first choice and Letta's file-plus-grep LoCoMo result come from small corpora or informal
  evidence.
- Controlled and larger studies favour an index, ideally beside grep:
  - Cursor saw +12.5% accuracy from adding semantic search, with the gain concentrated in codebases over 1,000 files.
  - A 2026 SWE-QA study measured 65.2% against 46.2%.
  - BrowseComp-Plus measured 70.1% with embeddings against 55.9% with BM25.

Memory also does two things grep cannot:

- It hides ended facts unless asked, which halves superseded citations and gives a perfect score on absent facts
  (1.0 against 0.875).
- It filters by space and owner before the agent sees anything. A grep-over-a-dump design would have to rebuild both.

The fixes above are SQL plus result formatting, and none needs a new extension on Azure.

## Conflicts and uncertainties

- The cost split is inferred from characters, not tokens. The runs record cost per run but not tokens per call.
- The ID-copying evidence outside this eval is one practitioner benchmark (BoundaryML) and Anthropic's statement. No
  peer-reviewed study exists, and none covers UUIDv7 specifically.
- RRF's k = 60 is the commonly cited value from Cormack et al. 2009. The paper's text was not checked. Bruch et al.
  2023 find a tuned score blend beats RRF.
- LoCoMo numbers for Mem0 and Zep are disputed by both vendors, and an audit reports many wrong answer keys. Nothing
  here rests on them.
- Microsoft's HorizonDB page calls the built-in ranking unnormalized by length. It does have opt-in normalization
  flags, but no IDF.

## Gaps

- There is no published evaluation of hybrid search on short facts. Every hybrid-search gain cited comes from passage
  benchmarks.
- No source covers matching month names to ISO dates.
- Whether Azure Flexible Server accepts custom synonym dictionary files is unverified.
- save-n06 and save-n08 (under half a call each) were not traced to a cause.

## Sources

**Primary**

- Anthropic, "Writing effective tools for agents": https://www.anthropic.com/engineering/writing-tools-for-agents
- SWE-agent (arXiv 2405.15793): https://arxiv.org/abs/2405.15793
- MCP specification, tools (2025-06-18): https://modelcontextprotocol.io/specification/2025-06-18/server/tools
- PostgreSQL text search controls: https://www.postgresql.org/docs/current/textsearch-controls.html
- PostgreSQL `pg_trgm`: https://www.postgresql.org/docs/current/pgtrgm.html
- PostgreSQL `dict_xsyn`: https://www.postgresql.org/docs/current/dict-xsyn.html
- Azure Flexible Server extensions: https://learn.microsoft.com/en-us/azure/postgresql/extensions/concepts-extensions-versions
- Azure HorizonDB full-text search: https://learn.microsoft.com/en-us/azure/horizondb/ai/full-text-search
- `pg_textsearch`: https://github.com/timescale/pg_textsearch
- VectorChord-bm25: https://github.com/tensorchord/VectorChord-bm25
- ParadeDB install: https://www.paradedb.com/docs/documentation/getting-started/install
- pgvector: https://github.com/pgvector/pgvector
- Supabase hybrid search: https://supabase.com/docs/guides/ai/hybrid-search
- Cormack, Clarke and Büttcher, RRF (SIGIR 2009): https://dl.acm.org/doi/10.1145/1571941.1572114
- Bruch, Gai and Ingber (arXiv 2210.11934): https://arxiv.org/abs/2210.11934
- Zep/Graphiti (arXiv 2501.13956): https://arxiv.org/html/2501.13956
- Graphiti search: https://help.getzep.com/graphiti/working-with-data/searching
- Graphiti search filters: https://github.com/getzep/graphiti/blob/main/graphiti_core/search/search_filters.py
- Mem0 (arXiv 2504.19413): https://arxiv.org/html/2504.19413
- Mem0 add operations: https://docs.mem0.ai/core-concepts/memory-operations/add
- Mem0 evaluation: https://docs.mem0.ai/core-concepts/memory-evaluation
- Letta archival search: https://docs.letta.com/guides/agents/archival-search
- LangMem conceptual guide: https://langchain-ai.github.io/langmem/concepts/conceptual_guide/
- A-MEM (arXiv 2502.12110): https://arxiv.org/html/2502.12110
- LongMemEval (arXiv 2410.10813): https://arxiv.org/html/2410.10813
- Cursor semantic search (vendor): https://cursor.com/en-US/blog/semsearch
- Letta memory benchmark (vendor): https://www.letta.com/blog/benchmarking-ai-agent-memory
- SWE-QA agentic vs semantic (arXiv 2608.01507): https://arxiv.org/abs/2608.01507
- MemDelta (arXiv 2606.29914): https://arxiv.org/abs/2606.29914
- Keyword search for agents (arXiv 2602.23368): https://arxiv.org/html/2602.23368
- BrowseComp-Plus (ACL 2026): https://preview.aclanthology.org/ingest-acl/2026.acl-long.1023/

**Secondary**

- BoundaryML, UUID swap: https://boundaryml.com/blog/uuid-swap
- Manus, context engineering: https://manus.im/blog/Context-Engineering-for-AI-Agents-Lessons-from-Building-Manus
- ParadeDB, BM25 in PostgreSQL: https://www.paradedb.com/learn/search-in-postgresql/bm25
- Augment engineer on grep vs embeddings: https://jxnl.co/writing/2025/09/11/why-grep-beat-embeddings-in-our-swe-bench-agent-lessons-from-augment/
- Zep's rebuttal of Mem0's LoCoMo numbers: https://www.getzep.com/blog/lies-damn-lies-statistics-is-mem0-really-sota-in-agent-memory/
- Boris Cherny on agentic search (interview summary): https://officechai.com/ai/claude-researcher-explains-how-agentic-search-performed-better-than-rag-for-code-generation/

**Tertiary**

- LoCoMo answer-key audit: https://dev.to/penfieldlabs/we-audited-locomo-64-of-the-answer-key-is-wrong-and-the-judge-accepts-up-to-63-of-intentionally-33lg
