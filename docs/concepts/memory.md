---
title: About agent memory
description: How Connapse stores the facts an AI agent learns, who can see each fact, how corrections keep history, and why forgetting really deletes.
type: explanation
audience: user
---

# About agent memory

An AI agent forgets everything when its session ends. Agent memory gives it somewhere to keep what it learned, such as which database a service uses or who leads a project, so the next session can start from it. The agent reads and writes memory through five MCP tools, `memory_remember`, `memory_recall`, `memory_correct`, `memory_forget` and `memory_timeline`. Memory has no page in the web UI and no REST endpoint.

## What a fact is

A fact is one statement in three parts: a subject, an attribute and a value, such as `Halcyon`, `database`, `PostgreSQL 15`. Each fact also records:

- **The dates it was true.** `validFrom` is when it became true and `validTo` when it stopped, if it has. An agent can ask what was true on a past date as well as what is true now.
- **Its sources.** These are the documents the agent learned it from: a file, a ticket, a pull request or a code file it found with `search_knowledge`, or a link node.
- **A reason**, such as the ticket that made the change.
- **Its author**, the user or the agent that wrote it.

Memory lives in a container, next to that container's documents. A source can't hold memory, because Connapse doesn't write into the systems sources read from.

## Who sees a fact

A fact is shown only to someone who can read **every** document it cites. Connapse checks this on each read, with the same per-object rules as search, so a fact never tells a reader something its sources would hide from them. If someone loses access to one of the sources, the fact disappears for them too.

The same rule applies when writing: an agent can cite only documents it can read, so it can't attach a private document to a fact to make it look supported.

A fact that cites no sources is seen only by the user who wrote it. For an agent, that is the user the agent acts for. If Connapse can't check access, for example because a cloud provider doesn't answer, reads show nothing and writes are refused rather than guessing.

## How corrections keep history

When a value changes, the agent calls `memory_correct` rather than writing a second fact. It says which kind of change it is:

- **Changed:** the old value was right until a date, and the new value is right from that date. The old fact ends on that date and stays in history, so an agent can still answer "what database did Halcyon use in March?".
- **Wrong:** the old value was never right. It is marked as corrected and the new value takes its dates.

Only a caller who can see every version of a fact can correct it; anyone else can remember a new fact instead. Forgetting deletes every version and needs the caller to see them all, so this rule keeps at least one person able to forget every fact.

`memory_timeline` lists a subject's facts in date order, ended and corrected ones included, so an agent can answer "when did this change, and why?".

## Forgetting deletes

`memory_forget` removes a fact and every earlier and later version of it. Nothing of it is kept, including in history. The audit log records that a fact was written or deleted by its id, never its content, so a deleted fact can't be read back from the log.

## Facts are claims, not instructions

`memory_recall` and `memory_timeline` return the facts as data, with a fixed notice that they are claims to weigh against their sources. An agent should treat a recalled fact the way it treats a search result, as evidence rather than an order. Anyone with `knowledge:write` on a container can write facts there.

## Related

- [Tutorial: connect Claude to Connapse](../get-started/connect-an-agent.md) ends with an agent saving and recalling a fact.
- [MCP tools](../reference/mcp-tools.md#agent-memory) lists each memory tool's parameters and scope.
- [About permissions](permissions.md) explains the per-object checks that decide who sees a fact.
