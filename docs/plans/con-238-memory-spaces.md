# Plan: memory spaces on a Memory page (CON-238)

**Status:** plan only, nothing built. Replaces [con-233-personal-team-memory.md](con-233-personal-team-memory.md).
Research: [memory-spaces-2026-11-03.md](../research/memory-spaces-2026-11-03.md), building on
[personal-team-memory-2026-11-02.md](../research/personal-team-memory-2026-11-02.md).
Plain-words version: [con-238-memory-spaces-explained.md](con-238-memory-spaces-explained.md).

## What changed from CON-233, and why

CON-233 kept all memory inside containers and let each fact be personal or team. The user decided against that:
"I'd prefer if memory was a tab in the nav rather than tied to containers", and "only container level memory can
be tied to containers, which are team only, but for regular team and personal memories, you create memory
'containers' in the memory tab."

So the choice moves from each fact to the place it is saved. You make **memory spaces** on a new **Memory** page in
the nav, and each space is personal or team. A document container can still hold memory, but only team memory.
An agent no longer picks a scope for each fact; it picks a space, and the space says who will see it.

These points from the user still stand: read-only users can't save memory, because saving costs compute, but they
can search team memory; personal and team must be obvious to agents; and memory is managed in one way, not three.

## The three kinds of place memory lives

| | Personal space | Team space | Memory on a document container |
|---|---|---|---|
| Made where | Memory page, **New space** → Personal | Memory page, **New space** → Team | Comes with every document container |
| Who sees it exists | Only its owner | Everyone with an account | Everyone with an account, as today |
| Who can search it | Its owner and the agents acting for them | Everyone with an account, Viewers included | Everyone with an account, Viewers included |
| Who can add, correct or forget | Its owner, while they have write access | Editors, Admins and Owners | Editors, Admins and Owners |
| Every fact is | Personal | Team | Team |
| Fact trimming | A fact citing documents is still shown only to people who can read all of them (the rule since 2026-10-27) | Same | Same |

"Write access" is the Editor role or above plus a token with `knowledge:write`, as PR #1123 already enforces.

**Personal or team is fixed when the space is made.** Products split here. Microsoft Loop and Confluence keep
personal and shared spaces as different kinds that can't be converted (Loop's "My workspace" can't be shared at
all), while ChatGPT, Claude, Notion and Basic Memory widen a project or page later by sharing it. Fixed is the
better fit for Connapse: a space's audience is what tells an agent who will read a fact, so it shouldn't change
under facts already saved for a different audience. A fact that should be shared is saved again in a team space. This removes the per-fact "Share with team" and "Make
personal" actions CON-233 planned, and the hard cases they had (mixed-author chains, legacy versions).

**Every Editor gets a personal space called `my-memory`**, made the first time they open the Memory page or an
agent saves personal memory for them. This is Loop's "My workspace" and Notion's Private section: there is always
somewhere personal to save, so an agent never has to stop and ask the user to create one. People can make more.
Viewers get none, since they can't save. Someone demoted to Viewer keeps reading their own personal spaces.

**A team space is shared with everyone the moment it is made.** Connapse has no per-container permissions (roles
apply to every container, `docs/concepts/permissions.md`), so a team space follows the same rule: every account can
search it, and the role decides who can add. There is no invite step. The create dialog says this in words, with the
number of people. A team space limited to named members would be Connapse's first per-container permission; it is
left as an open question below, not built here.

**Memory on a document container is team only.** It stays in the container's Memory tab (CON-225), labelled "Team
memory", and the Memory page lists every container that has some. It is for facts learned from that container's
documents. Saving personal memory there is refused, with an error that names the caller's personal spaces.

## Pictures

The Memory page, as Pat (an Editor) sees it. Personal spaces, team spaces and document containers with memory are
three labelled sections, like Notion's Private and Teamspaces sections.

![The Memory page](../images/memory-spaces/memory-page.png)

Making a space. The audience is the first question, and the note spells out who will see a team space.

![The New memory space dialog with Team chosen](../images/memory-spaces/create-space.png)

A team space as Dana, a Viewer: she can search it but not add, and a fact citing a document is still shown only to
people who can read that document.

