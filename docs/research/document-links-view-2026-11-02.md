# How should Connapse show what a document links to and what links to it?
**Date:** 2026-11-02
**Status:** Reviewed
**Built on:** no prior corpus material (the Connapse knowledge base was not reachable from this session)

## Executive summary
Every tool studied puts links and backlinks next to the document, not on a page of their own: Obsidian and VS Code in a
side pane, GitHub in the issue sidebar, Jira in a section of the issue, Logseq and Confluence at the foot of the page.
For Connapse that means a **Links section inside the file browser's existing File Details panel**, with two plain-word
lists, **"Links to"** and **"Linked from"**, each showing the first five documents and a "Show N more" link. A link the
reader may not open is left out entirely, with no placeholder and no count, which matches Jira, Notion and SharePoint
and Connapse's own fail-closed per-document rule. A graph view is not worth building for v1: the research shows
node-link diagrams stop being readable past roughly 50 nodes, and Obsidian users say they navigate by the backlinks
list, not the graph. The weakest evidence is on wording: no study compares "Links to / Linked from" against other
labels.

## Research brief
**Question:** How do established tools present a document's outgoing links and backlinks, and where should that live
in Connapse's file browser so it helps without bloating the page?

**Sub-questions:**
1. How do note tools (Obsidian, Logseq, Notion) show backlinks?
2. How do developer tools (Sourcegraph, GitHub, VS Code) show references and "used by"?
3. How do work trackers (Jira, Confluence) show linked items, and what do they do with items a viewer can't see?
4. What do UX guidelines (NN/g, Baymard, design systems) say about side panels, long lists, empty states and labels?
5. Where in Connapse does this belong, given the link model built in CON-E3?

