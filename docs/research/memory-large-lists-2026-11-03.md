# How do established tools keep very large lists of notes, issues and memories fast and usable?
**Date:** 2026-11-03
**Status:** Reviewed
**Built on:** memory-spaces-2026-11-03.md (how tools list personal and shared spaces). Connapse MCP was not available
in this session.

## Executive summary
Notion, Linear, Obsidian and the agent-memory services mem0, Zep and Letta all keep big lists usable the same way:
show a small first batch (25 to 100 items), let people narrow it with search, filters and grouping, and fetch more
only when asked. Their APIs mostly page with an opaque cursor rather than a page number (mem0 is the exception: its
v2 `get_all` takes `page` and `page_size`), cap page size at 100 to 250, and mostly avoid returning a total; mem0 and
Zep's user list are the exceptions there. Favourites and collapsible sections
keep long lists of spaces manageable. No product publishes a latency target for its list screens; the only published
numbers are search latencies from mem0 (0.148 s median, 0.200 s at the 95th percentile) and Zep (under 200 ms at the
95th percentile, a marketing claim). The main uncertainty is how each product's own dashboard pages, which is barely
documented for the memory services.

## Research brief
**Question:** How do Notion, Linear, Obsidian and agent-memory dashboards (mem0, Zep, Letta) handle very large lists,
and what should Connapse's Memory page copy?

**Sub-questions:**
1. Notion: database views, sidebars and the API at scale.
2. Linear: issue lists, grouping, sidebars, history and the API.
3. Obsidian: file trees, search and Bases with many notes.
4. mem0, Zep and Letta: memory list endpoints, filters, history and any dashboard behaviour.

**Out of scope:** search ranking quality, mobile layouts, pricing.

**Success criteria:** concrete patterns (page sizes, paging style, counts, grouping, lazy loading) with sources, and
any published speed targets.

## Findings by sub-question

### Notion: load a set number of rows, then "Load more"
Notion database views load a limit the user picks (10, 25, 50 or 100 rows) and show "Load more" below; this shipped
in 2020 (Notion release notes, primary). In 2022 Notion changed databases to "only load what you need", which it says
made databases over 1,000 rows about 2.5 times faster (Notion release notes, primary). Its help pages set a limit of
250,000 rows per database and advise filtering on simple properties, such as Created time, to keep views fast
(primary). Sidebar sections show 5 to all items, with the rest behind a "More" pane that has its own search and a
sort by manual order or last edited; sections collapse (Notion help, primary). The API pages with `start_cursor`,
returns `next_cursor` and `has_more`, defaults and caps `page_size` at 100, returns no total, stops at 10,000 results
per query with `request_status: incomplete`, limits clients to about 3 requests a second, and supports nested AND/OR
filters (Notion API reference, primary). Footer calculations such as Count exist in views; whether they are exact on
huge databases is not documented.

### Linear: load locally, group, and collapse old activity
Linear syncs a workspace's issues into the browser and loads them in tiers, with some objects fetched only when
requested (engineering talks and reverse-engineering write-ups, secondary). Its docs confirm that archived issues are
not kept in the client and load on demand (primary). Display options group issues by many fields with sub-groups and
sticky group headers that show a count or estimate total, and order by several fields (Linear docs, primary).
Favourites and collapsible, remembered sidebar sections keep long team and project lists in check (primary). The
GraphQL API uses Relay-style cursors with a default page of 50 and orders by `createdAt` or `updatedAt` (primary); a
maximum of 250 per page appears only in third-party guides. Since April 2025 an issue's history groups similar
events and collapses older activity behind a link (changelog, primary). Linear publishes no latency target.

### Obsidian: local search with counts, Bases with limits and grouping
Obsidian runs entirely on the user's machine. Version 1.8.8 improved file tree performance with many items
(changelog, primary); whether it virtualizes the tree is not stated. Search supports operators, sorting, a total
match count and collapsible results per file (help, primary). Bases, its database view, offers a user-set results
limit, a result count, grouping by one property into collapsible groups, several sort keys and all/any/none filter
groups (help, primary). File recovery keeps snapshots for 7 days by default. All scale figures, such as 35,000 files
indexed in about 10 minutes, come from users on the forum (tertiary).

### mem0, Zep and Letta: paged list endpoints, full histories
mem0's v2 `get_all` pages with `page` and `page_size` (default 100, maximum 200) and returns `count`, `next` and
`previous`; filters nest AND, OR and NOT; search takes `top_k` from 1 to 1,000; a memory's history comes back as one
unpaged array (mem0 API reference, primary). The mem0 paper reports search at 0.148 s median and 0.200 s at the 95th
percentile (arXiv 2504.19413, primary). Zep pages with a cursor in a `Zep-Next-Cursor` header, sorts by `created_at`,
`valid_at` and other fields, returns `total_size` on its user list, and claims under 200 ms at the 95th percentile for
retrieval (docs primary, latency claim marketing). Letta pages with `after` and `before` cursors and `limit` up to 200
and has a search parameter (API reference, primary). None of the three documents how its web dashboard pages, counts
or groups memories.

## Conflicts and uncertainties
- Linear's page-size maximum of 250 rests on third-party guides only.
- Linear's loading tiers come from talks and outside write-ups, not Linear's docs.
- Whether Notion's footer counts are exact or estimated at large sizes is undocumented.
- Totals split the field: Notion, Letta and Linear's API return none; mem0 returns `count` and Zep returns
  `total_size` on one endpoint. Connapse follows the cursor-and-no-total shape for facts because a per-viewer total
  is expensive, and offers the count separately.

## Gaps — what we did not find
- Any published latency target for a list or dashboard screen, from any of the six products.
- How mem0, Zep or Letta dashboards present thousands of memories.
- Whether Obsidian's file tree or Notion's large views use virtual scrolling.

## Source quality assessment
Most findings rest on primary sources: release notes, help centres, changelogs and API references. Linear's sync
engine details are secondary, Obsidian's scale numbers are tertiary, and Zep's latency figure is marketing.

## Sources
**Primary:** Notion release notes (2020 load limits; 2022 "only load what you need"); Notion help (database limits,
sidebar); Notion API reference (pagination, query a database, request limits); Linear docs (display options,
favourites, sidebar, archived issues); Linear changelog (April 2025 issue history); Linear GraphQL API docs
(pagination); Obsidian changelog 1.8.8; Obsidian help (search, Bases, file recovery); mem0 API reference (get all
memories v2, search, history); mem0 paper, arXiv 2504.19413; Zep API reference (pagination, users); Letta API
reference (list passages and messages).
**Secondary:** talks and write-ups on Linear's sync engine.
**Tertiary:** Obsidian forum reports on large vaults; third-party Linear API guides.
