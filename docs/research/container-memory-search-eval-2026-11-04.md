# How to test whether container memory makes agent search better
**Date:** 2026-11-04
**Status:** Reviewed
**Built on:** no prior corpus material (the Connapse MCP server was not available in this session)

## Executive summary

No published benchmark tests what CON-244 needs: whether an agent's stored facts about a document
collection help it find the right document in that same collection. The nearest template is BrowseComp-Plus.
It fixes the corpus and retriever, labels the evidence documents, and scores the documents an agent finds over
its whole run, with search calls counted separately. The memory benchmarks (LongMemEval, LoCoMo,
MemoryAgentBench and others) score answers, not retrieval. Most of them also compare memory systems with each
other or with full context, rather than with no memory at all. The harm literature (MemTrapBench, memory
poisoning) shows that memory often makes agents worse, so a no-memory arm and stale-memory tasks are required,
not optional.

The design taken from this research:

- Hold everything fixed except the memory tool.
- Label gold and superseded documents by hand.
- Score the documents the agent cites, judge-free.
- Average repeats per question, then compare with a paired bootstrap and a permutation test.
- Test non-inferiority on the control tasks.

The main uncertainty is that several harm results were read from abstracts or summaries only.

## Research brief

- **Question:** How should an eval show that a container's memory makes an agent's search in that container
  better, and that it does no harm?
- **Sub-questions:**
  1. How do published memory benchmarks build questions and score them?
  2. Which papers ablate memory on and off, and what did memory do to quality?
  3. Which paired statistics fit a small, binary per-question score?
  4. How should a judge work, if one is needed?
  5. How do agentic search benchmarks score retrieval when an agent searches several times?
- **Out of scope:**
  - building memory-aware search on the server (a product feature, possible later ticket);
  - memory spaces (CON-238).
- **Success criteria:** a design whose pass bar can be written down before the first run.

## 1. Memory benchmarks: how they ask and score

Published memory benchmarks score answers, not which document was found.

- **LongMemEval** (arXiv 2410.10813):
  - 500 questions over five abilities, including knowledge update (the value changed) and abstention (`_abs`
    questions with no answer).
  - Scored by a GPT-4o judge with one binary prompt per kind, at temperature 0, reported as above 97% agreement
    with people.
  - Its knowledge-update prompt accepts an answer that also mentions the old value, as long as the updated one is
    given.
- **LoCoMo** (arXiv 2402.17753):
  - Long conversations with single-hop, multi-hop, temporal and open-domain questions, but no update category.
  - Reviewers report about 6.4% of its answer key as wrong.
- **MemoryAgentBench** (arXiv 2507.05257):
  - Its FactConsolidation task feeds ordered pairs of contradicting facts, where the later fact must win.
  - Scored by substring exact match.
- **MemBench and BEAM:** both include knowledge-update questions. MemBench is multiple choice; BEAM covers ten
  abilities, including contradiction resolution.
- **Letta Context-Bench:** tests an agent's own file and memory tools. MemoryArena is in the same family.
- **What CON-244 takes from these:**
  - Corrected facts with a known later value, from LongMemEval and FactConsolidation.
  - Exact matching instead of a judge, from MemoryAgentBench.
  - Distinct task kinds reported separately.

## 2. Memory on and off: what ablations found

Few papers compare memory with no memory at all, and the ones that do often find harm.

- **Mem0** (arXiv 2504.19413) and **Zep** (arXiv 2501.13956):
  - Both report against full-context and other memory systems, not against an agent without memory.
  - The two disagree about each other's LoCoMo numbers, and the dispute is unresolved.
- **Letta:** reported that a plain filesystem agent beat Mem0 on LoCoMo. A strong no-memory baseline can win.
- **Agent Workflow Memory** (arXiv 2409.07429): uses a true no-memory baseline and reports gains on web tasks.
- **Evidence that memory hurts** (abstracts and summaries only):
  - **MemTrapBench:** agents scored 85.2 without memory and 71.2 with the best memory system.
  - **Experience-following work:** wrong stored experience propagates into later errors.
  - **Stateful CARS:** used a 0.10 non-inferiority margin with a paired bootstrap, and could not show
    non-inferiority.
- **Memory poisoning:** MINJA (arXiv 2503.03704) and AgentPoison (arXiv 2407.12784) show that injected memories
  steer agents.
- **MemDelta:** argues for changing only the memory switch between arms.
- **What CON-244 takes from these:**
  - The off arm is the same agent with the memory tool removed, not a weaker agent.
  - A control set where memory should not matter, plus stale facts, measures harm.

## 3. Paired statistics for a small eval

Use the question as the unit, average repeats within a question, and test the paired difference.

- **Repeats:** average them per question first. Treating repeats as independent overstates n.
- **Tests:**
  - For a binary outcome per question, the exact McNemar test is the sign test on discordant pairs.
  - For averaged scores in [0, 1], the sign-flip permutation test and the BCa bootstrap in
    `tests/Connapse.Eval/Statistics/PairedStats.cs`, ported from SciPy, fit directly. Urbano et al. (2019)
    favour the t-test and permutation test for IR decisions.