![A team space as a read-only viewer](../images/memory-spaces/team-space-viewer.png)

A document container's Memory tab after the move: team facts only, with a pointer to personal spaces.

![A document container's Memory tab showing team memory only](../images/memory-spaces/container-memory-tab.png)

## Who can do what on the Memory page

- **Create a space:** Editors and above. Viewers see team spaces and container memory but no **New space** button.
- **Rename, edit the description, delete:** a personal space by its owner; a team space by Editors and above, the
  same people who can delete a container. Deleting a team space names how many facts and authors go with it.
- **Admins can't open someone else's personal space.** They don't see that it exists, as with personal facts today.
  When an account is deleted, its personal spaces are deleted with it.
- **The description matters:** the dialog asks "What belongs here" and says agents read it to choose a space.

## How agents choose where to save and where to search

Models misjudge what is private in a quarter to half of cases (ConfAIde, PrivacyLens), so the choice is explicit,
named in plain words and checked by a person after the fact, as in CON-233. What changes is that the agent chooses
a space, and a space has one audience.

1. **A new read-only tool, `memory_spaces`,** lists the spaces the caller can see: name, id, kind (`personal`,
   `team` or `container`), description, whether the caller can add to it, and who sees it in words ("only you",
   "everyone in this Connapse, 14 people"). Agents call it once per session, as Claude Code reads `CLAUDE.md` once.
2. **`memory_remember` takes a required `space`**, a space or container name or id. Basic Memory's MCP server works
   the same way: every tool takes a `project`, and a call that doesn't name one is refused when several exist. The old `containerId` parameter
   keeps working as an alias for one release. There is no `scope` parameter: the space decides it, which replaces
   the required `scope` CON-233 planned. The description gives the test GitHub Copilot uses: "Save to a personal
   space if it is about the user, their preferences or their own work; to a team space if it would be true and
   useful for others; to a document container if you learned it from that container's documents. If unsure, ask."
3. **The answer says who will see the fact**, as CON-233 planned: "Saved to *on-call*, a team space: everyone in
   this Connapse can see it" or "Saved to *my-memory*: only you see it." Agents relay this, so a wrong choice is
   caught at once.
4. **`memory_recall` and `memory_timeline` take an optional `space`.** Left out, they search every space and
   container the caller can see, and each fact comes back labelled with its space and `personal` or `team`. When a
   personal and a team fact disagree, both come back and the description says the team fact is the shared record
   and the personal one is this user's own note. Viewers' agents can recall; they just can't save.
5. **`memory_correct` and `memory_forget`** find the fact by id, so `space` is optional there. A correction stays
   in the fact's space, keeps its scope and, when `sources` is left out, its sources (PR #1123).
6. **`search_knowledge` and `container_list`** never show another user's personal space. Asking for one by id or
   name gets the same "not found" as a space that doesn't exist, so nobody can probe for one.

A name used by both one of your personal spaces and a team space or container is refused as ambiguous, with both
ids, because personal space names are only unique per owner. Using the id always works.

## How today's memory moves over, with nobody gaining or losing access

PR #1123's migration has already given every fact version a scope that matches who could see it: versions citing
sources are team, versions citing nothing are personal. This plan's migration moves facts between places and never
changes a version's scope, so the visibility rule (every source readable, and team or yours) gives every reader the
same answer before and after.

- **Team facts on document containers** stay where they are.
- **Personal facts on document containers** move, whole chain at a time, to a personal space for their author named
  after the container, such as `halcyon-project-personal`, with the description "Moved from halcyon-project's
  Memory tab on <date>". A fact written by an agent with no user moves to a space owned by that agent.
- **Agent memory containers (CON-227)** become team spaces with the same id, name and description, so agents and
  tokens that name them keep working. Their personal facts move out to the author's personal space the same way.
  Their team facts stay. The Create Container dialog loses its "Agent memory" type; spaces are made on the Memory
  page.
- **A chain that mixes personal and team versions** (only possible from before the scope column, when one person
  corrected another's fact without sources) can't move without splitting its history. It stays where it is, each
  version keeping its scope, and its personal versions show to their author with an "Only you (older fact)" label.
  The migration logs how many there are; we expect very few.
- **Container ids and fact ids don't change.** Only a moved fact's container id changes. An agent that recalls from
  `halcyon-project` by name no longer gets its old personal notes there, but a recall without `space` finds them.

The done-condition is a test that seeds every case above, runs the migration, and compares every reader's view of
every fact before and after, extending PR #1123's `MemoryScopeMigrationTests`.

## What changes in open PR #1123 (CON-234)

Most of it is reused as it is:

- **Keep:** the `scope` column and `AddMemoryScope` migration; the visibility rule; write access for remember,
  correct and forget whatever the scope; read-only users and keys recalling team facts; corrections keeping the
  old scope and inheriting omitted sources; refusing a team correction that drops a source; and the tests for all of
  these. Spaces build on exactly this: a space just decides which scope new facts get.
- **Remove:** the `memory.setScope` operation, its sole-author check and their tests. A fact's scope no longer
  changes after it is saved; to share, save again in a team space. Nothing uses it yet, so nothing breaks.
- **Remove:** the PR's edits to `docs/plans/con-233-personal-team-memory.md`, which this plan supersedes. Its
  progress notes belong in this plan's Progress section once it merges.
- **Update the PR body:** drop the references to a Mine / Team / All filter in a container's Memory tab (CON-236 as
  planned before). The memory-scope pictures stay valid for what the PR does.
- **Unchanged until CON-235:** when `scope` is left out, sources still decide it, as the PR does now.

## Revised build tickets, in order

One ticket per PR. Each needs the one before it merged.

1. **CON-234: scope in storage and rules (PR #1123).** As above, minus `memory.setScope`. Done when the PR's tests
   pass with the removal and it merges.
2. **CON-235: memory spaces in storage and REST.** Add audience (`personal` or `team`) and owner to memory
   containers; personal space names unique per owner; `my-memory` made on first use; REST endpoints to list,
   create, rename and delete spaces; new facts take their scope from the space (personal space: personal; team space
   or document container: team); personal saves on a document container refused; personal spaces hidden from
   every other caller in container lists, lookups by id and name, search and the admin pages. Done when tests show
   a non-owner gets "not found" for a personal space by id and by name on every surface, and a Viewer can recall
   from a team space but not save to it.
3. **CON-236: move today's memory into spaces.** The migration in the section above. Done when the before-and-after
   test shows every reader sees exactly the same facts, and agent memory containers open as team spaces with
   their old ids.
4. **CON-237: MCP tools and docs.** `memory_spaces`; required `space` on remember (with the `containerId` alias);
   optional `space` and labels on recall and timeline; the plain-words answers; tool descriptions;
   `docs/reference/mcp-tools.md`, `docs/problems.md`, and a rewrite of `docs/concepts/memory.md` around spaces (it
   still says memory has no web page). Done when an agent that leaves out `space` gets an error that lists its
   spaces, and the concept page matches the tools.
5. **New ticket: the Memory page.** The nav entry, the page, the New space dialog, a space's view (the CON-225
   fact table, with an Author column for team spaces and the Viewer notice), the "Team memory" label and pointer on
   a container's Memory tab, and removing the "Agent memory" type from the Create Container dialog. Done when the
   pages match the pictures above for an Editor and a Viewer.

CON-235 is the largest. If it passes the PR size limit, it splits into storage and rules first, then REST, and the
split goes to the PM before coding.

## Open questions for the user

- **Team spaces for some people only?** This plan shares every team space with every account, the same as
  containers. Limiting one to named members (Notion's closed teamspaces, Confluence space permissions) would be
  Connapse's first per-container permission and its own epic. Say if you want it.
- **Should a personal fact be copyable into a team space from the Memory page?** No product we looked at has a
  "promote" action; the plan relies on saving again. A copy button would be small to add later.

## Progress

- **CON-238, 2026-11-03.** This plan, its explainer, the research report and four mockup pictures in
  `docs/images/memory-spaces/`. No product code. Next: CON-234 (PR #1123) drops `memory.setScope` and merges.
