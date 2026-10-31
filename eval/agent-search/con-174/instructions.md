# Instructions that get agents to read the container summary first

Quick test: 10 questions (`quick-questions.json`), folder setup, Sonnet. A candidate is consistent when the first tool
call is `container_describe` on `eval-erb-50k` in at least 9 of 10. Candidates are tried in order; the first consistent
one goes to the full run.

| Candidate | Described first | Cost | Result |
|---|---|---|---|
| describe-v1 | 10/10 (one call each) | $1.10 | consistent; used for the full run |
| describe-v2 | not run | | v1 already passed |
| describe-v3 | not run | | v1 already passed |
| describe-v4 | not run | | v1 already passed |

The instruction that worked, appended to CON-146's system prompt as its own section
([prompts/describe-v1.md](prompts/describe-v1.md)):

> ## The container summary
> Before your first search, call `container_describe` with containerId `eval-erb-50k`. It returns a summary of what
> the records hold (their sources, teams, products and vocabulary) and how to phrase queries that find them. Use it to
> plan your searches. Call it once per question.

The mildest wording was enough. It names the tool and the container, says when to call it (before the first search),
and says what the agent gets from it. Nothing had to override the server's own routing rules, which tell agents to
search a named container directly. In CON-160, with no such instruction, none of 48 runs called `container_describe`.
