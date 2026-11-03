# How do established tools organize personal and shared spaces for notes and AI memory?
**Date:** 2026-11-03
**Status:** Reviewed
**Built on:** personal-team-memory-2026-11-02.md (how products scope memory facts and how agents choose a scope).
Connapse MCP was not available in this session.

## Executive summary
Every tool studied lists named spaces (projects, notebooks, teamspaces, workspaces) in one place and marks which are
personal and which are shared, but they disagree on when that is decided. Document tools with whole spaces
(Microsoft Loop, Confluence) treat personal and shared spaces as different kinds and can't convert one into the
other; ChatGPT, Claude, Notion pages, Slack canvases and Basic Memory start private and widen access later by
sharing. Read-only members everywhere can read and use a shared space but not change it; Microsoft's Copilot
Notebooks and Loop workspaces have no read-only member at all. None of the named-space products in this run pools
agent-written memory for members: in ChatGPT and Claude shared projects it stays per person or walled off. The
closest precedent is still GitHub Copilot Memory's repository facts (2026-11-02 report), which are tied to a repo.
Where notes are attached to another object (a Loop workspace in a Teams channel, a Slack channel canvas),
membership comes from that object. Basic Memory, an MCP memory server, makes agents name a project on every call
and refuses to guess when several exist.

## Research brief
**Question:** how do established tools organize personal versus shared memory or notes spaces, so Connapse can
design memory spaces on a nav-level Memory page?

**Sub-questions:** (1) how document and notes tools (Notion, Confluence, Slack, Loop) create, share and convert
personal and shared spaces; (2) how AI products (ChatGPT, Claude, Copilot Notebooks, NotebookLM, Basic Memory,
Mem0, Zep, Letta) organize memory into named spaces and how an agent picks one.

**Out of scope:** per-fact scope and agents' privacy judgment (covered 2026-11-02), implementation.

**Success criteria:** precedents for space creation, the personal/team choice, sharing and roles, attached versus
standalone spaces, and agent selection, each with a source.

## Findings: personal and shared spaces in document and notes tools
Document tools keep personal and shared spaces as separate kinds, and the ones with whole spaces don't convert them.
- **Notion:** new pages land in the sidebar's Private section ("only visible to you"). Teamspaces are made with "+"
  and are Open, Closed or Private; members are people, groups or an invite link, and a "default" teamspace adds the
  whole workspace. Moving a page into a teamspace shares it; dragging a shared page into Private "will remove
  everyone else's access". Teamspace roles are Owner and Member; page access is Full access, Can edit, Can comment
  or Can view, where Can view can't comment. The sidebar shows Teamspaces, Shared and Private sections.
  (notion.com/help/intro-to-teamspaces, /sharing-and-permissions, /navigate-with-the-sidebar)
- **Confluence Cloud:** a personal space is created automatically or from the profile menu; a team space comes from
  Create > Space and needs the Create Space permission. There is no conversion (a long-open suggestion, CONF-29898,
  asks for one). Roles are Admin, Manager, Collaborator and Viewer, and a Viewer can comment. Personal spaces are not
  private by default. (support.atlassian.com/confluence-cloud: create-a-space, what-are-space-permissions)
- **Slack canvases:** a new canvas is invite-only and widened through Share; access levels are View, Comment, Edit
  and Owner. Since 9 April 2025, channel canvases are owned canvases shown as channel tabs, viewable by everyone in
  the channel, with edit rights following the channel's posting permission. (slack.com/help/articles/15678967614611,
  /21290478840979)
- **Microsoft Loop:** every user has one "My workspace", made on first use and private; it can't be shared as a
  whole, only page by page. Shared workspaces have Owners and Members, and all members can edit. A workspace made in
  a Teams channel takes the channel's members, which can't be edited separately.
  (learn.microsoft.com/microsoft-365/loop/loop-permission, support.microsoft.com/loop/loop-workspaces-in-teams-channels)

## Findings: named memory spaces in AI products, and how agents pick one
AI products list projects on one page and decide sharing later; agent-written memory is never pooled for members.
- **ChatGPT Projects:** projects sit in the sidebar. Default or project-only memory is chosen at creation; sharing
  forces project-only memory permanently, and shared projects never see a member's own memory.
  (academy.openai.com projects resource; help.openai.com/en/articles/10169521 via search snippets, page returned 403)
- **Claude Projects:** a projects page with "Your projects / Organization / Shared with you" tabs. A project is
  private or shared with the organization; members "Can view" (chat, no edits) or "Can edit". Each project has its
  own memory layered over account memory, and owners can't view members' memories.
  (support.claude.com articles 9519177 and 11817273)
- **Microsoft 365 Copilot Notebooks** (now Copilot Workspaces): stored in the creator's storage; every invitee can
  edit; chats in a shared notebook stay private to each user. (support.microsoft.com Copilot Notebooks FAQ)
- **NotebookLM:** Viewers read sources and notes; Editors add sources and reshare; chat history is private. No
  memory feature. (support.google.com/notebooklm/answer/16206563)
- **Basic Memory:** Personal and Team workspaces hold projects; a team project is Team (default) or Invite-only and
  can be changed later. Viewers are read-only. Every MCP tool takes a `project`; calls that omit it are refused when
  several projects are reachable, and search can span all of them. (docs.basicmemory.com teams and MCP tool reference)
- **Developer APIs:** Mem0 scopes every call by `user_id`, `agent_id`, `app_id` or `run_id` and has a READER role
  that can view and search; Zep keeps shared graphs apart from user graphs and the app searches both; Letta shares a
  memory block across agents with a `read_only` flag. (docs.mem0.ai, help.getzep.com, docs.letta.com)

## Conflicts and uncertainties
- **When personal versus shared is decided** is split: fixed at creation in Loop and Confluence, widened later in
  ChatGPT, Claude, Notion, Slack and Basic Memory. Connapse's plan picks fixed, for agent clarity; that is a design
  choice, not a consensus.
- Whether facts saved in a Claude or ChatGPT shared project are visible to other members is not stated officially.
- Loop's owner count, Copilot Notebooks' group sharing and Basic Memory's role names conflict between pages.
- ChatGPT's project help page was read only through search snippets.

## Gaps — what we did not find
- No named, standalone space of agent-written memory that every member reads, which is what a Connapse team space
  is. GitHub Copilot's repository memory (2026-11-02 report) is shared but tied to a repository, like container memory.
- No measurement of whether agents choose a space more accurately with a required parameter.
- No official default access level for a new Notion teamspace.

## Source quality assessment
Almost every claim rests on the vendors' own help centres and API docs (primary). ChatGPT's project article and
some Loop and Copilot details rest on search snippets or community answers and are marked above.

## Sources
- Primary: notion.com/help; support.atlassian.com/confluence-cloud; slack.com/help; learn.microsoft.com and
  support.microsoft.com (Loop, Copilot Notebooks); support.claude.com; academy.openai.com; help.openai.com (snippets);
  support.google.com/notebooklm; docs.basicmemory.com; github.com/modelcontextprotocol/servers (memory server);
  docs.mem0.ai; help.getzep.com; docs.letta.com.
- Secondary: jira.atlassian.com CONF-29898; community.openai.com; Microsoft Tech Community and Q&A answers.
