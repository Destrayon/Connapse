# How should a person see and fix what Connapse agents remember?
**Date:** 2026-11-02
**Status:** Reviewed
**Built on:** agent-memory-design-2026-10-27.md (the memory backend this UI sits on).

## Executive summary
The surveyed AI products show and edit memory as a list or table with a delete action on each row, but none of
them shows a source or a date for each memory, and none lets a person browse a memory's history. Connapse facts
carry all three: sources, two kinds of date, and a correction chain. That part of the Memory tab therefore has no
product to copy and is designed from general UX guidance. The tab follows Connapse's own file browser: a table, a
detail side panel, and confirmation modals. On top of that it adopts a Current/All filter (from Mem0's archived
toggle), corrections that close the old fact instead of overwriting it (from Zep and Notion's `supersedes` link),
and a single, named, permanent-delete confirmation (from NN/g and Primer). The main uncertainty is ChatGPT's
current memory UI: OpenAI's help pages returned 403, so that part rests on secondary sources.

## Research brief
**Question:** what should a Memory tab in a Connapse container look like so people can browse, search, check,
correct and delete agent facts, and actually use it?

**Sub-questions:** (1) how ChatGPT and Claude show and edit memory; (2) how memory platforms (Mem0, Zep, Letta)
show facts, sources and history; (3) how Notion AI, Gemini and Copilot show memory and apply permissions;
(4) what general UX guidance says about inline vs side-panel editing, delete confirmation, version history,
timestamps, empty states and provenance; (5) which Connapse page patterns to reuse so the tab feels native.

**Out of scope:** the memory storage model (settled in the 2026-10-27 report), agent-side memory tools, chat UI.

**Success criteria:** a layout and an interaction design for the tab, each choice traced to a source.

## Findings: ChatGPT and Claude memory UIs
Consumer assistants manage memory as a flat list or a prose profile with edit and delete, and show no per-item
source, date or history.
- **Claude** (Anthropic help centre, 2026-08 memory release): memory is a list of topics, each an editable file
  the person can change or delete. It also offers "Reset memory" (permanent), "Pause memory", memory scoped per
  project, and incognito chats that write no memory. Items show no source chat and no date.
- **ChatGPT**: since 2026-06 memory is a prose profile grouped by category (the "Dreaming" feature), with "Make a
  correction" and "Don't mention this again" actions. The earlier saved-memories UI was a modal list with a trash
  icon on each row, a "Memory updated" marker in chat, and a "Clear memory" confirmation. Confidence is medium:
  help.openai.com returned 403, so this rests on press coverage and screenshots.
- Neither product offers undo or a browsable version history.

## Findings: Mem0, Zep and Letta
Memory platforms store provenance and history in their data models, but mostly show them only through APIs.
- **Mem0 OpenMemory** (open-source UI, read from source at mem0 commit 96b31c4; removed from the monorepo
  2026-07-29): a table with the columns Memory, Categories, Source App and Created On, plus a row menu (Pause,
  Archive, Edit in a dialog, Delete in red). It has a "Search memories..." box, a filter popover with a "Show
  Archived Memories" toggle, sortable columns and bulk actions on checked rows. The detail page shows the text,
  "Created by <app>", an access log and related memories. Its empty states are short sentences ("No memories
  found").
- **Mem0 Platform**: its history API returns ADD/UPDATE/DELETE events with the old and new memory and the input
  that caused the change (docs.mem0.ai/api-reference/memory/history-memory). The changelog records an edit side
  drawer and "memory action badges" (2025-06). No page documents its delete flow.
- **Zep/Graphiti**: each fact keeps a list of its source episodes and four dates: when it became true, when it
  stopped, when it was recorded and when it was retired. A correction invalidates the old fact and creates new
  ones (help.getzep.com/facts). Editing and deleting are SDK-only. The dashboard has a graph view and a read-only
  "Ask graph" chat that cites the facts it used.
- **Letta**: the old Agent Development Environment had a side panel for editing core memory blocks and searching
  archival memory, but it has been deprecated (docs.letta.com/guides/ade/usage). Its block model separates
  "detach" from one agent and "delete everywhere", and a `read_only` flag stops agents, but not people, from
  editing a block.

## Findings: Notion AI, Gemini and Copilot
Notion keeps memory as an ordinary editable database and says memory grants no new access. Gemini and Copilot use
plain lists with delete on each row.
- **Notion AI** (notion.com/help/view-your-notion-agent-memories): memories live in a normal Notion database
  ("edit or remove anything you don't want your agent to keep") next to an "About" summary page. They are private
  to their owner and grant no new access, so agents use only content the person can already see. Notion's
  engineering blog describes correction links named `supersedes` and `conflicts_with` (single source, possibly an
  internal tool).
- **Notion permissions**: enterprise search checks permissions "at query time, not just during indexing", and a
  connected Drive file appears only to people who can open it in Drive.
- **Gemini**: an instructions list with "Add +" and a per-item menu holding Edit and Delete. Its docs admit that
  asking it to forget "doesn't always work".
- **Copilot**: a "Manage saved memories" list with a delete icon on each row and a "Delete all memories" button.
  In Microsoft 365, deleted memories can come back from chat history within 30 days, which is the failure to avoid.

## Findings: general UX guidance
UX authorities agree on four points: edit complex records in a panel, confirm only irreversible actions and name
the item, show history as old next to new, and give every empty state a next step.
- **Editing:** Atlassian's design system suggests inline editing for frequently changed single values. PatternFly
  suggests a modal or panel for edits with several fields. A correction changes a value, a kind, dates and a
  reason, so it belongs in a panel.
- **Delete confirmation:** NN/g says to confirm only actions that can't be undone, to name the item, and to label
  buttons by outcome ("Delete fact" / "Keep fact"). Primer says one item gets a simple dialog, type-to-confirm is
  for high-impact bulk deletes, and the dialog should say the action is permanent. GOV.UK reserves the red warning
  button for the final confirm.
- **History:** Wikipedia's diff view puts old on the left and new on the right, with author, time and edit
  summary. Google Docs lists versions in a side panel.
- **Timestamps:** Primer uses relative time ("3 days ago") under about a month and absolute dates after that, and
  warns against putting the full date only in a hover tooltip.
- **Empty states:** NN/g says to tell "nothing here yet" apart from "no matches", explain how the area fills, and
  never show an empty state while loading.
- **Provenance:** Microsoft's Human-AI guidelines G9 (support efficient correction), G11 (make clear why the system
  did what it did) and G16 (convey the consequences of user actions). Google PAIR says to show data sources and
  reveal detail progressively.

## Findings: Connapse's own patterns
Connapse's container page already has every building block the tab needs, so the tab adds no new visual
vocabulary.
- `FileBrowser.razor` is the container page. Its tabs (Files, Settings) are Bootstrap `nav-tabs`. Files appear in a
  `.file-table` with a right-hand `.detail-panel` (label/value rows), and destructive actions open a centred
  `.modal-content-custom` dialog with a short explanation and a red final button.
- Viewers (`IsInRole("Viewer")`) get no edit buttons. Errors show as a dismissible `alert-danger`.
- The activity log is the audit log at `/api/v1/audit` (admin-only). Memory writes already record
  `memory.remembered`, `memory.corrected` and `memory.forgotten` with ids, never content.

## Conflicts and uncertainties
- ChatGPT's current UI could not be checked against OpenAI's own help pages (403).
- Mem0's delete is soft by its schema (`deleted_at`) but this was not confirmed in its delete code.
- Notion's `supersedes` links come from one blog post that may describe an internal tool.
- Whether to show relative or absolute times: Primer prefers relative for recent items, Wikipedia-style history
  needs absolute ones. The tab uses both (relative in the list, absolute in the panel and history).

## Gaps — what we did not find
- No surveyed product shows each memory's sources and validity dates in its UI.
- No product documents a version-history view for a single memory that people can browse.
- No product hides memories from a person based on the permissions of their sources. Notion trims search results by
  permission but treats memories as private to their owner.

## Source quality assessment
UX guidance rests on primary design-system and research sources (NN/g, Primer, Atlassian, PatternFly, GOV.UK,
Microsoft HAX, Google PAIR). Mem0's UI rests on its own source code, which is primary but no longer maintained.
Claude, Notion, Gemini, Copilot, Zep and Letta rest on their own docs. ChatGPT rests on secondary sources.

## What Connapse adopts
- **A third "Memory" tab** on the container page, in the same table, detail panel and modal styles as Files.
- **A table** with Fact (subject, attribute, value), Sources, "True from" and "Recorded" columns, and a search box
  using the same recall the agents use (Mem0 OpenMemory columns, Connapse file table).
- **A Current/All filter.** Current shows the facts true now. All adds ended and corrected facts, labelled
  "ended" or "corrected" (Mem0's archived toggle, Zep's invalidated facts).
- **A detail panel** showing the value, status, author (person or agent icon), sources with document titles, both
  kinds of date as absolute dates, the reason, and a history list of every version, newest first, each with what
  it replaced (Wikipedia, Google Docs, HAX G11, PAIR).
- **Correct in the panel.** The old sources are pre-filled so the new fact isn't hidden as author-only, there is a
  choice between "It changed on [date]" and "It was wrong", and an optional reason. The old fact is kept and
  closed, never overwritten (Zep, Notion `supersedes`, PatternFly).
- **Delete with one named confirmation** that says the delete is permanent, removes every version, and leaves an
  entry in the activity log without the content. The final button is red and reads "Delete fact" (NN/g, Primer,
  GOV.UK). There is no undo and nothing comes back, unlike Copilot.
- **Separate empty states** for "no facts yet" (explaining that agents add facts through the memory tools) and
  "no matches" (with a Clear search link). Neither shows while loading (NN/g).
- **The same rules as agents.** The tab calls the same memory operations as the MCP tools, so a person sees only
  facts whose every source they can read, and sourceless facts only if they wrote them (Notion's query-time
  check, the 2026-10-27 design).

## Sources
- Primary: docs.mem0.ai (history API, changelog, filters, update/delete);
  github.com/mem0ai/mem0 at 96b31c4 (`openmemory/ui`, `openmemory/api/app/models.py`); help.getzep.com (facts,
  deleting data, graph visualizer, changelog); github.com/getzep/graphiti (`graphiti_core/edges.py`);
  docs.letta.com (ADE usage, memory blocks, passages API, MemFS); notion.com/help (memories, Notion agent, AI
  FAQs, enterprise search security, Slack and Drive connectors); support.google.com/gemini/answer/16598625;
  support.microsoft.com (Copilot memory, privacy controls); support.anthropic.com (Claude memory);
  nngroup.com (confirmation dialogs, empty states); primer.style (dialog, relative time);
  atlassian.design (inline edit); patternfly.org (modal); design-system.service.gov.uk (button);
  microsoft.com/haxtoolkit (guidelines); pair.withgoogle.com (guidebook); mediawiki.org (diff help);
  support.google.com/docs (version history).
- Secondary: notion.com/blog/building-shared-memory-for-ai-agents-in-notion (single source); press coverage of
  ChatGPT memory (help.openai.com returned 403).
