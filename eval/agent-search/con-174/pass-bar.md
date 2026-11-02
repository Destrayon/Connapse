# CON-174 pass bar

Written and committed on 2026-10-31, before any quick test or scored run. The runs may not change it.

## The question
Does a folder summary help agents answer more questions once their instructions get them to read it before they
search?

"Folder" here is the knowledge container: Connapse writes summaries per container, and `container_describe` returns
one. (erb-50k puts every record in its own numbered folder, so a path-folder summary would be a document summary under
another name.) This tests capability: the instructions are written by hand for this run. Making them easy for users to
set up is a later ticket, which gets the list of instructions that worked (`instructions.md`).

## Why CON-160 did not answer it
- Its agents never called `container_describe` (0 of 48), so the container summary was never read.
- The summary they would have read described 7 chat logs (breakfast tacos, fantasy basketball, a monitor sale) and
  told the agent "all seven documents are shown, so these exclusions are firm." The product's document-clustering
  rollup picks 20 medoids (cluster centres) from all 50,000 records but keeps only those among the first 10,000 it
  lists, which left 7; it then tells the container prompt the container holds 7 documents, all shown. That summary
  misdescribes the container, so this test writes its own (below), and the rollup problem goes to ClaudePM as its own
  ticket.

## The container summary
Written once, before any run, with Connapse's own prompts and Claude Haiku: the per-document prompt
(`SummaryPrompts.PerDocSystemPrompt`) on 20 records drawn at random (seed 174) from the 50,000, leaving out the gold
records of the 48 scored questions so the summary cannot carry an answer; then the container prompt
(`SummaryPrompts.ContainerRollupSystemPrompt`) with Connapse's clustered user message for 50,000 documents. 20 is the
product's `MaxClusters`. An eval-only proxy (`describe_proxy.py`) puts it in `container_describe`'s `Summary:` line;
there is no product change. The text is committed with the results.

## Step 1: quick tests (instructions)
- **Questions:** 10 "semantic" questions from erb-50k that are not among the 48 scored ones (`quick-questions.json`,
  picked by a stable hash), so the instructions are not tuned on the scored set. Answers are not judged.
- **Setup:** the folder setup below, with one candidate set of instructions at a time (`prompts/describe-v*.md`).
- **Consistent** means: in at least **9 of 10** questions the agent's first tool call is `container_describe` on
  `eval-erb-50k`, before any `search_knowledge`, `get_document` or file tool.
- Try candidates in order until one is consistent; stop after 4 candidates or $5 of quick tests, whichever comes
  first. If none is consistent, the full run does not happen and the answer is "agents could not be made to read it."
- Every candidate and its count goes into `instructions.md`.

## Step 2: full run
- CON-146's 48 questions (`../con-146/questions.json`): 36 hard and 12 controls over erb-50k (50,000 generated
  records of one company). Same restored CON-107 index, MinIO loaded so `get_document` works (as in CON-160), same
  agent (Sonnet 5.5 through Claude Code 2.1.295 `claude -p`, at most 25 turns, one attempt per question, one question
  at a time), same judge (CON-142's, with CON-146's one-word change).
- **baseline**: today's tools, exactly CON-146's `files_first` (five tools, `container_describe` hidden), rerun the
  same day.
- **folder**: the same, plus `container_describe` (returning the summary above) and the first consistent
  instructions from step 1, appended to the prompt.
- Cost: `total_cost_usd` that Claude Code reports per question (agent only). Writing the summary (about $0.03) and the
  quick tests are reported, not gated.

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

The full run also reports how many of the 48 called `container_describe` first. If fewer than 43 (90%) did, the
result says the instructions did not hold on the scored set, and the verdict stands but is marked so.

## What each outcome means
- All three hold: **yes**. With the right instructions, a folder summary helps; a follow-up fixes the rollup so the
  product writes a summary like this one and makes the instructions easy to set up.
- Bar 1 holds but 2 or 3 fails: **not as built**. Reading the summary finds answers but costs too much or breaks
  answers that work today; the follow-up is a shorter summary or a narrower instruction.
- Bar 1 fails: **no**. Even when agents read it, a folder summary does not help them find answers on this corpus.
  The feature stays in the product, off by default.

## Measured, not gated
- Hard questions won and lost against the baseline, with an exact McNemar p-value.
- Tool calls, turns, `search_knowledge` calls per question.
- Against CON-160's same-stack baseline (22 hard, 11 control, $0.160) as a drift check.

## Known limits, stated before the run
- One attempt per question; a 4-question gap on 36 is near the noise floor.
- One container, so the summary cannot help the agent choose where to search; it can only tell the agent what the
  records are like (sources, teams, products, vocabulary) before its first query. Several containers is where a
  folder summary has the most to do, and this test does not cover it.
- The folder setup differs from baseline in both the summary and the instructions. That is the capability being
  tested: the summary is only worth anything once instructions get it read.
- The summary is Haiku's from a random sample, not the product rollup's as it runs today; a **yes** says a good
  summary helps, not that today's rollup output does.