**Out of scope:** graph visualisation beyond deciding whether to build one; editing links by hand (Connapse derives
every link from content and never writes one on a user's behalf); search ranking by links (CON-E3 already did it).

**Success criteria:** a placement decision and a v1 feature list small enough not to feel bloated, each choice traced
to a source.

## Findings by sub-question

### 1. Note tools show backlinks beside or below the note, collapsed or behind a count
- **Obsidian** shows Backlinks as a tab in the right sidebar, with an option to show the same list at the foot of the
  note. It has two sections, "Linked mentions" and "Unlinked mentions" (name appears but isn't linked), grouped by
  source note with snippets, plus controls to collapse snippets, sort and filter. Outgoing links are a separate core
  plugin with its own sidebar tab. (obsidian.md/help/plugins/backlinks, obsidian.md/help/plugins/outgoing-links)
- **Logseq** puts "Linked References" and "Unlinked References" at the foot of every page. Unlinked ones are always
  collapsed; linked ones collapse above a configurable size threshold (forum sources only). Users asked for a way to
  hide the sections because of "the clutter of seeing the reference sections". (github.com/logseq/docs,
  discuss.logseq.com threads 778, 16127, 26613)
- **Notion** shows a "{#} backlinks" count near the title; clicking it opens the list. Only backlinks from pages the
  viewer can access are listed, and nothing renders when there are none. (notion.com/help/create-links-and-backlinks)
- Pattern: the default view is a count or a collapsed list, confirmed links are kept apart from guessed ones, and
  people complain when backlinks take too much room or are too far from where they read.

### 2. Developer tools offer a compact summary in place and the full list elsewhere
- **VS Code** Peek References shows results inline without leaving the file; Find All References opens a side-bar view;
  Call Hierarchy is one tree with a toggle between incoming and outgoing calls. (code.visualstudio.com/docs/editing/editingevolved,
  code.visualstudio.com/updates/v1_29, code.visualstudio.com/docs/cpp/cpp-ide)
- **Sourcegraph** opens references from a hover into a bottom panel, shows precise (compiler-indexed) results before
  search-based guesses, and truncates past 500 results. (sourcegraph.com/docs/code-search/code-navigation/features)
- **GitHub** shows linked pull requests in an issue's Development sidebar section and incoming references as timeline
  entries; "Used by" is a sidebar counter that opens a full Dependents tab. Caps: 10 manual links, 100 sub-issues.
  A reference from a private repository appears with "the details omitted". (docs.github.com, linking-a-pull-request-to-an-issue,
  autolinked-references-and-urls, creating-an-issues-only-repository, about-the-dependency-graph)
- Pattern: small in the sidebar, everything one click away; confident links ranked above guesses; hard caps on lists.

### 3. Work trackers use paired direction phrases and omit what the viewer can't see
- **Jira** lists linked issues in a section of the issue, grouped by paired phrases such as "blocks" / "is blocked by",
  with small rows (type, key, title, status). Issues the viewer can't see are omitted with no placeholder.
  (support.atlassian.com/jira-software-cloud/docs/link-issues/, .../configure-issue-linking/)
- **Confluence** hides empty sections; its incoming links sit in Page Information, a separate screen, and users file
  requests to bring them onto the page (CONFCLOUD-35218, CONFCLOUD-54243; confluence.atlassian.com/doc/view-page-information-139408.html).
  That is the clearest evidence against a separate page.
- Long lists: Atlassian documents no threshold; a third-party Jira app collapses a group past 3 items (deviniti.com).
- **Microsoft 365 Copilot / SharePoint** results are "always security trimmed": items the user can't see are left out.
  (learn.microsoft.com/sharepoint/make-site-content-searchable, sharepoint-copilot-best-practices)

### 4. UX guidance: a collapsible section, a short list with "Show N more", a one-line empty state
- Progressive disclosure: show the few most important items first and the rest on request (nngroup.com/articles/progressive-disclosure/).
  Accordions cost a click, so use them only when people need a few pieces; keep their open state and give them
  descriptive headings (nngroup.com/articles/accordions-complex-content/).
- Truncation: under about 6 items users assume the list is complete; up to 10 performs decently; label the control
  with what it reveals (baymard.com/blog/truncation-design). Carbon recommends "Show more"/"Show less" over fades or
  scrolling (carbondesignsystem.com/patterns/overflow-content); Workday Canvas and Vercel Geist put the hidden count in
  the button.
- Empty states: "Do not default to totally empty states"; say briefly what could appear, and don't say "nothing" while
  still loading (nngroup.com/articles/empty-state-interface-design/). Atlassian: one to two sentences
  (atlassian.design/foundations/content/designing-messages/empty-state).
- Labels: the link label is how people predict where it goes (nngroup.com/articles/information-scent/). MediaWiki's
  long-standing "What links here" is the closest precedent for plain direction words (mediawiki.org/wiki/Help:What_links_here).
- Graphs: people struggle to find paths in node-link diagrams past ~50 dense or ~100 sparse nodes (Yoghourdjian et al.,
  IEEE InfoVis 2020, arxiv.org/abs/2008.07944); a practitioner study quotes "Then it turns into a hairball"
  (arxiv.org/pdf/2304.01311). Obsidian forum users call the global graph "mostly a poster".
- Hidden items: OWASP says deny by default and don't let a probe tell "missing" from "forbidden"
  (cheatsheetseries.owasp.org Authorization Cheat Sheet).

### 5. Connapse: the File Details panel is the place, and container links today come from code
- Connapse's link layer (CON-E3) stores nodes, the documents that stand for them, and typed links asserted by
  documents; `ILinkReader` shows a node or link only when a readable document vouches for it.
- In a container (the only kind of storage the file browser opens), links come from code files: imports, calls and
  inherits between files, scoped to that container. Jira, Confluence and GitHub links live in sources, which are not
  browsable and have no in-app view, so a link to a source document could not be clicked through.
- The file browser already opens a File Details side panel when a file is selected; that is where "what links here"
  belongs, the way Obsidian, GitHub and VS Code put it in a sidebar.

## Recommendation (v1)
1. A **Links** section in the File Details panel, below the existing details. No new page, no graph.
2. Two lists, **"Links to"** and **"Linked from"**, each row a file name that opens the file (switching folders if
   needed), its folder in muted text, and the link kinds in direction words: calls / called by, imports / imported by,
   inherits from / inherited by. Other kinds fall back to their raw name.
3. First **5** rows per list, then **"Show N more"** (and "Show less").
4. Documents the reader can't open are omitted, and are not counted. "Defines" links are left out: they connect a file
   to its own symbols and to partial definitions of the same type, which is structure, not a link a reader follows.
5. Empty state: one muted line, "No links found for this file." shown only after loading, rather than hiding the
   section, so people learn the feature exists (NN/g). Notion and Confluence hide it; we follow NN/g because the panel
   is the only place the feature is discoverable.
6. Deferred: snippets per link (Obsidian, Logseq), confidence ranking (Sourcegraph), a local graph, links to source
   documents once they have an in-app view.

## Conflicts and uncertainties
- **Placeholder vs omission for hidden targets.** GitHub shows a redacted reference; Jira, Notion and SharePoint omit.
  We omit: a placeholder reveals that a document exists and links here, which Connapse's per-object rule forbids.
- **Empty state vs nothing.** Notion and Confluence render nothing; NN/g and Atlassian's design system say show a short
  message. We show one line, for discoverability.
- **Collapse threshold.** Logseq's default (reportedly 100) and the Jira app's 3 disagree; Baymard's 6–10 range is the
  only tested evidence. We use 5 visible, which sits at the low end and keeps the panel short.

## Gaps — what we did not find
- No usability study compares direction labels ("Links to / Linked from" vs "Outgoing / Backlinks").
- No primary source on putting a count in a section header.
- Obsidian's per-section counts, Sourcegraph's grouping by repository and file, and Notion's display settings are
  described from memory or mirrors, not current official docs.
- Roam Research was not studied.

## Source quality assessment
Most findings rest on primary sources: official product documentation (Obsidian, Notion, GitHub, VS Code, Sourcegraph,
Atlassian, Microsoft), NN/g and Baymard research, design-system guidance (Carbon, Atlassian) and a peer-reviewed
InfoVis paper. Logseq's collapse behaviour and all clutter complaints come from community forums (secondary). The
recommendation does not depend on any single secondary source.

## Sources
**Primary:** obsidian.md/help/plugins/backlinks; obsidian.md/help/plugins/outgoing-links; github.com/logseq/docs;
notion.com/help/create-links-and-backlinks; code.visualstudio.com/docs/editing/editingevolved;
code.visualstudio.com/updates/v1_29; code.visualstudio.com/docs/cpp/cpp-ide;
sourcegraph.com/docs/code-search/code-navigation/features; docs.github.com (linking-a-pull-request-to-an-issue,
autolinked-references-and-urls, creating-an-issues-only-repository, adding-sub-issues, about-the-dependency-graph);
support.atlassian.com/jira-software-cloud/docs/link-issues/; support.atlassian.com/jira-cloud-administration/docs/configure-issue-linking/;
confluence.atlassian.com/doc/view-page-information-139408.html; confluence.atlassian.com/confcloud/page-restrictions-724764891.html;
learn.microsoft.com/sharepoint/make-site-content-searchable; learn.microsoft.com/sharepoint/sharepoint-copilot-best-practices;
nngroup.com/articles/progressive-disclosure/; nngroup.com/articles/accordions-complex-content/;
nngroup.com/articles/empty-state-interface-design/; nngroup.com/articles/information-scent/;
baymard.com/blog/truncation-design; carbondesignsystem.com/patterns/overflow-content;
atlassian.design/foundations/content/designing-messages/empty-state; mediawiki.org/wiki/Help:What_links_here;
cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html; arxiv.org/abs/2008.07944.

**Secondary:** Atlassian issue tracker (JRASERVER-60636, JRACLOUD-84597, JRACLOUD-97455, CONFCLOUD-35218,
CONFCLOUD-54243); community.atlassian.com grouping thread; deviniti.com display-related-issues;
discuss.logseq.com (111, 778, 13281, 16127, 26613); forum.obsidian.md (71316, 83894); eleanorkonik.com on graph view;
arxiv.org/pdf/2304.01311; canvas.workday.com/v8/patterns/overflow; vercel.com/geist/show-more;
forum.buildkite.community thread 696.
