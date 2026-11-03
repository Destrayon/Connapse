# Plan: personal and team memory in one Memory tab (CON-233)

**Status:** plan only, nothing built. Research: [personal-team-memory-2026-11-02.md](../research/personal-team-memory-2026-11-02.md).
Plain-words examples: [con-233-memory-on-a-container-explained.md](con-233-memory-on-a-container-explained.md).

## The problem

Today a fact's audience is a side effect. A fact that cites documents is shown to everyone who can read all of
them; a fact that cites none is shown only to its author. So an agent can't say "keep this to myself" about a
fact it learned from a shared document, and it can't share a team fact that has no document behind it, such as
"Friday deploys need Sam's sign-off". And memory is looked after in two places, each container's Memory tab and
agent memory containers, with a feared third for personal memory.

## The model: every fact is personal or team

Each fact gets a **scope**, stored on each version's row and carried through corrections.

| | Personal | Team |
|---|---|---|
| Who sees it | Its author, meaning the user and the agents acting for that user (an agent with no user: that agent) | Everyone who can read the container and every document the fact cites |
| Cited documents | Still must all be readable by the author | Still must all be readable by each reader, so a fact never reveals what its sources hide |
| Who can write it | Only people and agents with write access (`knowledge:write`) | Only people and agents with write access (`knowledge:write`) |
| Who can recall it | Its author, with read access | Anyone with read access who passes the rule below |
| Typical facts | "I prefer short answers", "I'm rewriting the onboarding guide this week" | "Halcyon uses PostgreSQL 15", "deploys freeze on the last Friday of the quarter" |

The rule on every read becomes: the caller can read the container, every cited source is readable, and the fact
is either team or the caller's own. This keeps the 2026-10-27 all-sources rule as a ceiling and only adds the
scope check on top, so no fact becomes visible to anyone who couldn't see it before.

## How agents choose, on purpose

Models misjudge what is private in a quarter to half of cases even when told to be careful (ConfAIde,
PrivacyLens), so the design makes the choice explicit, visible and reversible rather than trusting it.

1. **`memory_remember` takes a required `scope`: `personal` or `team`.** No default. A call without it is refused
   with an `invalid-memory` error that names both choices, so the agent has to decide. One tool with a parameter,
   not two tools, per Anthropic's and OpenAI's tool guidance.
2. **The description gives the test Copilot uses:** "Team if it would be true and useful for anyone working in
   this container; personal if it is about you, your preferences or your own work. If you aren't sure, ask the
   user. Team facts are seen by everyone who can read the container and every document the fact cites."
3. **The answer says who will see it, in words:** "Saved for the team: everyone who can read *Halcyon docs* can
   see it" or "Saved as personal: only you see it." Agents relay this, so a wrong guess is caught at once.
4. **Recall labels every fact** with `scope` and accepts an optional `scope` filter (`personal`, `team`, `all`,
   default `all`). When a personal and a team fact disagree on the same subject and attribute, both come back and
   the description says: the team fact is the shared record; the personal one is this user's own note or
   preference. Connapse ranks neither, because products disagree (GitHub puts personal first, Cursor team first).
5. **`memory_correct` keeps the old fact's scope and, when `sources` is omitted, its sources.** Today an omitted
   list means "no sources", which for a team fact would drop its document restriction and show the new value to
   every container reader. So omitted sources are inherited, and a correction of a team fact whose source list
   leaves out any of the old fact's sources is refused, with an error saying to forget the fact and save a new
   team fact if the wider audience is intended. Changing scope is not a correction either. A correction to a team
   fact needs write access, as today, and so does a correction to a personal one.

Agents don't get a "share" tool in this plan. Turning a personal fact into a team fact is a person's action in
the Memory tab (below); if a user tells an agent to share something, the agent saves it again as a team fact.

## One Memory tab for both

There is no separate page for personal memory. The existing container Memory tab (CON-225) shows both kinds:

- **A Mine / Team / All filter** next to Current / All, defaulting to All, like Google Drive's owner filter.
- **A scope label on every row:** a lock with "Only you" (already shown today for sourceless facts) or a people
  icon with "Team".
