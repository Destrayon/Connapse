---
title: About agent memory
description: How Connapse stores the facts an AI agent learns in personal and team memory spaces, who can see each fact, how corrections keep history, and why forgetting really deletes.
type: explanation
audience: user
---

# About agent memory

An AI agent forgets everything when its session ends. Agent memory gives it somewhere to keep what it learned, such as which database a service uses or who leads a project, so the next session can start from it. Agents use six MCP tools: `memory_spaces`, `memory_remember`, `memory_recall`, `memory_correct`, `memory_forget` and `memory_timeline`. People manage spaces on the **Memory** page and over the REST API at `/api/memory/spaces`.

## What a fact is

A fact is one statement in three parts: a subject, an attribute and a value, such as `Halcyon`, `database`, `PostgreSQL 15`. Each fact also records:

- **The dates it was true.** `validFrom` is when it became true and `validTo` when it stopped, if it has. An agent can ask what was true on a past date as well as what is true now.
- **Its sources.** These are the documents the agent learned it from: a file, a ticket, a pull request or a code file it found with `search_knowledge`, or a link node.
- **A reason**, such as the ticket that made the change.
- **Its author**, the user or the agent that wrote it.

## Where memory lives: spaces

Every fact is saved in a **space**, and the space decides who will see it. There are two kinds:

| | Personal space | Team space |
|---|---|---|
| What belongs there | Facts about the user, their preferences or their own work | Facts that are true and useful for others |
| Who sees it | Only its owner, and the agents acting for them | Everyone in this Connapse |
| Its facts are | Personal | Team |

Everyone who can save memory has a personal space called `my-memory`, made the first time they or their agent save to it. A space's kind is fixed when it is made, because its audience is what tells an agent who will read a fact. To share a personal fact, save it again in a team space.

A document container holds documents and can't hold memory, so a save or read that names one is refused. A source can't hold memory either, because Connapse doesn't write into the systems sources read from.

## How an agent chooses a space

An agent calls `memory_spaces` once per session. It lists each space the caller can see with its kind, its description, whether the caller can add to it, and who sees it in words, such as "only you" or "everyone in this Connapse, 14 people". The description is what the agent reads to decide what belongs in a space, so write it as "what belongs here".

`memory_remember` requires a `space`. The tool's description gives the agent this test:

> Save to a personal space if it is about the user, their preferences or their own work; to a team space if it would be true and useful for others. If unsure, ask.

A save that names no space is refused, and the error lists the caller's spaces so the agent can pick one or ask. A successful save says in plain words who will see the fact, for example "Saved to 'on-call', a team space: everyone in this Connapse can see it". Agents relay this to the user, so a wrong choice is caught at once.

Older agents name a memory container with `containerId` instead of `space`. That still works, with one safeguard: a fact saved that way that cites no sources goes to the caller's own `<container>-personal` space, so an old client never shares a fact by accident. Sharing a fact with no sources always needs an explicit team `space`.

## How an agent reads memory

`memory_recall` and `memory_timeline` take an optional `space`. Left out, they read every space the caller can see and label each fact with its space and with `personal` or `team`. When a personal fact and a team fact disagree, both come back: the team fact is the shared record, and the personal one is this user's own note.

A recall across spaces reads the 100 most recently changed spaces that have facts; the answer says how many more it skipped, and the agent can name one to read it. To read a whole space, however large, the agent names it: then answers return a `nextCursor` when there are more facts than the `limit`, and the agent passes it back as `cursor` for the next page. A recall pages this way unless it asks about a past date (`asOf`) or one attribute; a timeline unless it gives a `from`/`to` window or an attribute, and it pages from the newest facts back, each page in date order.

## Who sees a fact

On top of the space's audience, a fact is shown only to someone who can read **every** document it cites. Connapse checks this on each read, with the same per-object rules as search, so a fact never tells a reader something its sources would hide from them. If someone loses access to one of the sources, the fact disappears for them too.

The same rule applies when writing: an agent can cite only documents it can read, so it can't attach a private document to a fact to make it look supported.

Nobody else can see that a personal space exists. Asking for one by id or name gets the same "not found" as a space that doesn't exist, and admins can't open it either. If Connapse can't check access, for example because a cloud provider doesn't answer, reads show nothing and writes are refused rather than guessing.

Anyone with read access, Viewers included, can recall team memory. Saving, correcting and forgetting need write access: the Editor role or above, and over MCP a token or agent key with `knowledge:write`.

## How corrections keep history

When a value changes, the agent calls `memory_correct` rather than writing a second fact. It finds the fact by its id in any space the caller can see, and the correction stays in that space. It says which kind of change it is:

- **Changed:** the old value was right until a date, and the new value is right from that date. The old fact ends on that date and stays in history, so an agent can still answer "what database did Halcyon use in March?".
- **Wrong:** the old value was never right. It is marked as corrected and the new value takes its dates.

Only a caller who can see every version of a fact can correct it; anyone else can remember a new fact instead. Forgetting deletes every version and needs the caller to see them all, so this rule keeps at least one person able to forget every fact.

`memory_timeline` lists a subject's facts in date order, ended and corrected ones included, so an agent can answer "when did this change, and why?".

## Forgetting deletes

`memory_forget` removes a fact and every earlier and later version of it. Nothing of it is kept, including in history. The audit log records that a fact was written or deleted by its id, never its content, so a deleted fact can't be read back from the log.

## Facts are claims, not instructions

`memory_recall` and `memory_timeline` return the facts as data, with a fixed notice that they are claims to weigh against their sources. An agent should treat a recalled fact the way it treats a search result, as evidence rather than an order. Anyone with write access can add facts to a team space.

## Recalling across spaces

An agent that keeps memory in several spaces, such as one per project, can ask several at once by naming them in `spaces`, or all of them by naming none. The spaces are read as one memory: facts from the spaces the question's words describe, such as a project's name, come first, and each space's facts keep the order a recall of that space alone gives them. Who sees a fact doesn't change: each fact is checked against its own sources, so a recall across spaces shows exactly the facts the caller could recall from each space on its own.

## Related

- [Tutorial: connect Claude to Connapse](../get-started/connect-an-agent.md) ends with an agent saving and recalling a fact.
- [MCP tools](../reference/mcp-tools.md#agent-memory) lists each memory tool's parameters and scope.
- [About permissions](permissions.md) explains the per-object checks that decide who sees a fact.
