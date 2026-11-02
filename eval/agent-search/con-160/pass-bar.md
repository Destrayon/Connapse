# CON-160 pass bar

Written and committed on 2026-10-31, before any scored run. The run may not change it.

## The question
Do document and container summaries help agents find answers they miss today?

## What runs
- CON-146's 48 questions (`../con-146/questions.json`): 36 hard ("semantic" questions whose words overlap their gold
  records least) and 12 controls, over EnterpriseRAG-Bench's erb-50k subsample (50,000 generated records of one
  company). Same restored CON-107 index, same agent (Sonnet 5.5 through Claude Code 2.1.295 `claude -p`, at most 25
  turns, one attempt per question, one question at a time), same judge (CON-142's, with CON-146's one-word change).
- Two setups, both with all five of CON-146's tools and CON-146's `files_first` prompt:
  - **baseline**: today's tools, exactly CON-146's `files_first`. Rerun on the same day as the summaries setup so
    both see the same stack, model and Claude Code version.
  - **summaries**: the same, plus the two summaries Connapse already knows how to make:
    - **Document summaries.** Each `search_knowledge` hit carries a `Document summary:` line, the description of its
      whole record. It is written by Connapse's own per-document prompt (`SummaryPrompts.PerDocSystemPrompt`, same
      truncation) with Claude Haiku, the first time a record appears in any result, and cached. The agent sees only
      summaries of records its searches return, which is exactly what it would see if all 50,000 had been
      summarized ahead of time.
    - **Container summary.** `container_describe` is allowed (CON-146 hid it) and returns the container summary that
      Connapse's own rollup job writes (document-clustering, the default method), with Haiku as the LLM.
  - The summaries prompt adds two sentences saying what these are, and no advice on when to use them.
- Both setups run against a stack whose MinIO holds the original records. CON-146's stack had an empty MinIO, so all
  120 of its `get_document` calls returned "could not be read from storage"; here `get_document` works, as it does
  in production, in both setups.
- Cost: `total_cost_usd` that Claude Code reports per question (agent only). Generating summaries is a one-time
  ingest cost; it is reported separately, with a projection for all 50,000 records, and not gated.

## Definitions
- **Missed today**: a hard question the baseline gets wrong.
- **Already right today**: a question (hard or control) that both CON-146's `files_first` run and this baseline run
  answered correctly. Requiring both runs keeps one-attempt luck out of the set. CON-146's `files_first` got 31
  right: qst_0001, 0024, 0034, 0042, 0046, 0055, 0067, 0078, 0081, 0083, 0091, 0108, 0178, 0191, 0193, 0196, 0213,
  0214, 0235, 0240, 0244, 0245, 0248, 0251, 0255, 0268, 0270, 0273, 0282, 0289, 0299.
- **Dropped**: an already-right question that the summaries setup gets wrong. Because one attempt is noisy, each
  already-right question the summaries setup misses is asked twice more under the summaries setup; it counts as
  dropped only if it is wrong in at least 2 of its 3 attempts.

## The bar
Summaries help (**yes**) only if all three hold against the same-day baseline:
1. **Finds more:** at least 4 more of the 36 hard questions right (10 percentage points), the same margin CON-146
   used.
2. **Cost:** average agent cost per question over all 48 at most 20% above the baseline's.
3. **No drop:** zero dropped questions.

## What each outcome means
- All three hold: **yes**. Show document summaries in `search_knowledge` hits and let agents call
  `container_describe`; a follow-up ticket decides how summaries get generated at ingest and what that costs.
- Bar 1 holds but 2 or 3 fails: **not as built**. Summaries find answers but cost too much or break answers that
  work today; the follow-up is a narrower form (for example only the container summary, or shorter summaries).
- Bar 1 fails: **no**. Summaries do not help agents find what they miss today; leave them out of the agent's tools.

## Measured, not gated
- Hard questions won and lost against the baseline, with an exact McNemar p-value.
- Against CON-146's `files_first` numbers (19 hard, 12 control, $0.204), as a check on run-to-run drift.
- How often the agent calls `container_describe`, tool calls and turns per question.
- Haiku cost of every summary written, and the projected cost of summarizing all 50,000 records.

## Known limits, stated before the run
- One attempt per question; a 4-question gap on 36 is near the noise floor (CON-146's +3 had p = 0.45).
- Summaries are Haiku's, not a local model's. A weaker summary model could only make summaries less useful, so a
  **no** here holds for weaker models too; a **yes** may not.
- Document summaries help the agent choose which hits to read; they are not indexed for search here. Searching
  summaries themselves would need all 50,000 written up front and is a different question.
