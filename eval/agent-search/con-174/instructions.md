# Instructions that get agents to read the container summary first

Quick test: 10 questions (`quick-questions.json`), folder setup, Sonnet. A candidate is consistent when the first tool
call is `container_describe` on `eval-erb-50k` in at least 9 of 10. Candidates are tried in order; the first consistent
one goes to the full run.

| Candidate | Described first | Cost | Result |
|---|---|---|---|
| describe-v1 | not run | | |
| describe-v2 | not run | | |
| describe-v3 | not run | | |
| describe-v4 | not run | | |
