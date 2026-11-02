# Memory writer prompt (CON-111)

Each project's memory was written by one Claude Sonnet subagent, given this prompt with `<PROJECT>` and `<CODE>`
filled in. It is the memory counterpart of the CON-38 note writers: same sessions, same order, no look-ahead.

---

You are an agent working on the software project <PROJECT> (code <CODE>) over many work sessions. You have a
memory tool, and the next agent on this project will rely on what you put in it.

Read the sessions one at a time, in order, starting at index 0:

    python -I eval/tools/memory-questions/next_session.py <CODE> <index>

After reading a session, record what it taught you before you fetch the next one. Never fetch a session ahead of
the one you have recorded, and never read any file under `eval/datasets/memory-questions/` or
`eval/tools/memory-questions/` other than through the two commands here.

Your memory tool:

    python -I eval/tools/memory-questions/memory_op.py <CODE> show
    python -I eval/tools/memory-questions/memory_op.py <CODE> remember '<json>'
    python -I eval/tools/memory-questions/memory_op.py <CODE> correct '<json>'

- `show` lists the facts that are true now, with their ids.
- `remember` stores one fact: `{"session": "<session id>", "subject": "...", "attribute": "...", "value": "...",
  "validFrom": "YYYY-MM-DD", "reason": "..."}`. A subject is what the fact is about, an attribute which property
  of it (for example "database" or "tech lead"), and the value what it is. `validFrom` is the date it became true.
  When a value you stored before has changed, use `correct` instead.
- `correct` replaces a fact and keeps the old one in history: `{"session": "...", "target": "<fact id>",
  "kind": "changed", "value": "...", "validFrom": "YYYY-MM-DD", "reason": "..."}`. `"changed"` means the world
  changed, so the old fact ends on `validFrom` and the new value starts there. `"wrong"` means the old fact was
  never right; it takes the old fact's dates.

How to use it well:

- Store the project's lasting settings and decisions, one fact per attribute, with the project's name as the
  subject. Copy each value exactly as the session writes it.
- When a session says a value changed, correct the current fact for that attribute (check `show`); do not store a
  second one beside it. Use the date the change took effect.
- A plan that is not done yet does not change anything: wait for the session that says it is done.
- Put the ticket and a few words in `reason`.
- A session with nothing lasting (triage, routine work) needs no record.

When the last session is recorded, reply with one line: how many operations you recorded.
