# How should agents keep personal and team memory apart in Connapse, and how should both show in one place?
**Date:** 2026-11-02
**Status:** Reviewed
**Built on:** agent-memory-design-2026-10-27.md (fact model and the all-sources permission rule),
memory-tab-ux-2026-11-02.md (the Memory tab this extends). Connapse MCP was not available in this session.

## Executive summary
Almost every product makes memory an agent writes private by default and needs a deliberate step to share it.
GitHub Copilot Memory is the closest match to what Connapse needs: it has one personal scope and one repository
scope, the agent picks between them by asking who the fact is true for, and it asks the user when unsure.
Connapse today has no scope at all: a fact's audience falls out of the documents it cites, so an agent can't
choose. Research on language models' privacy judgment (ConfAIde, PrivacyLens) shows models misjudge what is safe
to share in 25–57% of cases even when instructed. So the scope should be a required choice the agent states, a
team write should be echoed back in plain words, and a person should be able to see and change the scope of
every fact. The main uncertainty is that no study measures which tool shape (required parameter, default or
separate tools) makes agents choose most reliably; the guidance there is vendor practice, not measurement.

## Research brief
**Question:** how should Connapse let agents save each memory fact as personal (private to a user and their
agents) or team (shared with a container's readers) on purpose, and show both in one Memory tab?

**Sub-questions:** (1) how products model personal and shared memory scope; (2) how agents are told to choose a
scope, and how reliably models classify private information; (3) UI patterns that show private and shared items
in one list; (4) risks of shared memory; (5) everyday examples of team memory helping.

**Out of scope:** the fact storage model and correction history (settled 2026-10-27), the Memory tab's table and
detail panel (settled 2026-11-02), implementation.

**Success criteria:** a scope model, a tool shape and a UI shape for Connapse, each traced to a source.

## Findings: how products scope personal and shared memory
Products keep agent-written memory private by default; shared memory is either written by people or fenced by
write access, review and expiry.
- **GitHub Copilot Memory**: repository facts are shared with everyone using Copilot on that repo, but only users
  with write access cause them to be created; user preferences are personal. The agent decides: "Something that
  would be true for anyone working in the repository becomes a repository-level fact", and asks when intent is
  ambiguous. Repo facts cite code, are checked before use, expire after 90 days by default, and owners can review
  and delete them (docs.github.com/en/copilot/concepts/agents/copilot-memory).
- **Claude Code**: shared memory is the human-written `CLAUDE.md` in git; user and `CLAUDE.local.md` files are
  personal; auto memory the agent writes is "machine-local" (code.claude.com/docs/en/memory). No promote step.
- **ChatGPT shared projects** use "project-only memory" shared by collaborators and exclude personal memory
  (OpenAI Academy); that memory can't be browsed or edited, which users complain about (community.openai.com).
- **Personal only**: Glean assistant memory, Slack AI, Microsoft 365 Copilot, and Copilot Studio agents ("a
  separate memory store for every user… never shared"; disabled in group chats).
- **Cursor, Windsurf/Devin**: shared rules live in git or are set by admins; agent-generated memories are local
  (Cursor removed its Memories in 2.1.x per staff forum posts).
- **Frameworks**: Mem0 tags memories with `user_id`, `agent_id`, `app_id`, `run_id`, set by the developer; Zep
  separates user graphs from shared context graphs; Letta shares a memory block only when a developer attaches it
  to several agents; CrewAI is the one shared-by-default design ("Agents can use the crew's shared memory
  (default)"), with no per-user access control.
- **No product offers "promote this private memory to shared"** as an action; sharing is a choice made at write
  time or a person moving text into a shared file.

## Findings: how an agent should choose, and how reliable that choice is
Tool design guidance favours one tool with an enum parameter and a clear description, but models' privacy
judgment is poor enough that the choice needs a human-visible check.
- Anthropic's "Writing tools for agents" says to use enums and unambiguous names, avoid overlapping tools, and
  that small description changes improve behaviour a lot. OpenAI's function-calling guide says to use enums to
  prevent invalid states and to apply the "intern test": could a person use the tool from its description alone?
- The MCP specification says there should always be a human able to deny tool calls, and clients should confirm
  sensitive operations "to avoid malicious or accidental data exfiltration".
- ConfAIde (arXiv 2310.17884): GPT-4 reveals private information where humans would not 39% of the time, ChatGPT
  57%, and privacy prompts don't fix it. PrivacyLens (arXiv 2409.00138, NeurIPS 2024): GPT-4 leaks in 25.7% of
  agent actions even with privacy instructions. Models state privacy norms correctly but still leak when acting.
- **Gap:** no study compares a required enum, an enum with a default, and separate tools for scope accuracy.

## Findings: showing private and shared items in one list
Mixed lists work when every row carries its scope, an owner filter defaults to everything, and changing scope is
a named move whose consequences are spelled out.
- **Google Drive** has an Owner filter (Anyone by default, Owned by me, Not owned by me)
  (support.google.com/drive/answer/2375114). **Notion** shows Teamspaces, Shared and Private as labelled sidebar
  sections and warns that moving a page to Private removes others' access (notion.com/help/navigate-with-the-sidebar).
- **1Password** and **Bitwarden** treat sharing as moving an item into a shared vault; Bitwarden users are often
  surprised a share can't simply be undone (bitwarden.com/help/org-faqs). **Slack** lets only owners and admins
  make a private channel public (slack.com/help/articles/213185467).
- **GitHub Copilot** sends personal, repository and organization instructions together and ranks personal first
  on conflict; Cursor ranks team rules first. Products disagree, so Connapse must state its own rule.

## Findings: risks of shared memory
Shared memory is a proven route for one user's planted text to steer other users' agents, and pooled memory leaks
across users even without an attacker.
- MINJA (arXiv 2503.03704): ordinary queries planted malicious records 98.2% of the time and these affected other
  users' sessions. AgentPoison (arXiv 2407.12784): under 0.1% poisoned entries reached about 80% attack success.
- Microsoft's "AI Recommendation Poisoning" (2026-02-10 security blog) reports links that plant fake facts in
  assistant memory in the wild. OWASP's agentic Top 10 item ASI06 names shared-context poisoning (secondary
  sources only).
- Collaborative Memory (arXiv 2505.18279) uses a private tier plus an opt-in shared tier with provenance on every
  fragment. PiSAs (arXiv 2607.05318) and MemLeak (NeurIPS 2026) report cross-user spillage in pooled memory; both
  are recent single sources.
- Mitigations seen across sources: private default, write permission to share, author and sources shown on every
  shared fact, owner review and delete, and keeping the cited-sources rule as a ceiling.

## Findings: everyday value of team memory on a project
Shared project memory saves teammates and their agents from rediscovering the same things.
- Copilot Memory stores "coding conventions, architectural decisions, build commands, project-specific rules" for
  a repository. Notion's shared agent memory ("Lore", notion.com/blog/building-shared-memory-for-ai-agents-in-notion)
  stores gotchas, decision records, rare procedures and follow-ups so one person's agent learns from a teammate's.
- Devin Knowledge pins guidance such as "how to write DB queries in service X" to a repo
  (docs.devin.ai/onboard-devin/knowledge-onboarding). Glean users find "who the product SME is, what changed"
  (community post; Forrester's onboarding savings figure is vendor-funded).

## Conflicts and uncertainties
- Personal-versus-team precedence: GitHub ranks personal first, Cursor ranks team first.
- Whether shared Claude Projects expose project memory to every member could not be confirmed from Anthropic docs.
- AgentPoison's figures differ between paper versions (63–88%). OWASP ASI06 was read only through summaries.
- Cursor's removed Memories being visible to teammates rests on one third-party source.

## Gaps — what we did not find
- No measurement of which tool shape makes agents pick a scope most accurately.
- No product with an explicit "promote to shared" action, so its UX has no precedent to copy.
- No product that combines a per-fact scope with source-permission trimming, as Connapse would.

## Source quality assessment
Scope models rest mostly on primary vendor docs (GitHub, Anthropic, OpenAI Academy, Microsoft, Glean, Slack,
Mem0, Zep, Letta, CrewAI). Privacy judgment and poisoning rest on peer-reviewed papers. Cursor, Claude Projects
sharing and Teams details rest on forums and third-party guides and are marked low confidence.

## Sources
- Primary: docs.github.com (Copilot Memory, response customization); code.claude.com/docs/en/memory;
  academy.openai.com (projects); docs.glean.com; slack.com/help; support.microsoft.com and learn.microsoft.com
  (Copilot memory); docs.mem0.ai; help.getzep.com; docs.letta.com; langchain-ai.github.io/langmem;
  docs.crewai.com; cursor.com/docs/context/rules; docs.devin.ai; anthropic.com/engineering/writing-tools-for-agents;
  developers.openai.com function-calling guide; modelcontextprotocol.io/specification/2025-06-18/server/tools;
  arXiv 2310.17884, 2409.00138, 2503.03704, 2407.12784, 2505.18279, 2607.05318;
  microsoft.com/en-us/security/blog/2026/02/10/ai-recommendation-poisoning; support.google.com/drive;
  notion.com/help; support.1password.com; bitwarden.com/help.
- Secondary: notion.com/blog (Lore); community.openai.com; forum.cursor.com; OWASP ASI06 summaries
  (goteleport.com); spotdev.co.uk (Claude Projects); Glean community and Forrester TEI (vendor-funded).
