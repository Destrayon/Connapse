# Plan: making Connapse memories easier for agents to find and save (CON-313)
**Date:** 2026-11-10
**Status:** Proposed, awaiting the user's approval before any search code changes
**Built on:** `memory-findability-2026-11-09.md` (research), `eval/baselines/memory-agent-test-2026-11-10.json`
(baseline), the CON-313 section of `eval/pass-bars.md` (bar)

## Summary

Today's agents already find and save memories well. Claude Sonnet 5.5 scores 0.946 on the hard find tasks and 0.986
on the save tasks, even though `memory_recall` with the question as typed puts the right fact in the top 10 for
only half the hard tasks, and for none of the indirect or partial ones. The agent closes the gap itself: it rewords
its query, expands abbreviations ("sec eng" to "security engineer"), and lists every fact about a subject once it
works out which subject is meant. What that costs is extra calls: only 47% of first recalls hit on indirect questions,
which average 2.04 recall calls, and one took 7.

So the problem worth fixing is how much work the agent has to do, and how much callers that cannot reword their
query (REST search, the UI, prompt-injected recall, weaker models) get back. The pick is **cheaper matching inside
`memory_recall` first, with embeddings only if that falls short**. Text-to-Cypher is rejected, and save needs no
change. The pass bar fixed before the run cannot be met, since it asks for +0.15 over 0.946. This plan proposes a
replacement for the user to approve, judged on a fresh held-out task set.

## What the baseline shows

Conforming run on the 74 find and 24 save test tasks, `keyword` config, 3 repetitions, 2026-11-10.

| Find kind | Tasks | Agent score | First recall shows gold | Recall calls | Retrieval-only hit@10 |
|---|---|---|---|---|---|
| exact | 10 | 1.00 | 1.00 | 1.00 | 1.00 |
| reworded | 31 | 0.92 | 0.95 | 1.11 | 0.90 (hit@1 0.13) |
| indirect | 15 | 0.96 | 0.47 | 2.04 | 0 |
| partial | 10 | 1.00 | 0.60 | 1.73 | 0 |
| absent | 8 | 0.88 | — | 1.96 | returned something for 8 of 8 |
| **hard** (reworded + indirect + partial) | 56 | **0.946** | **0.756** | **1.47** | 0.50 |

- **Save:** new 1.00, update 0.96, duplicate 1.00. The agent ends the old value with `memory_correct` on the same
  chain every time it updates, and never saves a duplicate. The single miss overwrote an unrelated fact and then
  put it back.
- **Speed and cost:** `memory_recall` p50 12 ms, p95 20 ms. A find run costs about $0.013 at API list price, billed
  to the subscription.
- **Misses are mostly scoring strictness, not search.** Of the 12 failed find runs, 3 named the old owner alongside
  the current one, 3 correctly said memory had no such fact but cited the nearest fact anyway, and 2 gave the right
  answer while citing the wrong fact. The other 4 are real search misses. In `find-r01`, "line of code" never led the
  agent to "release branch". In `find-i06`, the agent stopped at Ferro's purpose fact and never saw the fact naming
  its scheduler. The committed scoring is left as it is.
- **Indirect questions are where the work goes.** The trace for `find-i03`, "How long does a sign-in stay valid at
  the SSO gateway?", shows the pattern. The query matches the fact that says Wicket *is* the SSO gateway, and the
  agent then lists Wicket's facts to find the session length. The fact it needs never shares a word with the
  question, but another fact about the same subject does.

## Options compared

| Option | What it fixes | Cost and risk | Verdict |
|---|---|---|---|
| **A. Cheaper matching inside `memory_recall`**: index each subject's describing facts with its other facts, rank with IDF, add `pg_trgm` for partial words, and hint on empty results | indirect (A1), reworded ranking (A2), spelling and prefixes (A3), wasted retries (A4) | SQL and one migration, no model, no new tool | **Pick, stage 1** |
| **B. Hybrid: embed each fact as a short sentence and fuse with keyword** | meaning matches with no shared word, such as `find-r01` | an embedding per fact and per query, per-model columns, a no-match cutoff per model (the CON-278 problem) | **Stage 2, only if A misses the bar** |
| **C. Simpler memory tools** (merge `memory_recall` and `memory_timeline`, fewer parameters) | tool-choice errors | agents make almost none: save scores 0.986 and agents choose filters correctly | Tool-text fixes go into A4; no consolidation |
| **D. LLM-written Cypher over a graph** | multi-hop questions, which the set barely has | about 60% zero-shot accuracy (CypherBench); fails on the same wording mismatch; arbitrary queries bypass the per-space and per-owner filter in application code; Apache AGE is low-activity | **Rejected** |

