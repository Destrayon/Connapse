# memory-agent

- **What it tests:** whether an agent can find and save facts in its own memory space when the question is worded
  differently from the fact (CON-313). Scored by the `memory-agent` command; the bar is in `eval/pass-bars.md`.
- **Built by** `python -I eval/tools/build_memory_agent.py eval/datasets/memory-agent`. The facts and tasks are written
  by hand in the script, which has no randomness, so the output is a pure function of the file. The script stops if a
  task breaks its kind's wording rule.
- **Facts** (`facts.jsonl`, 245): what a project manager at the made-up company Orrin Systems keeps, as subject,
  attribute and value: projects, people, services, rules for agents, budgets and schedules. 95 are hand-written and
  10 of those carry an older value with the dates it held (`history`), saved first and then corrected. 150 are filler
  about 30 made-up services (owner, repository, language, deploy day, alerts channel), so the space is as crowded as
  a real one. Every name is made up; none come from a real project manager's memory.
- **Find tasks** (`type: find`): a question, the gold fact keys (none for absent), and the answer.

  | Kind | Wording rule (checked by the script) | Test | Dev |
  |---|---|---|---|
  | exact | names the fact's subject and attribute | 10 | 2 |
  | reworded | names the subject but shares no content word with the attribute or value | 31 | 3 |
  | indirect | shares no content word with the fact at all | 15 | 2 |
  | partial | its only link to the fact is a short form of one of its words ("tix" for "ticket") | 10 | 1 |
  | absent | memory holds no fact with that subject and attribute | 8 | 2 |

- **Save tasks** (`type: save`): what the user tells the agent, the fact it touches (`target`, none for new), and word
  groups the saved fact must hold, one word of each group.

  | Kind | What memory holds | Test | Dev |
  |---|---|---|---|
  | new | nothing about it | 10 | 2 |
  | update | an older value of it | 8 | 1 |
  | duplicate | the same fact, worded differently | 6 | 1 |