- **Non-inferiority:** shown when the lower bound of a two-sided 90% interval (a one-sided 95% interval) is
  above −margin. Tango's score interval is the binary-outcome analogue.
- **Several gates:** when every gate must pass (intersection-union), each is tested at full α with no split.
- **Power:**
  - With about 40 questions, 80% power is reached only for 20–28-point differences on binary outcomes.
  - A 30-question harm set with a 10-point margin has roughly 28–53% power.
  - The control set therefore needs more questions or averaged repeats. CON-244 uses 48 harm questions and three
    repeats.
- **Pre-registration:** questions, metric, bar, margin, α, repeats, model and exclusion rules are committed
  before any scored run.

## 4. Judging

CON-244 needs no judge. The agent must cite document IDs, and gold and superseded documents are labelled.

- If an answer judge were used:
  - it would follow LongMemEval: binary, temperature 0, blind to the arm, from a different model family than
    the agent;
  - it would count an answer that hedges between several options as wrong.
- **Mem0's "J" judge** is reported as overly generous.
- **Exact matching** (MemoryAgentBench) avoids judge bias. CON-244 scores the answer value as a secondary
  metric by normalized substring match.

## 5. Agentic search: scoring retrieval over several queries

When an agent searches several times, the field reports three separate scores rather than one ranking metric.

- **BrowseComp-Plus** (arXiv 2508.06600):
  - 830 queries over about 100K documents, a fixed retriever, and a search tool fixed at top-5.
  - Evidence and gold documents are labelled, with hard negatives.
  - It reports the recall of evidence documents retrieved at any point in the run, citation precision and
    recall, and search calls.
  - It also scores the retriever alone with the original question (nDCG@10, Recall@k).
  - A better retriever gave higher accuracy with fewer search calls.
- **BRIGHT** (arXiv 2407.12883): reasoning-heavy retrieval scored by nDCG@10. Rewriting the query with reasoning
  helps by up to 12.2 points, which is a close analogue of rewriting a query with recalled facts.
- **FreshStack** (arXiv 2504.13128): nugget coverage, α-nDCG@10 and Recall@50.
- **Bespoke** (arXiv 2509.21106) and **LaMP** (arXiv 2304.11406): user history shaping search. They score
  outputs or information needs, not retrieval inside a fixed collection.
- **Aliases:** AmbER (arXiv 2106.06830) builds sets of entities that share a name. No benchmark tests team
  nicknames that are absent from the documents, so CON-244 builds its own and checks that no document contains
  the alias.
- **Stale documents:**
  - TempRAGEval (arXiv 2412.15540) labels time-sensitive evidence.
  - HoH (arXiv 2503.04800) keeps outdated documents in the corpus and finds they mislead models even when the
    current one is also retrieved.
  - VersionQA (arXiv 2510.08109) reports 58% for naive RAG on versioned documents.
- **What CON-244 takes from these:**
  - The fixed tool and corpus, labelled gold and superseded documents, and citation scoring, from BrowseComp-Plus.
  - Run recall and first-query MRR as separate secondary scores.
  - A retriever-only arm, which also prepares the later server-side variant.

## Conflicts and uncertainties

- The Mem0 and Zep LoCoMo numbers conflict; neither has a no-memory arm, so neither is used.
- The MemTrapBench, Stateful CARS, MemDelta and experience-following findings were read from abstracts or
  secondary summaries.
- BrowseComp-Plus does not give a formula for its run-level recall. CON-244 uses the union of all search
  results, which matches its project page wording.

## Gaps

- No published eval measures memory feeding search within one fixed collection.
- No published paired memory on/off result on search-call counts was found.
- No team-alias retrieval benchmark exists.

## Source quality

Most load-bearing choices rest on primary papers: LongMemEval, MemoryAgentBench, BrowseComp-Plus, BRIGHT, HoH,
and the SciPy port already in the harness. The harm evidence is partly secondary, but it only motivates
including control and stale tasks, which costs little.

## Sources

- **Primary:**
  - LongMemEval (arXiv 2410.10813), LoCoMo (arXiv 2402.17753), MemoryAgentBench (arXiv 2507.05257)
  - Mem0 (arXiv 2504.19413), Zep (arXiv 2501.13956), Agent Workflow Memory (arXiv 2409.07429)
  - MINJA (arXiv 2503.03704), AgentPoison (arXiv 2407.12784)
  - BrowseComp-Plus (arXiv 2508.06600), BRIGHT (arXiv 2407.12883), FreshStack (arXiv 2504.13128)
  - LaMP (arXiv 2304.11406), Bespoke (arXiv 2509.21106), AmbER (arXiv 2106.06830)
  - TempRAGEval (arXiv 2412.15540), HoH (arXiv 2503.04800), VersionRAG (arXiv 2510.08109)
  - Urbano, Lima and Hanjalic, "Statistical Significance Testing in Information Retrieval" (SIGIR 2019)
  - Tango (1998), the score interval for paired proportions
- **Secondary, read as abstracts or summaries:** MemBench, BEAM, MemoryArena, Letta Context-Bench and the Letta
  LoCoMo post, MemTrapBench, Stateful CARS, MemDelta, the experience-following paper, and the BrowseComp-Plus
  summary at emergentmind.com.