**Stage 1 parts:**

- **A1, subject expansion.** A fact's search text also carries the values of describing facts about the same subject
  in the same space, such as "description", "is" and "also known as". "SSO gateway" then reaches Wicket's
  session-length fact directly. This applies LongMemEval's finding that adding extracted facts to the index key
  raised recall (+9.4%), and it is the cheap form of Mem0's entity matching. A changed describing fact refreshes the
  other facts of that subject. Which attributes count as describing is a fixed list, and the plan states it before
  the code.
- **A2, ranking.** `ts_rank` has no IDF, so on reworded questions gold is in the top 10 for 90% of tasks but first for
  only 13%. Weight query words by how rare they are in the space, which `DistinctiveWordsAsync` only approximates
  today.
- **A3, partial words.** Add a `pg_trgm` `word_similarity` match on subject, attribute and value as a second keyword
  signal, so "Postgres" finds PostgreSQL and "tmpl" finds template. It cannot link k8s to Kubernetes; agents already
  expand those.
- **A4, tool text and hints.** When a query matches nothing, say so and list the space's subjects whose describing
  facts come closest, with the next call to make. The tool description says that a subject filter lists everything
  about a subject. The research found this recommended but never measured, so it is measured here.

Stage 2 reuses document search's embedding path (`Connapse.Search`, `VectorColumnManager`) and CON-278's per-model
cutoff. Stage 1 is worth trying first because the baseline shows every indirect miss sits one describing fact away,
which A1 covers without a model.

## The bar

Bar 1 as registered asks hard find tasks to gain +0.15 over today's 0.946, which no change can do. The tasks have
also now been seen, and A1 and A4 were shaped by their traces. So stage 1 cannot honestly be judged on them. The
rules in `eval/pass-bars.md` say a bar is never changed after results, so this replacement needs the user's
approval. It comes in as a new pass-bars section that names why, and the old section stays as written.

**Proposed bar.** It is judged on a fresh held-out set: 60 or more new hard find tasks, 8 absent and 24 save tasks
over a new made-up company, written by the same generator rules and committed sealed before any stage-1 code. The
baseline is re-run on that set. The change passes only when all of these hold:

1. **Less agent work:** on hard find tasks, the first `memory_recall` shows gold for at least 0.90 of them and gains
   at least +0.10 over today, and recall calls per task fall by at least 0.3. Both are tested with the paired
   permutation test at p < 0.05.
2. **Callers that cannot reword:** retrieval-only hit@10 on hard tasks is at least 0.80.
3. **No harm:** hard, exact, absent and save scores are each non-inferior, meaning the lower end of the 90% BCa
   interval of the difference is above −0.05. The margin is tighter than the old −0.10 because scores sit near 1.0.
4. **Speed:** `memory_recall` p95 is under 300 ms.
5. **Leaks:** the permission-leak suite passes with zero failures. A1 must expand only within the same space.

If stage 1 fails bar 1 or 2, stage 2 (embeddings) is built and judged on a second sealed set. Bars 3–5 failing
blocks the change until fixed.

## What we do not know

- Whether real project-manager memories carry describing facts for their subjects the way this set does. A1 helps
  only where they exist, so the held-out set should include some subjects with none.
- How a weaker model does. A Haiku run was started for comparison but was stopped at 32 of 222 runs when the machine
  ran low on memory. Running it again would show how much headroom smaller agents have.
- Whether the empty-result hint (A4) changes agent behaviour, which is unmeasured anywhere.

## Sources

The full source list, with tiers, is in `memory-findability-2026-11-09.md`. The ones the pick rests on are:
LongMemEval (arXiv 2410.10813), EntityQuestions (arXiv 2109.08535), Dense X Retrieval (arXiv 2312.06648),
CypherBench, Graphiti/Zep (arXiv 2501.13956), Mem0 (arXiv 2504.19413 and the 2026 README), Anthropic's "Writing
effective tools for agents", the PostgreSQL text search and `pg_trgm` docs, and Miller, "Adding Error Bars to Evals"
(arXiv 2411.00640).