- **The detail panel shows the scope** and offers **Share with team** on your own personal facts (needs write
  access) and **Make personal** on team facts. Each opens a confirmation that names the audience and the
  consequence: "Everyone who can read Halcyon docs and the documents each version cites, about 14 people, will see this fact
  and its history" or "Other
  people and their agents will stop seeing this fact." The change applies to the whole correction chain and is
  recorded in the audit log by id, as writes are.
- **A scope change needs you to have written every version in the chain.** A legacy chain can mix authors, for
  example one person's sourceless (so private) correction of another's sourced fact, so sharing one version could
  publish someone else's private one. Storage checks the complete chain under the same lock corrections take, not
  the Memory tab's history list, which hides versions you can't see and stops at 100. A mixed-author chain is
  refused with a message naming why.
- **Anyone with write access can delete a team fact they can see**, as `memory_forget` allows today, which is how
  a poisoned or wrong shared fact is removed (the risk the MINJA and AgentPoison papers show). Personal facts are
  never shown to anyone else, owners included.

## What happens to what exists today

- **Container memory (the Memory tab):** stays, and becomes the one place both kinds are managed.
- **Existing facts migrate with no change in who sees them:** each version that cites sources becomes team (it is
  already shown to readers of its sources), and each version that cites none becomes personal (it is already shown
  only to its author). The migration sets the column per version, so mixed chains keep each version's audience;
  no fact moves.
- **Agent memory containers (CON-227) stay, but stop being a second way to manage memory.** They are an ordinary
  container whose page is the same Memory tab, with the same filter and labels. Their job is a home for team facts
  that belong to no document set, such as on-call rules or "how we run releases". The create dialog's note says
  that, and that personal facts belong in whichever container they are about.
- **No third, global "my memory" page.** Preferences that apply across all projects belong in the agent client's
  own memory (for example a user `CLAUDE.md`) or, if the user wants them in Connapse, in a memory container they
  create and keep personal. This is what keeps memory to one tab.

## Work to do, one ticket per PR

1. **Scope in storage and rules.** Add the `scope` column and migration (sourced facts to team, sourceless to
   personal), the new visibility rule, and the write-access check for saving either scope.
   Done when tests show every existing fact keeps its exact audience, a personal fact citing a public
   document stays private, a correction of a team fact citing a restricted document stays restricted, and
   sharing a mixed-author chain is refused.
2. **Scope in the MCP tools and REST API.** Required `scope` on remember, `scope` on recall and timeline
   answers and filters, the plain-words answer, updated tool descriptions, `docs/reference/mcp-tools.md` and
   `docs/problems.md`. Done when an agent omitting `scope` gets an error naming both choices.
3. **Memory tab.** Mine / Team / All filter, scope labels, Share with team and Make personal with their
   confirmations. Done when a viewer sees team facts plus only their own personal
   ones, and the confirmation names the audience.
4. **Docs.** Rewrite `docs/concepts/memory.md` around the two scopes (it still says memory has no web page, which
   CON-225 made untrue) and add the everyday examples. Done when the concept page matches the tools.

## Open questions for the user

- ~~Should a reader without write access be able to keep personal facts?~~ Answered 2026-11-03: no. Every saved
  fact costs storage and embedding compute, so saving any memory needs write access. Read-only users and keys
  can still recall and search the team facts they are allowed to see.
- Should owners see a count of other people's personal facts before deleting a container? Today the delete dialog
  says facts are deleted "including facts other people's agents saved", which would now include private ones.

## Progress

- **Phase 1 (CON-234), 2026-11-03.** Every fact version now has a `scope` column (personal or team). The
  `AddMemoryScope` migration makes versions that cite sources team and the rest personal, so no audience changes.
  Corrections keep the old scope and every old source of a team fact. Changing scope (`memory.setScope`) needs the
  author of every version. Saving, correcting or forgetting any fact needs write access; read-only users and keys
  only recall. Files touched:
  `MemoryModels`, `IMemoryStore`, `PostgresMemoryStore`, `MemoryService`, `MemoryOperations`, `McpTools.Memory`,
  plus the migration. Verified by unit, random-input and integration tests, including a migration test that
  compares every reader's view before and after. Next: phase 2, a required `scope` in the MCP tools and REST API.
