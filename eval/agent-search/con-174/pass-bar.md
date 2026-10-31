# CON-174 pass bar

Written and committed on 2026-10-31, before any scored run. The run may not change it.

## The question
Does a folder summary help agents answer more questions when it is put in front of them, in the folder list,
instead of behind a tool they never call?

"Folder" here is the knowledge container: Connapse writes summaries per container, not per path folder, and erb-50k
puts every record in its own numbered folder, so a path-folder summary would be a document summary under another name.

## Why CON-160 did not answer it
- Its agents never called `container_describe` (0 of 48), so the container summary was never read.
- The summary it would have read described 7 chat logs (breakfast tacos, fantasy basketball, a monitor sale) and told
  the agent "all seven documents are shown, so these exclusions are firm." The product's document-clustering rollup
  picks 20 medoids (cluster centres) from all 50,000 records but keeps only those among the first 10,000 it lists,
  which left 7; it then tells the container prompt the container holds 7 documents, all shown. That summary misdescribes the container, so this test writes its
  own (below) and the rollup problem is reported separately.
- `container_list`, the folder list, cuts a summary to its first sentence, at most 120 characters.

## What runs
- CON-146's 48 questions (`../con-146/questions.json`): 36 hard and 12 controls over erb-50k (50,000 generated
  records of one company). Same restored CON-107 index, MinIO loaded so `get_document` works (as in CON-160), same
  agent (Sonnet 5.5 through Claude Code 2.1.295 `claude -p`, at most 25 turns, one attempt per question, one
  question at a time), same judge (CON-142's, with CON-146's one-word change).
- Two setups, both with CON-146's five tools plus `container_list`, CON-146's `files_first` prompt, and a section at
  the end of the system prompt that shows what `container_list` returns, so the agent has read the folder list before
  its first turn:
  - **baseline**: the listing exactly as Connapse returns it today for this container, which has no summary.
  - **folder**: the same listing with one more line, `Summary:`, holding the whole container summary (not cut to
    120 characters). `container_list` calls return the same listing through an eval-only proxy. No product change.
- **The container summary** is written once, before the run, with Connapse's own prompts and Claude Haiku: the
  per-document prompt (`SummaryPrompts.PerDocSystemPrompt`) on 20 records drawn at random (fixed seed 174) from the
  50,000, leaving out the gold records of all 48 questions so the summary cannot carry an answer; then the container
  prompt (`SummaryPrompts.ContainerRollupSystemPrompt`) with Connapse's user message for 50,000 documents shown as a
  sample. 20 is the product's `MaxClusters`. The summary text is committed with the results.
- Cost: `total_cost_usd` that Claude Code reports per question (agent only). Writing the summary is a one-time cost
  (about $0.03), reported and not gated.

## Definitions (same as CON-160)
- **Already right today**: a question (hard or control) that both CON-146's `files_first` run and this baseline
  answered correctly. CON-146's `files_first` got these 31 right: qst_0001, 0024, 0034, 0042, 0046, 0055, 0067, 0078,
  0081, 0083, 0091, 0108, 0178, 0191, 0193, 0196, 0213, 0214, 0235, 0240, 0244, 0245, 0248, 0251, 0255, 0268, 0270,
  0273, 0282, 0289, 0299.
- **Dropped**: an already-right question the folder setup gets wrong in at least 2 of 3 attempts. Each one it misses
  is asked twice more under the folder setup.

## The bar
Folder summaries help (**yes**) only if all three hold against the same-day baseline:
1. **More hard questions right:** at least 4 more of the 36 (10 percentage points), the margin CON-146 and CON-160
   used.
2. **Cost:** average agent cost per question over all 48 at most 20% above the baseline's.
3. **No drop:** zero dropped questions.

## What each outcome means
- All three hold: **yes**. Show the whole container summary in `container_list`; a follow-up fixes the rollup so the
  product writes a summary like this one, and decides how agents get the folder list in front of them.
- Bar 1 holds but 2 or 3 fails: **not as built**. A summary up front finds answers but costs too much or breaks
  answers that work today; the follow-up is a shorter summary.
- Bar 1 fails: **no**. A folder summary does not help agents find answers on this corpus, even when they cannot miss
  it. The feature stays in the product, off by default, as it is now.

## Measured, not gated
- Hard questions won and lost against the baseline, with an exact McNemar p-value.
- Tool calls, turns, `search_knowledge` calls and `container_list` calls per question.
- Against CON-160's same-stack baseline (22 hard, 11 control, $0.160) as a drift check.

## Known limits, stated before the run
- One attempt per question; a 4-question gap on 36 is near the noise floor.
- One container, so the summary cannot help the agent choose where to search; it can only tell the agent what the
  records are like (sources, teams, products, vocabulary) before it writes its first query. A corpus with several
  containers is where a folder summary has the most to do, and this test does not cover it.
- The summary is Haiku's from a random sample, not the product rollup's as it runs today; a **yes** says a good
  summary helps, not that today's rollup output does.
